# Contrato: tratamiento de mensajes (venenosos, reintentos y DLQ)

Esta feature **no cambia** ningún nombre de evento ni payload. Solo se añaden convenciones sobre cómo se reprograman, se apartan y
se registran los mensajes. Aplica a los cuatro servicios que consumen: Transaction, Balance, Transfer y Notification.

## 1. Mensaje reprogramado (reintento)

Un reintento es un mensaje **nuevo** publicado en el mismo tópico (`SERVICEBUSTOPIC`) con entrega programada.

| Elemento | Valor |
|---|---|
| Cuerpo | Idéntico al original |
| `Subject` | El mismo que el original (el nombre del evento ⇒ llega a la misma suscripción) |
| `ContentType` | El mismo que el original |
| Propiedades de aplicación | Las del original más `RetryAttempt` (entero) |
| `RetryAttempt` | Intento anterior + 1; el original no la tiene (= 0) |
| Entrega | `ScheduledEnqueueTimeUtc` = ahora + espera (ver tabla) |

| Reintento | Espera nominal | Rango con variación ±20 % |
|:-:|--:|--:|
| 1 | 5 s | 4 – 6 s |
| 2 | 15 s | 12 – 18 s |
| 3 | 45 s | 36 – 54 s |
| 4 | 135 s | 108 – 162 s |
| 5 | 300 s | 240 – 360 s |

**Orden**: se programa la copia y **después** se completa el original (un duplicado es preferible a una pérdida; los consumidores
son idempotentes — ver research.md R1 y R9).

## 2. Mensaje apartado a la DLQ

| Motivo (`DeadLetterReason`) | Cuándo | Compensa con `*-failed` |
|---|---|---|
| `PoisonMessage` | Cuerpo vacío, JSON ilegible o `null`, sin `CorrelationId` o con más de 64 caracteres, o error de deserialización al procesar (campo con tipo incorrecto) | Sí, si el `CorrelationId` es legible y el servicio tiene evento de fallo en ese paso |
| `RetriesExhausted` | Un fallo transitorio con `RetryAttempt = 5` | Sí, en los primeros pasos (ver §3) |
| `CompensationFailed` | No se pudo emitir el `*-failed` al compensar | No (es la razón por la que se aparta) |

| Campo | Contenido |
|---|---|
| `DeadLetterErrorDescription` | Detalle legible, **máximo 1 024 caracteres** (se trunca) |
| Propiedad `CorrelationId` | El `CorrelationId` leído del cuerpo, si existe |
| Propiedad `Attempts` | Número de ejecuciones realizadas (`RetryAttempt + 1`) |

Un mensaje apartado **no vuelve a entregarse** automáticamente. Reprocesarlo es una operación manual (fuera de alcance).

## 3. Eventos de fallo emitidos al compensar

Los mismos eventos y payloads que ya emiten los caminos permanentes. **Sin cambios de contrato.**

| Servicio | Suscripción de entrada | Evento emitido | Payload |
|---|---|---|---|
| Transaction | `transaction-initiated` | `transaction-failed` | `{ CorrelationId, CustomerId }` |
| Balance | `balance-initiated` | `balance-failed` | `{ CorrelationId, CustomerId }` |
| Transfer | `transfer-initiated` | `transfer-failed` | `{ CorrelationId, CustomerId }` |
| Notification | — | — | No tiene evento de fallo: solo registra y aparta |
| Suscripciones que solo actualizan estado | — | — | Desviación justificada (hallazgo 6): solo registran y apartan |

`CustomerId` puede ir nulo si no se pudo leer.

**Regla de emisión (FR-016)**: el evento de fallo se emite **solo si** el servicio no tiene ya un registro del `CorrelationId` en su
propia base de datos (Transaction: `Transaction`; Balance: `Balance`; Transfer: `TransferRequest`) o si no puede consultarla.
Con registro existente el mensaje se aparta sin emitir. Motivo: `balance-failed` y `transfer-failed` cancelan en Transaction toda
saga `Pending`, y `transaction-failed` genera un correo de fallo en Notification sin comprobar estado.

## 4. Contrato de registro

Cada mensaje apartado, reprogramado o fallido produce una entrada con estos parámetros estructurados (nombres fijos):

| Parámetro | Descripción |
|---|---|
| `CorrelationId` | Del cuerpo; `null` y explícito si no se pudo leer |
| `MessageId` | Identificador del mensaje en el broker (siempre disponible) |
| `MessageType` | Nombre de la suscripción/evento (p. ej. `transfer-initiated`) |
| `Attempt` | Número de ejecución actual (`RetryAttempt + 1`) |
| `Classification` | `Poison` · `Permanent` · `Transient` |
| `Reason` | Motivo de apartado cuando aplica (`PoisonMessage`, `RetriesExhausted`, `CompensationFailed`) |

| Situación | Nivel | Plantilla (resumen) |
|---|---|---|
| Mensaje apartado | `Error` | "Mensaje {MessageType} apartado a la DLQ ({Reason}); CorrelationId {CorrelationId}…" |
| Fallo transitorio con reintento programado | `Warning` | "Mensaje {MessageType} falló (transitorio); reintento {Attempt} en {DelaySeconds}s; CorrelationId {CorrelationId}…" |
| Compensación omitida: ya existe registro del `CorrelationId` (FR-016) | `Warning` | "Compensación omitida para {MessageType}: ya hay registro; CorrelationId {CorrelationId}" con `Reason = "AlreadyRegistered"` |
| Compensación emitida | `Information` | "Evento de fallo {Event} emitido para {MessageType}; CorrelationId {CorrelationId}" |
| No se pudo consultar el registro al decidir compensar | `Warning` | "No se pudo comprobar el registro de {CorrelationId}; se emite {Event}" + excepción |
| Error del procesador (`ProcessError`) | `Error` | "Error del procesador {ErrorSource} en {EntityPath}" + excepción |
| Error del procesador por apagado ordenado | `Debug` | Misma plantilla; no se trata como fallo (FR-014) |
| Mensaje ya ignorado por `ProcessService` | `Warning` | Sin cambios (plantilla existente con `Reason`) |

## 5. Lo que no cambia

- Nombres de evento, `Subject`, payloads y `infra/local/Config.json` (suscripciones, reglas, `MaxDeliveryCount`, `LockDuration`, TTL).
- Las constantes de `IgnoredMessageReasonConstants` y el comportamiento de mensajes ignorados dentro de `ProcessService`.
- El camino permanente de datos (SQL 515/547/8115/8152): compensa y completa, sin reintento.
