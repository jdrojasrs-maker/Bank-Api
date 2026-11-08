---

description: "Lista de tareas: Consumo resiliente de mensajes"
---

# Tasks: Consumo resiliente de mensajes

**Input**: Documentos de diseño en `/specs/002-resilient-message-consumption/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/message-handling.md, quickstart.md

**Tests**: INCLUIDAS y obligatorias. La spec las exige (FR-015) y la constitución las impone (Principio V, NON-NEGOTIABLE): cada fase
de historia empieza por pruebas que deben **fallar** antes de implementar (rojo → verde).

**Organization**: Las tareas se agrupan por historia de usuario. US1 (venenosos) y US2 (transitorios) son P1; US3 (reintentos agotados)
y US4 (registro) son P2. US3 reutiliza el compensador de US1 y el reintento de US2.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: se puede ejecutar en paralelo (archivos distintos, sin dependencias pendientes)
- **[Story]**: US1 a US4 (solo en fases de historia)
- Todas las rutas son relativas a la raíz del repositorio
- **Una tarea que lista varias rutas se aplica a cada servicio**: son archivos independientes que pueden hacerse en paralelo, pero la
  tarea no termina hasta cubrir los cuatro (o los tres, si lo indica)

## Path Conventions

- Servicios (abreviaturas que se usan abajo):
  - `<TRANSACTION>` = `src/Bank.Transaction/Bank.Transaction.Api`
  - `<BALANCE>` = `src/Bank.Balance/Bank.Balance.Api`
  - `<TRANSFER>` = `src/Bank.Transfer/Bank.Transfer.Api`
  - `<NOTIFICATION>` = `src/Bank.Notification/Bank.Notification.Api` (**no** la plantilla `src/Bank.Notification/Bank.Notification`, que no se toca)
- Pruebas (un proyecto por servicio; ninguno referencia a más de un servicio):
  - `<TX-TESTS>` = `tests/Bank.Transaction.Api.Tests`, `<BAL-TESTS>` = `tests/Bank.Balance.Api.Tests` (nuevo),
    `<TRF-TESTS>` = `tests/Bank.Transfer.Api.Tests`, `<NOT-TESTS>` = `tests/Bank.Notification.Api.Tests` (nuevo)
- Código nuevo por servicio en `<SERVICIO>/Application/Resilience/` (namespace `Bank.<Servicio>.Api.Application.Resilience`) y
  `<SERVICIO>/External/ServiceBusRecive/`
- Estilo del repo: namespaces *file-scoped*, constantes `UPPER_SNAKE_CASE`, sufijos `Service`/`Handler`. Los archivos existentes usan
  CRLF y BOM: conservarlos al editar. Mensajes de registro y de excepción en español
- **`ProcessService` de ningún servicio se modifica** y **ninguna prueba existente se modifica** (SC-007). Si una tarea parece exigirlo, parar y revisar
- **No hacer commits** hasta que el responsable del repositorio lo indique de forma expresa

## Contratos compartidos (referencia para las tareas)

```text
public record IncomingMessage(string Body, string MessageType, string MessageId, int RetryAttempt);

public interface IMessageActions {
    Task CompleteAsync(CancellationToken ct);
    Task DeadLetterAsync(string reason, string detail, string? correlationId, int attempts, CancellationToken ct);
    Task ScheduleRetryAsync(int nextAttempt, TimeSpan delay, CancellationToken ct);
}

public interface IFailureCompensator {
    // true si emitió un evento de fallo; false si ese paso no tiene (no hace nada)
    Task<bool> CompensateAsync(string body, string messageType, CancellationToken ct);
}

public class ResilientMessageProcessor {
    public ResilientMessageProcessor(Func<string, string, CancellationToken, Task> process,   // (body, messageType, ct)
        IFailureCompensator compensator, ILogger<ResilientMessageProcessor> logger, Random? random = null);
    public Task HandleAsync(IncomingMessage message, IMessageActions actions, CancellationToken ct);
}
```

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: crear los dos proyectos de pruebas que faltan y fijar la línea base

- [X] T001 Crear `<BAL-TESTS>/Bank.Balance.Api.Tests.csproj` (`net10.0`, nullable e implicit usings, `IsPackable=false`, `<Using Include="Xunit" />`) copiando las mismas versiones de `coverlet.collector`, `Microsoft.NET.Test.Sdk`, `xunit` y `xunit.runner.visualstudio` que `<TRF-TESTS>/Bank.Transfer.Api.Tests.csproj`, **incluido** `Testcontainers.MsSql` (el compensador de Balance se prueba con SQL Server real, FR-016; requiere Docker); `ProjectReference` **solo** a `<BALANCE>/Bank.Balance.Api.csproj`
- [X] T002 [P] Crear `<NOT-TESTS>/Bank.Notification.Api.Tests.csproj` con la misma configuración que T001 pero **sin** `Testcontainers.MsSql` (Notification no tiene compensador ni base relacional: no necesita Docker); `ProjectReference` **solo** a `<NOTIFICATION>/Bank.Notification.Api.csproj`
- [X] T003 Añadir ambos proyectos a `Bank.slnx` dentro del `<Folder Name="/tests/">` existente (depende de T001 y T002)
- [X] T004 Línea base: `dotnet build Bank.slnx` sin errores y `dotnet test` en los proyectos de Transfer y Transaction con **31 y 72 pruebas en verde**; los dos proyectos nuevos descubren 0 pruebas sin error. Anotar los resultados (depende de T003; requiere Docker)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: andamiaje común en los cuatro servicios, **sin cambiar el comportamiento**: el receptor delega en un procesador que por
ahora solo ejecuta y completa, y propaga cualquier excepción como hoy

**⚠️ CRITICAL**: ninguna historia puede empezar hasta terminar esta fase

### Constantes y contratos

- [X] T005 [P] Añadir en `<TRANSACTION>/Domain/Constants/Constant.cs`, `<BALANCE>/Domain/Constants/Constant.cs`, `<TRANSFER>/Domain/Constants/Constants.cs` y `<NOTIFICATION>/Domain/Constants/Constant.cs` dos clases: `DeadLetterReasonConstants` (`POISON_MESSAGE = "PoisonMessage"`, `RETRIES_EXHAUSTED = "RetriesExhausted"`, `COMPENSATION_FAILED = "CompensationFailed"`) y `RetryConstants` (`MAX_RETRIES = 5`, `BASE_DELAY_SECONDS = 5`, `GROWTH_FACTOR = 3`, `MAX_DELAY_SECONDS = 300`, `JITTER = 0.2` como `double`, `RETRY_ATTEMPT_PROPERTY = "RetryAttempt"`, `MAX_DETAIL_LENGTH = 1024`, `MAX_CORRELATION_ID_LENGTH = 64`)
- [X] T006 [P] Crear en `<TRANSACTION>`, `<BALANCE>`, `<TRANSFER>` y `<NOTIFICATION>` bajo `Application/Resilience/`: `IncomingMessage.cs`, `IMessageActions.cs`, `IFailureCompensator.cs` y `NullFailureCompensator.cs` (devuelve `false` sin hacer nada), con las firmas exactas de la sección *Contratos compartidos*

### Infraestructura de pruebas

- [X] T007 [P] Crear `Infrastructure/FakeMessageActions.cs` en `<TX-TESTS>`, `<BAL-TESTS>`, `<TRF-TESTS>` y `<NOT-TESTS>`: implementa `IMessageActions`; guarda en una lista ordenada `Calls` cada invocación (`"Complete"`, `"DeadLetter:<reason>"`, `"ScheduleRetry:<n>"`) con sus argumentos en propiedades legibles (`Reason`, `Detail`, `CorrelationId`, `Attempts`, `NextAttempt`, `Delay`); permite configurar que una operación lance una excepción (`FailOn`). En `<BAL-TESTS>` copiar además `CapturingLogger.cs`, `FakeServiceBusSender.cs` y `SqlServerFixture.cs` desde `<TX-TESTS>/Infrastructure/` (cambiando el namespace) y crear un `BalanceHarness.cs` análogo a `TransactionHarness.cs` (cadena de conexión del contenedor, esquema creado con las **migraciones reales** de Balance, y utilidades para insertar y contar filas de `Balance`); en `<NOT-TESTS>`, solo `CapturingLogger.cs`

### Pruebas del andamiaje (escribir primero; deben FALLAR por no compilar)

- [X] T008 [P] `Resilience/ResilientMessageProcessorTests.cs` en `<TX-TESTS>`, `<BAL-TESTS>`, `<TRF-TESTS>` y `<NOT-TESTS>`: (a) con un `process` que termina bien, `HandleAsync` invoca `process` una vez con (cuerpo, tipo de mensaje) y llama `CompleteAsync` una vez; (b) con un `process` que lanza `InvalidOperationException`, la excepción **se propaga** y no se llama `CompleteAsync`. Usar `NullFailureCompensator`, `CapturingLogger<ResilientMessageProcessor>` y `FakeMessageActions`. Ejecutar y confirmar que falla (depende de T006 y T007)

### Implementación

- [X] T009 [P] Crear `Application/Resilience/ResilientMessageProcessor.cs` en `<TRANSACTION>`, `<BALANCE>`, `<TRANSFER>` y `<NOTIFICATION>` con el constructor y `HandleAsync` de *Contratos compartidos*; por ahora: `await _process(...)`, `await actions.CompleteAsync(ct)`, sin capturar excepciones (depende de T006)
- [X] T010 [P] Crear `External/ServiceBusRecive/ServiceBusMessageActions.cs` en los cuatro servicios: implementa `IMessageActions` sobre un `ProcessMessageEventArgs` y un `ServiceBusSender` de reintentos. `CompleteAsync` → `args.CompleteMessageAsync`. `DeadLetterAsync` → `args.DeadLetterMessageAsync(args.Message, propiedades, reason, detail)` con `detail` **truncado a `MAX_DETAIL_LENGTH` (1 024)** y propiedades `CorrelationId` (solo si no es nulo) y `Attempts`. `ScheduleRetryAsync` → crea un `ServiceBusMessage` con el mismo cuerpo, `Subject`, `ContentType`, `MessageId` y `ApplicationProperties` del original más `RetryAttempt = nextAttempt`, y lo programa con `sender.ScheduleMessageAsync(copia, DateTimeOffset.UtcNow + delay)`; **si falla, la excepción se propaga** (el original no se completa)
- [X] T011 [P] Modificar `External/ServiceBusRecive/ServiceBusReciveService.cs` en los cuatro servicios: crear los procesadores con `new ServiceBusProcessorOptions { AutoCompleteMessages = false }`; crear **un** `ServiceBusSender` de reintentos con `_client.CreateSender(configuration["SERVICEBUSTOPIC"])` y liberarlo en `StopAsync`; recibir por constructor `IFailureCompensator` e `ILogger<ResilientMessageProcessor>` y construir un `ResilientMessageProcessor` cuyo `process` es `(body, type, ct) => _mediator.Publish(new ProcessEvents(body, type), ct)`; `Process` arma el `IncomingMessage` (`RetryAttempt` leído de `ApplicationProperties["RetryAttempt"]`, 0 si falta o no es entero; `MessageId` del mensaje; `MessageType` = suscripción), crea el `ServiceBusMessageActions` y llama `HandleAsync`. **`ProcessError` queda como está** por ahora (depende de T009 y T010)
- [X] T012 [P] Registrar en `<TRANSACTION>/Program.cs`, `<BALANCE>/Program.cs`, `<TRANSFER>/Program.cs` y `<NOTIFICATION>/Program.cs`: `builder.Services.AddSingleton<IFailureCompensator, NullFailureCompensator>();` (los reales llegan en US1). `Program.cs` solo registra dependencias (depende de T006)

**Checkpoint**:
- [X] T013 `dotnet build Bank.slnx` sin errores; las pruebas de T008 pasan en verde; las 31 + 72 existentes siguen en verde **sin modificarlas**; y, con los servicios en marcha, una transferencia por el Gateway sigue terminando `Completed` (el cambio a `AutoCompleteMessages = false` no debe romper el flujo). Depende de T008 a T012

---

## Phase 3: User Story 1 - Un mensaje imposible de procesar se aparta de inmediato con su motivo (Priority: P1) 🎯 MVP

**Goal**: un mensaje venenoso va a la DLQ en la primera entrega, con motivo y detalle, y, si su `CorrelationId` es legible, la
transferencia se cancela con el evento de fallo del paso.

**Independent Test**: enviar a cada servicio mensajes con cuerpo vacío, `null`, JSON roto y sin `CorrelationId`: terminan en la DLQ
con `DeliveryCount = 1`, motivo `PoisonMessage` y detalle; con `CorrelationId` legible se emite además el `*-failed`.

### Tests for User Story 1 (escribir primero; deben FALLAR)

- [X] T014 [P] [US1] `Resilience/MessageClassifierTests.cs` en `<TX-TESTS>`, `<BAL-TESTS>`, `<TRF-TESTS>` y `<NOT-TESTS>` sobre `MessageClassifier.Check(string body)` (devuelve `PoisonCheck { IsPoison, Detail, CorrelationId }`). Cada caso venenoso con `Detail` no vacío: cuerpo vacío; solo espacios; texto que no es JSON; JSON `null`; JSON que no es objeto (`[]`); sin `CorrelationId`; `CorrelationId` nulo o en blanco; `CorrelationId` de **65 caracteres** (con `Detail` que lo indica); cuerpo con `"CustomerId":"abc"` (no deserializa a la entidad del servicio). Casos válidos: `CorrelationId` de exactamente **64** caracteres y un mensaje completo bien formado (`IsPoison = false`, `CorrelationId` devuelto). Cuando el `CorrelationId` es legible pero el mensaje es venenoso por otro motivo (p. ej. `CustomerId` inválido), `CorrelationId` se devuelve igualmente
- [X] T015 [P] [US1] `Resilience/PoisonMessageTests.cs` en los cuatro proyectos de pruebas sobre `ResilientMessageProcessor`: con cada cuerpo venenoso de T014, `process` **nunca** se invoca, `Calls` contiene exactamente `["DeadLetter:PoisonMessage"]` con `Attempts = 1`, `Detail` no vacío y sin `Complete`; si el `CorrelationId` es legible se llama al compensador una vez (usar un `FakeFailureCompensator` en `Infrastructure/` que cuenta llamadas y devuelve `true`); si el compensador devuelve `false` (p. ej. por FR-016), el mensaje se aparta igualmente y no hay otro efecto; si no es legible, el compensador **no** se llama; una `JsonException` lanzada por `process` se trata igual que un venenoso (segunda línea de defensa, FR-002)
- [X] T016 [P] [US1] `Resilience/FailureCompensatorTests.cs` en `<TX-TESTS>`, `<BAL-TESTS>` y `<TRF-TESTS>` sobre `FailureCompensator` con SQL Server real (harness del proyecto) y `FakeServiceBusSender`. **Regla de seguridad FR-016**: (a) **sin registro** del `CorrelationId`, para la suscripción de primer paso (`transaction-initiated` / `balance-initiated` / `transfer-initiated`) emite **un** evento `transaction-failed` / `balance-failed` / `transfer-failed` con payload `{ CorrelationId, CustomerId }` y devuelve `true`; si falta `CustomerId`, viaja nulo; (b) **con registro existente** no emite nada y devuelve `false`: en Transaction probar las filas `Pending`, `Completed` y `Canceled`; en Balance una fila existente; en Transfer una fila de `TransferRequest` con resultado `Confirmed` y otra `Failed`; (c) con la **base de datos inalcanzable** **emite** y registra un `Warning`. Para construir el compensador en las pruebas: un `ServiceCollection` que registre `IDatabaseService` apuntando al contenedor del harness (caso normal) y otro con una cadena de conexión a un puerto cerrado con `Connect Timeout=1` (caso inalcanzable), y usar su `IServiceScopeFactory`; (d) para cualquier otra suscripción (p. ej. `balance-confirmed` en Transaction) no consulta ni envía nada y devuelve `false`; (e) si el envío lanza, la excepción se propaga; (f) `CorrelationId` ilegible → `false` sin consultar. Escenario SC-008 completo: insertar la fila, invocar con cuerpo defectuoso y comprobar 0 eventos. En `<NOT-TESTS>`: `NullFailureCompensator` devuelve `false` y no envía nada
- [X] T017 [US1] Ejecutar T014 a T016 y **confirmar que fallan** por la razón correcta (no compilan por falta de `MessageClassifier`, `PoisonCheck` y `FailureCompensator`)

### Implementation for User Story 1

- [X] T018 [P] [US1] Crear `Application/Resilience/MessageClassifier.cs` (clase estática con `PoisonCheck Check(string body)` y el tipo `PoisonCheck`) en los cuatro servicios, con las 6 reglas de data-model.md: cuerpo vacío/en blanco; JSON ilegible; JSON `null` o no objeto; `CorrelationId` ausente/en blanco; `CorrelationId` de más de `MAX_CORRELATION_ID_LENGTH` (64) caracteres; y que `JsonConvert.DeserializeObject<TEntidad>` (`TransactionEntity`, `BalanceEntity`, `TransferEntity`, `NotificationEntity`, con Newtonsoft como `ProcessService`) no lance `JsonException`. El `CorrelationId` se extrae de forma tolerante del JSON aunque otra regla falle; para registrar se trunca a 64 caracteres
- [X] T019 [P] [US1] Crear `Application/Resilience/FailureCompensator.cs` en `<TRANSACTION>`, `<BALANCE>` y `<TRANSFER>`: implementa `IFailureCompensator` con `IServiceBusSenderService` (el del servicio), `ILogger` e `IServiceScopeFactory` (el compensador es *singleton* e `IDatabaseService` es *scoped*: crear un scope por consulta). **Regla FR-016**: antes de emitir, `AnyAsync` con `AsNoTracking` del registro propio (Transaction: `Transaction`; Balance: `Balance`; Transfer: `TransferRequest`) por `CorrelationId`; si existe, no emitir y devolver `false` (registrar `Warning` con `Reason = "AlreadyRegistered"`); si la consulta lanza, registrar `Warning` y emitir; solo se consulta para la suscripción de primer paso. Mapa de primer paso a evento de fallo (Transaction: `TRANSACTION_INITIATED` → `TRANSACTION_FAILED`; Balance: `BALANCE_INITIATED` → `BALANCE_FAILED`; Transfer: `TRANSFER_INITIATED` → `TRANSFER_FAILED`, usando las constantes existentes de `SendSubscriptionConstants`/`ReceivedSubscriptionsConstants`); payload `new { CorrelationId, CustomerId }` leído con tolerancia (si falta, `null`). En `<NOTIFICATION>` se conserva `NullFailureCompensator`
- [X] T020 [P] [US1] Ampliar `ResilientMessageProcessor.HandleAsync` en los cuatro servicios: antes de invocar `process`, `MessageClassifier.Check(body)`; si es venenoso: llamar al compensador **solo si** `CorrelationId` no es nulo, `DeadLetterAsync(POISON_MESSAGE, detail, correlationId, attempts: RetryAttempt + 1)` y terminar sin completar ni invocar `process`; capturar `JsonException` lanzada por `process` y tratarla como venenosa (misma ruta). Las excepciones del compensador se propagan por ahora (FR-009 se completa en US3)
- [X] T021 [P] [US1] Sustituir en `<TRANSACTION>/Program.cs`, `<BALANCE>/Program.cs` y `<TRANSFER>/Program.cs` el registro de `NullFailureCompensator` por `AddSingleton<IFailureCompensator, FailureCompensator>()` (Notification conserva el nulo)
- [X] T022 [US1] Ejecutar T014 a T016 en verde y las suites existentes en verde sin modificarlas; con los servicios en marcha, ejecutar la sección B de [quickstart.md](quickstart.md) y confirmar `DeliveryCount = 1`, motivo y detalle en la DLQ, y la transferencia cancelada cuando el `CorrelationId` es legible **y no hay registro previo**; comprobar además el escenario de seguridad de la misma sección (mensaje defectuoso con el `CorrelationId` de una transferencia ya `Completed` ⇒ DLQ y **ningún** `*-failed` ni correo) (depende de T018 a T021)

**Checkpoint**: la historia 1 funciona y se prueba de forma independiente. **Es el MVP.**

---

## Phase 4: User Story 2 - Un fallo transitorio se reintenta con espera creciente (Priority: P1)

**Goal**: un fallo transitorio de un mensaje válido se reintenta con espera creciente y variación aleatoria, sin agotar las 10
entregas en segundos y sin perder el mensaje si el servicio se reinicia.

**Independent Test**: dejar `BankTransfer` fuera de línea ~90 s con una transferencia en curso; al volver, termina `Completed`, la DLQ
queda vacía y el registro muestra reintentos con esperas crecientes.

### Tests for User Story 2 (escribir primero; deben FALLAR)

- [X] T023 [P] [US2] `Resilience/RetryPolicyTests.cs` en los cuatro proyectos de pruebas sobre `RetryPolicy.NextDelay(int retry, Random random)` (retry de 1 a 5): con la semilla neutra, el valor está en `[0,8 × nominal ; 1,2 × nominal]` para la tabla 5 s / 15 s / 45 s / 135 s / 300 s; con un `Random` que devuelve siempre el mínimo y siempre el máximo, se obtiene exactamente `0,8×` y `1,2×`; los nominales son estrictamente crecientes hasta el tope; la suma de los cinco nominales es de al menos 300 s (SC-003: ventana acumulada ≥ 5 min); el reintento 5 no supera `300 × 1,2`; `retry` menor que 1 o mayor que `MAX_RETRIES` lanza `ArgumentOutOfRangeException`
- [X] T024 [P] [US2] `Resilience/TransientRetryTests.cs` en los cuatro proyectos sobre `ResilientMessageProcessor` con un `process` que lanza `TimeoutException` y un `Random` con semilla fija: con `RetryAttempt = 0`, `Calls` es exactamente `["ScheduleRetry:1", "Complete"]` **en ese orden** y `Delay` dentro del rango del reintento 1; con `RetryAttempt = 3` programa el intento 4 con el rango correspondiente; si `ScheduleRetryAsync` lanza, la excepción se propaga y **no** se llama `Complete`; con el token de cancelación cancelado y `process` lanzando `OperationCanceledException`, no se programa ni se completa y la excepción se propaga (FR-014); un fallo transitorio nunca llama a `DeadLetterAsync` mientras queden reintentos
- [X] T025 [US2] `Resilience/PermanentDataErrorTests.cs` en `<TRF-TESTS>` (y una equivalente en `<BAL-TESTS>` para la ruta permanente de Balance, con el `ProcessService` real, el `BalanceHarness` y una violación de NOT NULL, SQL 515, que debe emitir `balance-failed` y completar sin reintento; Transaction ya tiene cobertura en sus pruebas existentes). Para Transfer: usando `TransferHarness`, `AddAmountCheckAsync` y un `ResilientMessageProcessor` cuyo `process` ejecuta el `ProcessService` real: (a) un mensaje cuyo monto viola la restricción CHECK (SQL 547) emite `transfer-failed`, `Calls` es `["Complete"]` y **no** hay `ScheduleRetry` ni `DeadLetter` (FR-010); (b) el mismo mensaje entregado dos veces completa ambas veces sin reintento ni DLQ (violación de unicidad = "ya procesado", FR-011). Revisar primero la API de `TransferHarness` (`DeliverAsync`, `Message(...)`, `Sender`)
- [X] T026 [US2] Ejecutar T023 a T025 y **confirmar que fallan** (T023 y T024 por falta de `RetryPolicy`; T025 puede pasar por compilar ya con el procesador del andamiaje: en ese caso anotarlo y mantenerla como prueba de regresión)

### Implementation for User Story 2

- [X] T027 [P] [US2] Crear `Application/Resilience/RetryPolicy.cs` (clase estática) en los cuatro servicios con `TimeSpan NextDelay(int retry, Random random)`: `min(BASE_DELAY_SECONDS × GROWTH_FACTOR^(retry−1), MAX_DELAY_SECONDS) × u`, con `u = 1 − JITTER + random.NextDouble() × 2 × JITTER`; función pura, sin reloj; validar `retry` entre 1 y `MAX_RETRIES`
- [X] T028 [P] [US2] Ampliar `ResilientMessageProcessor.HandleAsync` en los cuatro servicios: envolver `process` en `try/catch (Exception)`; si `ct.IsCancellationRequested` (cierre ordenado), relanzar sin programar ni completar; si `RetryAttempt < MAX_RETRIES`, calcular `RetryPolicy.NextDelay(RetryAttempt + 1, _random)`, `await actions.ScheduleRetryAsync(RetryAttempt + 1, delay, ct)` y **después** `await actions.CompleteAsync(ct)`; si `RetryAttempt >= MAX_RETRIES`, relanzar por ahora (US3 lo completa). Orden obligatorio: programar primero, completar después
- [X] T029 [US2] Ejecutar T023 a T025 en verde y las suites existentes en verde; con los servicios en marcha, ejecutar la sección C de [quickstart.md](quickstart.md) (`ALTER DATABASE BankTransfer SET OFFLINE …` ~90 s) incluido el paso de **reinicio del servicio durante la espera** (FR-006), y confirmar `Completed`, DLQ vacía y esperas crecientes en el registro (depende de T027 y T028)

**Checkpoint**: las historias 1 y 2 funcionan de forma independiente.

---

## Phase 5: User Story 3 - Al agotarse los reintentos, la transferencia se compensa y el mensaje queda explicado (Priority: P2)

**Goal**: si el fallo transitorio persiste más allá de la ventana de reintentos, se emite el `*-failed` del primer paso y el mensaje
se aparta con motivo `RetriesExhausted`; si no se puede compensar, se aparta como `CompensationFailed` sin perderse.

**Independent Test**: dejar `BankTransfer` fuera de línea más de ~9 min con una transferencia en curso: Transaction queda `Canceled`
y el mensaje original está en la DLQ con motivo `RetriesExhausted`.

### Tests for User Story 3 (escribir primero; deben FALLAR)

- [X] T030 [P] [US3] `Resilience/ExhaustedRetriesTests.cs` en los cuatro proyectos sobre `ResilientMessageProcessor` con un `process` que lanza `TimeoutException`: con `RetryAttempt = 5` en un mensaje de primer paso, se llama al compensador una vez, `Calls` es exactamente `["DeadLetter:RetriesExhausted"]` con `Attempts = 6`, `Detail` que contiene el número de intentos y el tipo de la última excepción y **nunca** supera 1 024 caracteres (probar con una excepción de mensaje muy largo), sin `ScheduleRetry` ni `Complete`. Casos adicionales: suscripción que solo actualiza estado (p. ej. `balance-confirmed` en Transaction, `transfer-confirmed-balance` en Balance) → el compensador devuelve `false` y solo hay `DeadLetter:RetriesExhausted`; con un compensador que devuelve `false` por FR-016 (ya hay registro del `CorrelationId`) el mensaje se aparta con `RetriesExhausted` sin ningún efecto adicional; en Notification → solo `DeadLetter:RetriesExhausted`; si el compensador lanza (el bus no responde) → `DeadLetter:CompensationFailed` con detalle que menciona el fallo de compensación y el error original; el mismo `CompensationFailed` ocurre en la ruta de un venenoso con `CorrelationId` legible (FR-009)
- [X] T031 [US3] Ejecutar T030 y **confirmar que falla** (el procesador relanza al agotar y propaga las excepciones del compensador)

### Implementation for User Story 3

- [X] T032 [P] [US3] Ampliar `ResilientMessageProcessor` en los cuatro servicios: cuando `RetryAttempt >= MAX_RETRIES` en un fallo transitorio, `try { await compensator.CompensateAsync(...) } catch → DeadLetterAsync(COMPENSATION_FAILED, ...)`; si compensó o no aplica, `DeadLetterAsync(RETRIES_EXHAUSTED, detalle, correlationId, attempts: RetryAttempt + 1)`; el detalle sigue la forma "Reintentos agotados tras {n} intentos. Último error: {Tipo}: {mensaje}" truncado a `MAX_DETAIL_LENGTH`; aplicar la misma captura de la excepción del compensador a la ruta de venenosos de T020. Un `CorrelationId` no legible no llama al compensador
- [X] T033 [US3] Ejecutar T030 en verde y las suites existentes en verde; ejecutar la sección D de [quickstart.md](quickstart.md) (base fuera de línea > 9 min) o, si el tiempo no lo permite, dejar constancia de que la lógica está cubierta por T030 y probar la ruta en el emulador forzando `RetryAttempt = 5` con un mensaje enviado a mano (depende de T032)

**Checkpoint**: las historias 1 a 3 funcionan de forma independiente.

---

## Phase 6: User Story 4 - Los errores del procesador se registran con su contexto (Priority: P2)

**Goal**: todo error de procesamiento y del procesador queda registrado con origen, entidad y, siempre que se pueda, `CorrelationId`,
tipo de mensaje, intento y clasificación.

**Independent Test**: provocar un error de procesamiento y otro de conexión; ambos aparecen en el registro con sus campos.

### Tests for User Story 4 (escribir primero; deben FALLAR)

- [X] T034 [P] [US4] `Resilience/ProcessorErrorHandlerTests.cs` en los cuatro proyectos sobre `ProcessorErrorHandler.Log(ILogger logger, Exception exception, string errorSource, string entityPath, string fullyQualifiedNamespace, bool cancellationRequested)`: registra en nivel `Error` con los parámetros `ErrorSource`, `EntityPath` y `FullyQualifiedNamespace` y la excepción adjunta; con `cancellationRequested = true` y `OperationCanceledException`, el nivel es `Debug` y no `Error` (FR-014); el manejador no recibe ni usa `IMessageActions` (no puede apartar, completar ni compensar: FR-012)
- [X] T035 [P] [US4] `Resilience/ProcessorLoggingTests.cs` en los cuatro proyectos sobre `ResilientMessageProcessor` con `CapturingLogger<ResilientMessageProcessor>`: un venenoso produce una entrada `Error` con `CorrelationId`, `MessageId`, `MessageType`, `Attempt`, `Classification = "Poison"` y `Reason = "PoisonMessage"`; un transitorio con reintento produce una entrada `Warning` con `Classification = "Transient"`, `Attempt`, `DelaySeconds` y `CorrelationId`; reintentos agotados produce `Error` con `Reason = "RetriesExhausted"`; un mensaje sin `CorrelationId` legible registra `CorrelationId = null` y `MessageId` presente (FR-013). Nombres de parámetros exactamente como en [contracts/message-handling.md](contracts/message-handling.md) §4
- [X] T036 [US4] Ejecutar T034 y T035 y **confirmar que fallan**

### Implementation for User Story 4

- [X] T037 [P] [US4] Crear `Application/Resilience/ProcessorErrorHandler.cs` (clase estática con `Log(...)`, firma de T034) en los cuatro servicios
- [X] T038 [P] [US4] Añadir en `ResilientMessageProcessor` de los cuatro servicios las entradas de registro de [contracts/message-handling.md](contracts/message-handling.md) §4 (plantillas con parámetros nombrados `{CorrelationId}`, `{MessageId}`, `{MessageType}`, `{Attempt}`, `{Classification}`, `{Reason}`, `{DelaySeconds}`), sin cambiar las decisiones de T020, T028 y T032
- [X] T039 [P] [US4] Modificar `ProcessError` en `External/ServiceBusRecive/ServiceBusReciveService.cs` de los cuatro servicios para llamar a `ProcessorErrorHandler.Log` con `args.Exception`, `args.ErrorSource.ToString()`, `args.EntityPath`, `args.FullyQualifiedNamespace` y `args.CancellationToken.IsCancellationRequested`, inyectando `ILogger<ServiceBusReciveService>`. Devolver `Task.CompletedTask`; **no** apartar ni completar nada aquí
- [X] T040 [US4] Ejecutar T034 y T035 en verde; ejecutar la sección E de [quickstart.md](quickstart.md) (reiniciar el emulador y detener un servicio con Ctrl+C) y confirmar que el error del procesador se registra y que el cierre ordenado no cuenta como fallo (depende de T037 a T039)

**Checkpoint**: las cuatro historias funcionan de forma independiente.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: CI, gobierno, herramientas, documentación y verificación final

- [X] T041 Ampliar la matriz de `.github/workflows/ci.yml` a `service: [Transfer, Transaction, Balance, Notification]` (los nombres de job resultan `Tests (Balance)` y `Tests (Notification)`); actualizar los comentarios del encabezado que listan los checks exigidos y aclarar que `Tests (Balance)`, igual que Transfer y Transaction, usa Docker del runner (Testcontainers) mientras que `Tests (Notification)` no
- [X] T042 Enmendar `.specify/memory/constitution.md` a la versión **1.2.0** (MINOR): la regla de flujo que nombra los checks exigidos pasa a `Build`, `Tests (Transfer)`, `Tests (Transaction)`, `Tests (Balance)` y `Tests (Notification)`; actualizar *Last Amended*, el informe de impacto y las brechas del Principio III (cerradas para los primeros pasos; restan los manejadores de solo-actualización). **Requiere aprobación expresa del responsable del repositorio antes de aplicarla**
- [ ] T043 **[MANUAL — responsable del repositorio]** En GitHub, ajustar la protección de `main` y `develop` para exigir también `Tests (Balance)` y `Tests (Notification)`. Solo se pueden elegir después de que corran una vez; los nombres deben coincidir exactamente
- [ ] T044 [P] Extender `infra/local/duplicate-test/Program.cs` con una opción para enviar un cuerpo arbitrario y el `Subject` que se indique (necesaria para la sección B de [quickstart.md](quickstart.md)); documentar el uso en el encabezado del archivo
- [ ] T045 [P] Actualizar `CLAUDE.md`: nueva sección "Resiliencia en el consumo" (clasificación en tres clases, política `5 s·3^(n−1)` tope 5 min ±20 %, motivos de DLQ, compensación de primer paso, `ProcessError` solo registra, `RetryAttempt`), lista de proyectos de pruebas (4), checks de CI (5) y la deuda técnica (retirar "reintentos inmediatos sin espera" y `ProcessError` vacío; mantener los manejadores de solo-actualización sin compensar)
- [ ] T046 [P] Actualizar `README.md` (**UTF-16 LE con BOM**, finales LF; editarlo con PowerShell conservando la codificación): sección de pruebas con los 4 proyectos, tabla de CI con 5 checks, subsección de resiliencia y "Problemas conocidos" sin los puntos resueltos
- [ ] T047 [P] Actualizar los documentos de estudio de `C:\Users\jdroj\OneDrive\Documentos\CV\` (regla permanente): marcar los hallazgos 1 y 4 como resueltos en `HALLAZGOS-PENDIENTES-Bank.md` y `GUIA-ESTUDIO-Hallazgos-Bank.md` con fecha, commit, qué se hizo, verificación y frase de entrevista; añadir la entrada en `Plan_de_estudio.md`. Fuera del repositorio: no se commitean
- [ ] T048 Verificación final: `dotnet build Bank.slnx -c Release` sin errores ni avisos NU1902/NU1903; `dotnet test` de los 4 proyectos en verde; `git diff --stat origin/develop -- tests/Bank.Transfer.Api.Tests tests/Bank.Transaction.Api.Tests` muestra **solo archivos añadidos** (ninguna prueba existente modificada, SC-007); `dotnet list Bank.slnx package --vulnerable --include-transitive` limpio; `git diff origin/develop -- infra/local/Config.json` vacío
- [ ] T049 Ejecutar de punta a punta las secciones A, B, C y E de [quickstart.md](quickstart.md) (D si el tiempo lo permite) y anotar los resultados y las observaciones (p. ej. si el emulador conserva las propiedades de `DeadLetterMessageAsync`, research.md R7 [VERIFICAR])

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: sin dependencias
- **Foundational (Phase 2)**: depende de Setup; **bloquea** todas las historias
- **US1 (Phase 3)**: depende de Foundational. **MVP**
- **US2 (Phase 4)**: depende de Foundational; comparte `ResilientMessageProcessor.cs` con US1, por lo que sus tareas T028 vienen **después** de T020 en cada servicio
- **US3 (Phase 5)**: depende de US1 (compensador) y de US2 (reintentos)
- **US4 (Phase 6)**: depende de Foundational; añade registro sobre el procesador de US1 a US3, por lo que T038 viene después de T020, T028 y T032
- **Polish (Phase 7)**: depende de las historias deseadas; T042 y T043 son decisiones del responsable

### Within Each Story

- Las pruebas se escriben primero y deben **fallar** (T017, T026, T031, T036) antes de implementar
- Constantes y contratos → pruebas → implementación → verificación
- Un mismo archivo por servicio no se edita en paralelo (`ResilientMessageProcessor.cs`, `ServiceBusReciveService.cs`, `Program.cs`): las tareas que lo tocan van en orden entre fases, pero los cuatro servicios entre sí sí son paralelos

### Parallel Opportunities

- T001/T002; T005 a T007; T008; T009 a T012 son [P] entre servicios y entre archivos distintos
- Dentro de cada historia, todas las pruebas [P] (archivos distintos) y todas las implementaciones [P] (un archivo por servicio) pueden avanzar a la vez
- Los cuatro servicios son independientes entre sí: una persona por servicio es viable (Notification es el más simple: sin compensador real ni base relacional)
- T044 a T047 en paralelo

---

## Parallel Example: User Story 1

```bash
# Pruebas de US1 en paralelo (archivos distintos):
Task: "MessageClassifierTests en los 4 proyectos de pruebas"
Task: "PoisonMessageTests en los 4 proyectos de pruebas"
Task: "FailureCompensatorTests en Transaction, Balance y Transfer"

# Implementación de US1 en paralelo (un archivo por servicio):
Task: "MessageClassifier en los 4 servicios"
Task: "FailureCompensator en Transaction, Balance y Transfer"
```

---

## Implementation Strategy

### MVP First (User Story 1)

1. Phase 1 (Setup) y Phase 2 (Foundational): el comportamiento no cambia y el flujo sigue completando (T013)
2. Phase 3 (US1): mensajes venenosos a la DLQ con motivo, y cancelación de la transferencia cuando hay `CorrelationId`
3. **PARAR y validar**: sección B de quickstart.md y las suites en verde

### Incremental Delivery

1. Setup + Foundational → andamiaje sin cambios de comportamiento
2. + US1 → venenosos apartados (MVP)
3. + US2 → reintentos con espera (cierra la causa de las transferencias perdidas por caídas breves)
4. + US3 → cierre garantizado al agotarse los reintentos
5. + US4 → observabilidad completa
6. Polish → CI de 5 checks, constitución 1.2.0, documentación

### Notas

- Si se prefiere acortar el alcance: US1 + US2 en Transfer y Transaction primero (los servicios con pruebas y más cercanos al flujo), y
  Balance y Notification después. Hay que decidirlo antes de empezar T001, porque cambia qué proyectos de pruebas se crean
- Commit después de cada fase **solo si el responsable del repositorio lo indica**
- Antes de abrir el pull request: todas las pruebas en rojo-verde verificadas, los 5 checks de CI en verde, y T042/T043 resueltas si se quiere que los checks nuevos sean obligatorios
