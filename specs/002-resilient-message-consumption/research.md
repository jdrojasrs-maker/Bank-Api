# Research: Consumo resiliente de mensajes

Fuentes: lectura del código en la rama `feat/retry-and-poison-messages`, `infra/local/Config.json` y una **prueba de concepto
desechable** contra el emulador local (programa de consola fuera del repositorio). Etiquetas: **[PRUEBA]** comprobado ejecutando,
**[CÓDIGO]** leído en el repositorio, **[DOC]** conocimiento del SDK/servicio sin verificar en esta sesión, **[VERIFICAR]** por
confirmar al implementar.

No quedaban marcadores `NEEDS CLARIFICATION` en la spec; las decisiones siguientes resuelven los puntos que ésta delegó al plan.

---

## R1. Cómo esperar entre reintentos

**Decisión**: al fallar de forma transitoria, el consumidor **programa una copia del mensaje** en el mismo tópico con
`ScheduleMessageAsync` (hora futura creciente) y **después completa el original**. El número de reintento viaja en la propiedad de
aplicación `RetryAttempt`.

**Evidencia**:
- **[PRUEBA]** Se programó un mensaje 8 s en el futuro: llegó a la suscripción a los **8,7 s**, con `Subject` y `RetryAttempt`
  intactos y `DeliveryCount = 1`. El emulador soporta mensajes programados.
- **[CÓDIGO]** `Config.json`: cada suscripción tiene una regla de correlación por `Label` = nombre del evento, y hay una suscripción
  por evento. La copia lleva el mismo `Subject`, así que solo llega a **la suscripción que la originó**.
- **[CÓDIGO]** Hoy `Process` solo llama a `CompleteMessageAsync` tras el `Publish`; si la excepción sube, el SDK abandona el mensaje y
  la reentrega es inmediata. Por eso las 10 entregas se consumían en ~15 s (hallazgo 1).

**Orden de operaciones**: programar la copia **primero**, completar el original **después**.
- Si falla el programado (el bus no responde): se abandona el original y el broker lo reentrega (normal); no hay pérdida.
- Si falla la finalización tras programar: queda el original más la copia ⇒ un duplicado, que los consumidores idempotentes
  absorben. Se prefiere un duplicado a una pérdida.

**Alternativas descartadas**:
| Alternativa | Por qué no |
|---|---|
| Espera dentro del proceso (`Task.Delay`/Polly) con el mensaje bloqueado | Retiene el lock y la concurrencia; el reintento no sobrevive al reinicio del servicio (incumple FR-006); añade dependencia |
| `EnableRetryOnFailure` de EF Core | Solo cubre SQL, en milisegundos o pocos segundos, y no el bus; [VERIFICAR] su interacción con `ExecuteUpdateAsync` |
| Subir `MaxDeliveryCount` o `LockDuration` | No añade espera; solo aplaza el problema (el hallazgo 1 lo descarta expresamente) |
| `ServiceBusRetryOptions` del cliente | Se aplica a operaciones del cliente con el broker, no al manejador de negocio |
| Circuit breaker (pausar el procesador) | Fuera de alcance (spec, Assumptions); complementario, no sustituto |
| Abandonar con retardo | Service Bus no ofrece "abandonar con retardo" |

---

## R2. Política de reintentos

**Decisión**: **5 reintentos** (6 ejecuciones en total). Espera del reintento *n* (1 a 5):

`espera(n) = min(5 s × 3^(n−1), 300 s) × u`, con `u` uniforme en [0,8 ; 1,2]

| Reintento | Espera nominal | Acumulado nominal |
|:-:|--:|--:|
| 1 | 5 s | 5 s |
| 2 | 15 s | 20 s |
| 3 | 45 s | 65 s |
| 4 | 135 s | 200 s |
| 5 | 300 s (tope) | 500 s ≈ 8,3 min |

**Razonamiento**:
- Tolera una interrupción de **más de 3 minutos** (el 4.º reintento ocurre a ~3 min 20 s) ⇒ cumple SC-002 (2 min) con holgura; la
  ventana total (~8 min) cumple SC-003 y es muy inferior al TTL de 1 h del emulador.
- Factor 3 y tope de 5 min: crece rápido al principio (fallos breves) sin dejar un mensaje esperando indefinidamente.
- La variación aleatoria de ±20 % evita que mensajes que fallaron a la vez reintenten a la vez (FR-005).
- La política es una **función pura** (`RetryPolicy.NextDelay(attempt, random)`), por lo que se prueba sin broker ni reloj.

**[VERIFICAR]** Los valores se expondrán como constantes (`RetryConstants`) y se ajustan si la verificación manual (quickstart)
muestra que es demasiado larga o corta.

---

## R3. Cómo se clasifica un fallo

**Decisión**: tres clases, evaluadas en este orden.

| Clase | Cómo se detecta | Qué se hace |
|---|---|---|
| **Venenoso** | Validación previa a MediatR: cuerpo vacío o en blanco, JSON ilegible, JSON `null` o no objeto, `CorrelationId` ausente/en blanco, o con más de 64 caracteres. **También** que el cuerpo no deserialice a la entidad del servicio (p. ej. un campo con tipo incorrecto, `"CustomerId":"abc"`; `CustomerId` es entero en las cuatro entidades), y, como segunda línea de defensa, una `Newtonsoft.Json.JsonException` lanzada al procesar | DLQ en la primera entrega (`PoisonMessage`) + `*-failed` si el `CorrelationId` es legible (R4) |
| **Permanente de datos** | Dentro de `ProcessService`, la lista blanca ya existente: SQL 515, 547, 8115, 8152 (`DatabaseErrors.IsPermanent`) | **Sin cambios**: el servicio emite su `*-failed` y el mensaje se completa (FR-010) |
| **Transitorio** | Todo lo demás que salga como excepción de `ProcessService`, salvo `JsonException` (timeout, deadlock, conexión caída, error del bus). Las violaciones de unicidad ya se tratan como "ya procesado" dentro del servicio | Reintento con espera (R1, R2) |

**Evidencia y matices**:
- **[CÓDIGO]** La lista blanca ya existe y el resto "se propaga" por diseño. La clasificación por exclusión es la que usa el código.
- **[CÓDIGO]** Aclaración respecto al borrador de la conversación: **8152 es truncamiento de texto, no JSON corrupto**, y **547
  es violación de clave foránea o CHECK, no falta de `CorrelationId`**. El JSON corrupto no llega a SQL: se detecta en la validación previa.
- **Un timeout de SQL no garantiza "no se guardó nada"**: puede confirmarse la escritura y fallar la respuesta. Por eso los
  reintentos requieren idempotencia (Transfer y Transaction ya la tienen; ver R9).

**Por qué la validación incluye deserializar a la entidad del servicio**: **[CÓDIGO]** `ParseTransaction` y `ParseTransfer`
capturan `JsonException` y tratan el mensaje como "ignorado" (se completa). Si el clasificador solo mirara la forma del JSON, un
campo con tipo incorrecto seguiría completándose en silencio en Transfer y Transaction, en lugar de ir a la DLQ. Por eso el
`MessageClassifier` (uno por servicio) intenta la misma deserialización que hace `ProcessService` y, si falla, el mensaje es venenoso.

**Por qué la validación va antes de MediatR y no dentro de `ProcessService`**: **[CÓDIGO]** hoy `ParseTransaction` (Transaction) y
`ParseTransfer` (Transfer) ya tratan un mensaje inválido como "ignorado": lo registran y lo **completan**; las 103 pruebas existentes
fijan ese comportamiento. Moverlo a una excepción rompería esas pruebas (SC-007). Con la validación previa, un mensaje venenoso
nunca llega a `ProcessService`, y la protección interna queda como segunda línea de defensa. En Balance y Notification
(`JsonConvert.DeserializeObject` sin comprobación; hoy lanzan `NullReferenceException` y gastan las 10 entregas) la validación previa
es el único control.

---

## R4. Compensación (venenosos y reintentos agotados)

**Decisión** (aclaración del 2026-11-07, opción B): si el `CorrelationId` es legible, se emite el `*-failed` del **primer paso** y
además se aparta el mensaje; si no es legible, solo se aparta.

**Regla de seguridad (aclaración del 2026-11-07, opción B, FR-016)**: antes de emitir, el compensador consulta **la base de datos
del propio servicio**; si ya existe un registro de ese `CorrelationId`, el paso tuvo efecto y **no se emite** (el mensaje se aparta
igualmente). Si no hay registro, o la base no se puede consultar, **se emite**.

**Por qué es necesaria** (verificado en el código, **[CÓDIGO]**):

| Evento de fallo | Lo consume | Al recibirlo | Efecto de un `*-failed` espurio |
|---|---|---|---|
| `balance-failed` | Transaction | `Pending` ⇒ emite `transaction-failed` y pasa a `Canceled`; estado final ⇒ lo ignora | Cancela una saga **en vuelo** (el `transfer-confirmed` real que llegue después se ignora: dinero movido y transacción cancelada) |
| `transfer-failed` | Transaction | Igual, y además emite `transfer-failed-balance` | Igual |
| `transaction-failed` | Notification | Envía el correo "Your transaction has failed" y lo guarda; **no comprueba estado** | Correo de fallo falso para una transferencia completada |

Transaction solo protege los estados *finales*; una saga `Pending` en vuelo no está protegida. La versión anterior de este plan,
con un compensador sin acceso a la base de datos, no distinguía estos casos.

**Qué se consulta** (solo para la suscripción de primer paso; para cualquier otra se devuelve `false` sin consultar):

| Servicio | Registro consultado | Si existe |
|---|---|---|
| Transaction | Fila de `Transaction` con ese `CorrelationId` (cualquier estado) | No se emite `transaction-failed` |
| Balance | Fila de `Balance` con ese `CorrelationId` | No se emite `balance-failed` |
| Transfer | Fila de `TransferRequest` con ese `CorrelationId` (existe tanto para confirmadas como para rechazadas) | No se emite `transfer-failed`: ya hay un resultado y su emisión la recupera la reentrega |

**Probabilidad frente a gravedad**: el cuerpo dañado en un duplicado de un mensaje bueno es casi imposible (Service Bus entrega
duplicados idénticos). Lo más plausible es un duplicado que agota los reintentos mientras otra copia ya tuvo efecto. Es raro, pero
la consecuencia (cancelar una transferencia ya ejecutada) es grave en un sistema bancario, y la comprobación es barata.

**Carrera aceptada**: entre la consulta y la emisión pasan milisegundos; un registro creado justo en ese intervalo no se vería. Es la
misma clase de ventana que ya se documenta en Transaction (emitir antes de confirmar) y se acepta.

**Base de datos caída**: es el caso principal de reintentos agotados. Si la consulta falla, se emite: sin registro legible lo más
probable es que el paso no ocurriera, y es justo la situación que la compensación quiere cerrar. Se registra un `Warning`.

Una clase `FailureCompensator` por servicio (con `IFailureCompensator.CompensateAsync(body, messageType, ct)`) que depende del
`IServiceBusSenderService`, del logger y de la base de datos del servicio **a través de `IServiceScopeFactory`** (el compensador es
*singleton*; `IDatabaseService` es *scoped*):

| Servicio | Suscripción de entrada | Evento de fallo | Payload (igual al existente) |
|---|---|---|---|
| Transaction | `transaction-initiated` | `transaction-failed` | `{ CorrelationId, CustomerId }` |
| Balance | `balance-initiated` | `balance-failed` | `{ CorrelationId, CustomerId }` |
| Transfer | `transfer-initiated` | `transfer-failed` | `{ CorrelationId, CustomerId }` |
| Notification | cualquiera | — (sin evento de fallo) | — |
| Resto de suscripciones (solo actualizan estado) | — | — (desviación justificada, hallazgo 6) | — |

**[CÓDIGO]** Los payloads coinciden con los que ya emiten los caminos permanentes en cada `ProcessService`.
`CustomerId` se lee de forma tolerante: si falta, viaja nulo.

**Por qué una clase aparte y no un método de `IProcessService`**: `ProcessService` queda intacto (SC-007) y la compensación tiene
una única responsabilidad. Precio de la regla de seguridad: **las pruebas del compensador necesitan SQL Server real**, también en
Balance, cuyo proyecto de pruebas pasa a usar Testcontainers (Docker) como los de Transfer y Transaction. Notification no tiene
compensador ni base de datos relacional y sigue sin Docker. Los demás componentes (procesador, política, clasificador) se siguen
probando sin base de datos.

**Si el propio `*-failed` no se puede enviar** (FR-009): la excepción **no se traga**. El mensaje se aparta con motivo
`CompensationFailed` si el broker lo permite; si el broker no responde, la finalización también falla, el lock vence y el broker
reentrega (nada se pierde en silencio).

**Seguridad ante procesamiento tardío**: si un mensaje apartado se reprocesa más tarde (reproceso manual de la DLQ) tras haber
emitido su `*-failed`, Transaction ya protege sus estados finales (`Canceled` no cambia), de modo que no hay estados contradictorios.
Esto cubre el reproceso manual; el caso de un duplicado concurrente lo cubre la regla de seguridad de arriba.

---

## R5. Qué hace `ProcessError`

**Decisión**: solo **registra** (nivel `Error`): `ErrorSource`, `EntityPath`, `FullyQualifiedNamespace` y la excepción. No aparta,
no completa ni compensa (aclaración del responsable, FR-012).

**Evidencia**:
- **[DOC]** `ProcessErrorEventArgs` no contiene el mensaje; solo describe el fallo del procesador (conexión, lock perdido, o una
  excepción lanzada por el manejador, con `ErrorSource = ProcessMessageCallback`). No hay qué apartar.
- **[DOC]** Si el manejador del mensaje lanza, el SDK abandona el mensaje por su cuenta y notifica a `ProcessError`. Por eso la
  decisión sobre el mensaje se toma **dentro** del manejador (`ResilientMessageProcessor`), que captura las excepciones y nunca
  deja que se propaguen salvo cancelación.
- Un cierre ordenado (FR-014): si `args.CancellationToken` está cancelado, no se programa reintento ni se cuenta como fallo; el
  mensaje se abandona y se reentrega tras el reinicio. En `ProcessError`, una `OperationCanceledException` por apagado se
  registra en nivel bajo, no como error.

---

## R6. Registro con `CorrelationId`

**Decisión**: parámetros estructurados en las plantillas (no solo scopes), con nombres fijos: `CorrelationId`, `MessageId`,
`MessageType`, `Attempt`, `Classification` (`Poison` | `Permanent` | `Transient`) y `Reason`.

**Razón**: **[CÓDIGO]** `CapturingLogger` de las pruebas ya recoge propiedades estructuradas de la plantilla (como hace
`IgnoreMessage` con `Reason`), y `BeginScope` devuelve `null`: un scope no sería observable en las pruebas. Si falta el
`CorrelationId`, se registra con `MessageId` y `CorrelationId = null` explícito (FR-013).

---

## R7. Dead-letter con motivo y detalle

**Decisión**: `DeadLetterMessageAsync(mensaje, propiedadesAModificar, motivo, detalle)` con:
- Motivos fijos: `PoisonMessage`, `RetriesExhausted`, `CompensationFailed`.
- Detalle legible, **truncado a 1 024 caracteres** (el broker limita el tamaño; se evita un fallo al apartar).
- Propiedades `CorrelationId` y `Attempts` para poder consultar la DLQ sin abrir el cuerpo.

**Evidencia**: **[PRUEBA]** `DeadLetterMessageAsync(msg, "PoisonMessage", "detalle de prueba")` en el emulador: la DLQ devolvió
`DeadLetterReason = PoisonMessage` y `DeadLetterErrorDescription = detalle de prueba`. **[VERIFICAR]** que el emulador conserve
también las propiedades modificadas; si no, el `CorrelationId` irá solo en el detalle y el registro.

---

## R8. Cómo se prueba sin broker

**Decisión**: separar decisión y ejecución.
- `ResilientMessageProcessor` recibe el mensaje, la suscripción, el número de entrega/`RetryAttempt` y un `IMessageActions`
  (`Complete`, `DeadLetter(motivo, detalle, propiedades)`, `ScheduleRetry(intento, espera)`). No conoce el SDK.
- `ServiceBusMessageActions` (en `External`) implementa `IMessageActions` sobre `ProcessMessageEventArgs`.
- Las pruebas usan un `FakeMessageActions` que registra qué se pidió, un `IProcessService`/delegado que lanza lo que se quiera, un
  `Random` con semilla fija y `FakeServiceBusSender` (ya existe en Transfer y Transaction).

**Cobertura** (todas fallan antes del cambio, Principio V):
| Escenario de la spec | Prueba |
|---|---|
| Historia 1 | Cuerpo vacío, `null`, JSON roto, sin `CorrelationId`, `CorrelationId` largo ⇒ `DeadLetter("PoisonMessage")` en la 1.ª entrega, sin `Publish`; con `CorrelationId` legible **y sin registro previo**, se emite el `*-failed` (con registro: no se emite, FR-016) |
| Historia 2 | Excepción transitoria ⇒ `ScheduleRetry` con intento +1 y espera dentro del rango; esperas crecientes entre reintentos; el original se completa; con cancelación no se reprograma |
| Historia 3 | Intento 5 y excepción ⇒ `*-failed` + `DeadLetter("RetriesExhausted")`; si el envío del `*-failed` falla, no se traga y se aparta como `CompensationFailed` |
| Historia 4 | El registro contiene `CorrelationId`, `MessageType`, `Attempt`, `Classification`; `ProcessError` registra origen y entidad |
| FR-010/FR-011 | Un permanente de datos y una violación de unicidad **no** pasan por la ruta de reintento (las pruebas existentes lo cubren con SQL real) |
| Política | `RetryPolicy` es creciente, está acotada por el tope y respeta el rango de variación |

Balance y Notification: proyectos nuevos. **Notification sin base de datos** (no tiene compensador). **Balance con SQL Server en
contenedor** (Testcontainers) solo por las pruebas del compensador (FR-016); el resto de sus pruebas no usa la base. Pruebas
nuevas de FR-016 en Transaction, Balance y Transfer: con registro existente en cada estado (`Pending`, `Completed`, `Canceled`
para Transaction) no se emite; sin registro se emite; con la base inalcanzable se emite.

---

## R9. Idempotencia y riesgo de los reintentos

- **[CÓDIGO]** Transfer (tabla `TransferRequest`, índice único) y Transaction (transiciones condicionales desde `Pending`)
  convergen ante duplicados: un reintento tras un timeout ambiguo es seguro.
- **[CÓDIGO]** Balance: `ProcessDataBase` busca por `CorrelationId` y actualiza; es idempotente en secuencia, no ante
  concurrencia (sin índice único). Los reintentos son secuenciales, por lo que no empeoran la situación.
- **[CÓDIGO]** Notification: envía el correo y luego guarda; un fallo tras enviar duplica el correo en el reintento. **Hoy ya
  ocurre hasta 10 veces en segundos**; con espera serían como máximo 6, más espaciados. No se corrige aquí (hallazgo 5).
- Conclusión: el cambio **no empeora** ningún caso y se señala como riesgo en la spec y en el plan.

---

## R10. Receptor y ciclo de vida

- **[CÓDIGO]** Los cuatro receptores son casi idénticos (`ServiceBusClient` + un procesador por suscripción + MediatR). Se mantiene
  esa forma; solo cambia `Process` (delega en el procesador de resiliencia) y `ProcessError`.
- `AutoCompleteMessages = false`: cada ruta del procesador debe liquidar el mensaje de forma explícita (completar, apartar o
  reprogramar+completar). Si una ruta olvidara liquidarlo, se detectaría en pruebas en lugar de ocultarlo el SDK.
- El remitente de reintentos es un `ServiceBusSender` **único**, creado desde el mismo `ServiceBusClient` del receptor y reutilizado
  (no uno por mensaje). La fuga del remitente por mensaje de `ServiceBusSenderService` es deuda conocida y se **señala, no se corrige**.
- **[CÓDIGO]** `Process` hoy publica por MediatR y completa; se conserva `IMediator.Publish(ProcessEvents)` como único punto de entrada
  al servicio, pasado al procesador como delegado.

---

## R11. Entorno y CI

- **`Config.json`**: sin cambios. Se conservan `MaxDeliveryCount = 10`, `LockDuration = PT1M` y TTL `PT1H` como red de seguridad
  (por ejemplo, ante una caída del proceso en mitad del manejador, donde sí cuenta la entrega del broker).
- **CI**: la matriz de pruebas de `ci.yml` pasa de `[Transfer, Transaction]` a `[Transfer, Transaction, Balance, Notification]`. Los
  checks nuevos se llaman `Tests (Balance)` y `Tests (Notification)`.
- El job `Tests (Balance)` pasa a usar Docker (Testcontainers) en el runner, como los de Transfer y Transaction; `Tests (Notification)` no.
- **Dos acciones manuales** (no son archivos del repositorio): ajustar la protección de ramas en GitHub para exigir los dos checks
  nuevos y enmendar la constitución (1.2.0), que nombra los tres checks vigentes. Hasta hacerlo, los jobs nuevos corren pero no bloquean.
