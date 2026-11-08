# Data Model: Consumo resiliente de mensajes

Esta feature **no añade tablas, columnas ni migraciones**. Todo el estado del reintento viaja en el propio mensaje. Las entidades
siguientes son conceptos de diseño (clases en memoria o valores de configuración), no persistencia.

## FailureClassification

Resultado de clasificar un fallo. Determina qué se hace con el mensaje.

| Valor | Cuándo | Acción |
|---|---|---|
| `Poison` | El mensaje no puede interpretarse o carece de datos obligatorios | DLQ inmediata (+ `*-failed` si hay `CorrelationId`) |
| `Permanent` | La base de datos rechaza los datos (SQL 515, 547, 8115, 8152) | Lo resuelve `ProcessService`: emite su `*-failed` y completa (sin cambios) |
| `Transient` | Cualquier otra excepción del procesamiento (salvo `JsonException`, que es `Poison`) | Reintento programado con espera creciente |

Nota: la unicidad (SQL 2601/2627) **no es un fallo**: significa "ya procesado" y la absorbe `ProcessService` (FR-011).

## PoisonCheck (resultado de `MessageClassifier`)

| Campo | Tipo | Descripción |
|---|---|---|
| `IsPoison` | bool | Verdadero si el mensaje es venenoso |
| `Detail` | string | Qué estaba mal, legible (p. ej. "El cuerpo del mensaje está vacío") |
| `CorrelationId` | string? | El `CorrelationId` si se pudo leer; `null` si no. Máx. 64 caracteres (los más largos se truncan solo para registrar) |

**Reglas de validación** (cada una produce `Poison`):
1. Cuerpo vacío o solo espacios.
2. No es JSON válido.
3. Es JSON `null` o no es un objeto.
4. `CorrelationId` ausente, nulo o en blanco.
5. `CorrelationId` con más de 64 caracteres (misma regla que ya aplican Transfer y Transaction).

6. El cuerpo no deserializa a la entidad del servicio (por ejemplo un campo con tipo incorrecto, `"CustomerId":"abc"`, ya que `CustomerId` es un entero en las cuatro entidades): es el mismo
   intento de deserialización que hace `ProcessService`, hecho antes para poder apartar el mensaje.

Como segunda línea de defensa, una `JsonException` lanzada **durante** el procesamiento se trata también como `Poison` en
`ResilientMessageProcessor`: es un error permanente, reintentarlo no lo arregla.

## RetryPolicy

| Valor | Tipo | Descripción |
|---|---|---|
| `MAX_RETRIES` | int = 5 | Reintentos tras la primera ejecución (6 ejecuciones en total) |
| `BASE_DELAY_SECONDS` | int = 5 | Espera base |
| `GROWTH_FACTOR` | int = 3 | Multiplicador por reintento |
| `MAX_DELAY_SECONDS` | int = 300 | Tope de la espera |
| `JITTER` | decimal = 0,2 | Variación aleatoria ±20 % |

`NextDelay(retry, random)` es función pura: `min(BASE · FACTOR^(retry−1), MAX) × (1 ± JITTER)`. Tabla en [research.md](research.md) R2.

## RetryAttempt (propiedad del mensaje)

| Aspecto | Valor |
|---|---|
| Nombre | `RetryAttempt` (propiedad de aplicación del mensaje) |
| Tipo | entero |
| Ausente | equivale a 0 (primera entrega del mensaje original) |
| Quién la escribe | el consumidor, al programar la copia: `RetryAttempt = intento anterior + 1` |
| Quién la lee | el consumidor, al recibir un mensaje, para saber cuántos reintentos lleva |
| Valor máximo | `MAX_RETRIES`; si un fallo transitorio ocurre con `RetryAttempt = MAX_RETRIES`, se compensa y se aparta |

El resto de propiedades y el cuerpo del mensaje original se copian sin cambios, de modo que la copia es indistinguible para el
servicio salvo por `RetryAttempt`.

## DeadLetterRecord (lo que queda en la DLQ)

| Campo | Origen | Ejemplo |
|---|---|---|
| `DeadLetterReason` | Constante fija | `PoisonMessage` · `RetriesExhausted` · `CompensationFailed` |
| `DeadLetterErrorDescription` | Detalle legible, ≤ 1 024 caracteres | "Reintentos agotados tras 6 intentos. Último error: SqlException: Timeout expired" |
| `CorrelationId` (propiedad) | Del cuerpo, si es legible | `a3f1…` |
| `Attempts` (propiedad) | `RetryAttempt + 1` | `6` |

## LogEntry (contrato de registro)

Campos estructurados, con nombres fijos en la plantilla (ver [contracts/message-handling.md](contracts/message-handling.md)):
`CorrelationId`, `MessageId`, `MessageType`, `Attempt`, `Classification`, `Reason`.

## CompensationDecision (regla de seguridad, FR-016)

Decisión que toma el `FailureCompensator` antes de emitir un `*-failed`. Solo aplica a la suscripción de primer paso de Transaction,
Balance y Transfer; para cualquier otra devuelve `false` sin consultar.

| Situación | Decisión | Se registra |
|---|---|---|
| No existe registro del `CorrelationId` en la base del servicio | **Emitir** el `*-failed` | `Information` |
| Existe un registro (cualquier estado) | **No emitir**: el paso ya tuvo efecto | `Warning` con `Reason = "AlreadyRegistered"` |
| La base de datos no se puede consultar | **Emitir** | `Warning` con la causa de la consulta fallida |
| `CorrelationId` ilegible | **No emitir** (no hay a quién correlacionar) | `Warning` |

Registro consultado: `Transaction` (Transaction), `Balance` (Balance) y `TransferRequest` (Transfer). El mensaje se aparta a la DLQ
en todos los casos; esta decisión solo gobierna el evento de fallo.

## Transiciones del procesamiento de un mensaje

```text
recibido ──► ¿venenoso? ──sí──► [compensar si hay CorrelationId y no hay registro propio] ──► DLQ (PoisonMessage)
   │
   no
   ▼
 ProcessService.Execute
   │
   ├─ éxito ───────────────────────────────────────────► completar
   ├─ permanente de datos / duplicado ─ (lo resuelve ProcessService) ─► completar
   └─ excepción (transitoria)
        ├─ RetryAttempt < MAX_RETRIES ─► programar copia (RetryAttempt+1, espera creciente) ─► completar el original
        └─ RetryAttempt = MAX_RETRIES ─► [compensar si no hay registro propio] ─► DLQ (RetriesExhausted)
        (cancelación por apagado ─► sin reintento; el broker reentrega tras el reinicio)
```
