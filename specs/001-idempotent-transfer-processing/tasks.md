---

description: "Lista de tareas: Procesamiento idempotente de transferencias"
---

# Tasks: Procesamiento idempotente de transferencias

**Input**: Documentos de diseño en `/specs/001-idempotent-transfer-processing/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/events.md, quickstart.md

**Tests**: INCLUIDAS y obligatorias. La spec las exige (SC-007) y la constitución las impone (Principio V, NON-NEGOTIABLE):
cada historia empieza por pruebas que deben **fallar** antes de implementar.

**Organization**: Las tareas se agrupan por historia de usuario. US1 (Transfer) y US2 (Transaction) son P1 y viven en servicios distintos,
por lo que pueden avanzar en paralelo; US3 (P2) extiende a US1.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: se puede ejecutar en paralelo (archivos distintos, sin dependencias pendientes)
- **[Story]**: US1, US2 o US3 (solo en fases de historia)
- Todas las rutas son relativas a la raíz del repositorio

## Path Conventions

- Servicios: `src/Bank.Transfer/Bank.Transfer.Api/` y `src/Bank.Transaction/Bank.Transaction.Api/` (a continuación `<TRANSFER>` y `<TRANSACTION>`)
- Pruebas: `tests/Bank.Transfer.Api.Tests/` y `tests/Bank.Transaction.Api.Tests/` (un proyecto por servicio; ninguno referencia a más de un servicio)
- Estilo del repo: namespaces *file-scoped*, constantes `UPPER_SNAKE_CASE`, un `Execute` por servicio, sufijos `Service`/`Handler`
- Los archivos existentes usan CRLF y BOM: conservarlos al editar
- Los mensajes de registro y de excepción siguen en español, como el resto del repo
- **No hacer commits** hasta que el responsable del repositorio lo indique de forma expresa

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: crear los proyectos de pruebas que hoy no existen

- [X] T001 Crear `tests/Bank.Transfer.Api.Tests/Bank.Transfer.Api.Tests.csproj` (`net10.0`, nullable e implicit usings, `IsPackable=false`) con xUnit, `Microsoft.NET.Test.Sdk`, el runner de xUnit para Visual Studio y `Testcontainers.MsSql`; añadir **solo** `ProjectReference` a `src/Bank.Transfer/Bank.Transfer.Api/Bank.Transfer.Api.csproj`. Fijar con `dotnet add package` la última versión estable compatible con `net10.0` y anotar las versiones elegidas en un comentario del `.csproj` (no verificadas aún; ver research.md R7)
- [X] T002 [P] Crear `tests/Bank.Transaction.Api.Tests/Bank.Transaction.Api.Tests.csproj` con la misma configuración y paquetes que T001; `ProjectReference` **solo** a `src/Bank.Transaction/Bank.Transaction.Api/Bank.Transaction.Api.csproj`
- [X] T003 Añadir ambos proyectos de pruebas a `Bank.slnx` dentro de un nuevo `<Folder Name="/tests/">` (depende de T001 y T002)
- [X] T004 Comprobar que `dotnet build Bank.slnx` compila y que `dotnet test` descubre 0 pruebas sin error en ambos proyectos (depende de T003)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: esquema de datos, constantes e infraestructura de pruebas que necesitan todas las historias

**⚠️ CRITICAL**: ninguna historia puede empezar hasta terminar esta fase

### Transfer: esquema y constantes

- [X] T005 [P] Crear `<TRANSFER>/Domain/Entities/TransferRequestEntity.cs` con los campos de data-model.md: `Id` int identity, PK; `CorrelationId` `nvarchar(64)` NOT NULL; `Outcome` `nvarchar(16)` NOT NULL; `PayloadFingerprint` `nvarchar(64)` NOT NULL; `CreatedAt` `datetime2` NOT NULL; `ClaimedAt` `datetime2` NULL; `EmittedAt` `datetime2` NULL (nulables como `DateTime?`)
- [X] T006 [P] Añadir en `<TRANSFER>/Domain/Constants/Constants.cs` tres clases: `TransferOutcomeConstants` (`CONFIRMED = "Confirmed"`, `FAILED = "Failed"`), `IgnoredMessageReasonConstants` (`DUPLICATE_ALREADY_EMITTED = "DuplicateAlreadyEmitted"`, `DUPLICATE_IN_PROGRESS = "DuplicateInProgress"`, `CONTENT_MISMATCH = "ContentMismatch"`, `INVALID_MESSAGE = "InvalidMessage"`, `RECOVERED_EMISSION = "RecoveredEmission"`) y `EmissionConstants` (`EMISSION_LEASE_SECONDS = 30`)
- [X] T007 Añadir `DbSet<TransferRequestEntity> TransferRequest { get; set; }` a `<TRANSFER>/Application/Database/IDatabaseService.cs` (depende de T005)
- [X] T008 Configurar en `<TRANSFER>/Persistence/Database/DatabaseService.cs` (`OnModelCreating` y `DbSet`): `Transfer.CorrelationId` con `HasMaxLength(64)` e índice único `IX_Transfer_CorrelationId`; `TransferRequest.CorrelationId` con `HasMaxLength(64)` e índice único `IX_TransferRequest_CorrelationId`; `Outcome` `HasMaxLength(16)`; `PayloadFingerprint` `HasMaxLength(64)`; implementar el `DbSet` de T007 (depende de T005 y T007)
- [X] T009 [P] Añadir `IsUniqueViolation(Exception)` a `<TRANSFER>/Application/Features/Process/DatabaseErrors.cs`: `DbUpdateException` cuya `SqlException` tenga número 2601 o 2627 (no añadirlos a `PermanentSqlErrors`; ver el comentario existente)
- [X] T010 Generar la migración con `dotnet tool run dotnet-ef migrations add AddIdempotency --project src/Bank.Transfer/Bank.Transfer.Api --output-dir Persistence/Migrations` y revisar que: altere `Transfer.CorrelationId` a `nvarchar(64)`, cree `IX_Transfer_CorrelationId`, cree la tabla `TransferRequest` con sus columnas e índice único, y que `Down` revierta todo (depende de T008)

### Transaction: esquema y constantes

- [X] T011 [P] Añadir en `<TRANSACTION>/Domain/Constants/Constant.cs` la clase `IgnoredMessageReasonConstants` (`FINAL_STATE_PROTECTED = "FinalStateProtected"`, `NO_SUCH_TRANSACTION = "NoSuchTransaction"`, `INVALID_MESSAGE = "InvalidMessage"`)
- [X] T012 [P] Añadir `IsUniqueViolation(Exception)` (SQL 2601 o 2627) a `<TRANSACTION>/Application/Features/Process/DatabaseErrors.cs`, sin incluirlos en `PermanentSqlErrors`
- [X] T013 Configurar en `<TRANSACTION>/Persistence/Database/DatabaseService.cs`: `Transaction.CorrelationId` con `HasMaxLength(64)` (`nvarchar(64)` NOT NULL) e índice único `IX_Transaction_CorrelationId`
- [X] T014 Generar la migración con `dotnet tool run dotnet-ef migrations add AddCorrelationIdUniqueness --project src/Bank.Transaction/Bank.Transaction.Api --output-dir Persistence/Migrations` y revisar que altere la columna a `nvarchar(64)`, cree el índice único y que `Down` revierta (depende de T013)

### Infraestructura de pruebas (por servicio)

- [X] T015 [P] Crear `tests/Bank.Transfer.Api.Tests/Infrastructure/SqlServerFixture.cs`: fixture de colección de xUnit que arranca **un** contenedor `Testcontainers.MsSql` con la imagen `mcr.microsoft.com/mssql/server:2022-latest` y expone `CreateDatabaseAsync()`, que crea una base con nombre único por prueba y le aplica las **migraciones reales** con `DatabaseService.Database.MigrateAsync()` (`Bank.Transfer.Api.Persistence.Database`)
- [X] T016 [P] Crear `tests/Bank.Transfer.Api.Tests/Infrastructure/FakeServiceBusSender.cs` implementando `IServiceBusSenderService` (namespace `Bank.Transfer.Api.Application.External.ServiceBusSender`): guarda cada llamada `(subscriptionName, eventModel)`, es *thread-safe* y tiene `FailNextSend()` para forzar una excepción en el siguiente envío
- [X] T017 [P] Crear `tests/Bank.Transfer.Api.Tests/Infrastructure/CapturingLogger.cs`: `ILogger<T>` que guarda nivel, mensaje y las propiedades estructuradas (`CorrelationId`, `MessageType`, `Reason`) para poder afirmar sobre los registros
- [X] T018 Crear `tests/Bank.Transfer.Api.Tests/Infrastructure/TransferHarness.cs`: construye un `ProcessService` con un `DatabaseService` nuevo por llamada (simula un *scope* por entrega), el sender falso y el logger capturador; incluye un constructor de mensajes JSON de `transfer-initiated` (`CorrelationId`, `Amount`, `SourceAccount`, `DestinationAccount`, `CustomerId`) y ayudas SQL para contar filas, añadir un `CHECK` y retrasar `ClaimedAt` (depende de T015, T016, T017)
- [X] T019 [P] Crear `tests/Bank.Transaction.Api.Tests/Infrastructure/SqlServerFixture.cs` con el mismo comportamiento que T015, aplicando las migraciones reales de Transaction (`Bank.Transaction.Api.Persistence.Database`)
- [X] T020 [P] Crear `tests/Bank.Transaction.Api.Tests/Infrastructure/FakeServiceBusSender.cs` implementando `IServiceBusSenderService` (namespace `Bank.Transaction.API.Application.External.ServiceBusSender`, con `API` en mayúsculas), con las mismas capacidades que T016
- [X] T021 [P] Crear `tests/Bank.Transaction.Api.Tests/Infrastructure/CapturingLogger.cs` con el mismo comportamiento que T017
- [X] T022 Crear `tests/Bank.Transaction.Api.Tests/Infrastructure/TransactionHarness.cs`: construye `ProcessService` como en T018; incluye constructores de mensajes JSON para `transaction-initiated`, `balance-confirmed`, `balance-failed`, `transfer-failed` y `transfer-confirmed`, y `SeedTransactionAsync(estado)` que inserta una transacción en `Pending`, `Completed` o `Canceled` (depende de T019, T020, T021)

**Checkpoint**: esquema, constantes e infraestructura de pruebas listos; las historias pueden empezar

---

## Phase 3: User Story 1 - Un mensaje de inicio repetido no duplica la transferencia (Priority: P1) 🎯 MVP

**Goal**: ante entregas repetidas o simultáneas de `transfer-initiated` con el mismo `CorrelationId`, existe una sola transferencia y se emite un solo resultado (confirmado o fallo), incluidos los rechazos.

**Independent Test**: entregar el mismo mensaje varias veces (serie y paralelo) y comprobar 1 fila en `Transfer`, 1 fila en `TransferRequest` y 1 evento emitido; repetir con un rechazo y comprobar 1 `transfer-failed`.

### Tests for User Story 1 (escribir primero; deben FALLAR antes de implementar) ⚠️

- [X] T023 [P] [US1] Crear `tests/Bank.Transfer.Api.Tests/TransferInitiated/DuplicateDeliveryTests.cs`: teoría con 2 a 10 entregas en serie del mismo mensaje ⇒ 1 fila en `Transfer`, 1 en `TransferRequest`, exactamente un `transfer-confirmed`, y el registro original sin cambios (`Id`, `TransferDate`, `Amount`) (escenarios 1 y 4; SC-001, SC-002)
- [X] T024 [P] [US1] Crear `tests/Bank.Transfer.Api.Tests/TransferInitiated/ConcurrentDeliveryTests.cs`: 2 a 10 entregas **en paralelo** (cada una con su propio `DatabaseService`) repetidas en 20 iteraciones con identificadores distintos ⇒ siempre 1 fila y 1 evento (escenario 2)
- [X] T025 [P] [US1] Crear `tests/Bank.Transfer.Api.Tests/TransferInitiated/RejectedTransferTests.cs`: añadir `CHECK (Amount < 20000)` a `Transfer`, enviar `Amount = 25000` ⇒ 1 `transfer-failed`, `TransferRequest.Outcome = "Failed"`, 0 filas en `Transfer`; repetir 3 veces ⇒ sigue habiendo un solo `transfer-failed` (escenario 3)
- [X] T026 [P] [US1] Crear `tests/Bank.Transfer.Api.Tests/TransferInitiated/InvalidAndMismatchedMessageTests.cs`: mensajes sin `CorrelationId`, con identificador vacío o de más de 64 caracteres, JSON inválido y cuerpo `null` ⇒ 0 filas, 0 eventos y registro `InvalidMessage`; mismo `CorrelationId` con otro `Amount` ⇒ prevalece el primero, 0 eventos nuevos y registro `ContentMismatch` (FR-008, FR-010)
- [X] T027 [P] [US1] Crear `tests/Bank.Transfer.Api.Tests/TransferInitiated/SchemaTests.cs`: tras aplicar las migraciones existen los índices únicos `IX_Transfer_CorrelationId` e `IX_TransferRequest_CorrelationId`, `CorrelationId` es `nvarchar(64)` y un segundo `INSERT` con el mismo identificador falla con número SQL 2627 o 2601
- [X] T028 [US1] Ejecutar `dotnet test tests/Bank.Transfer.Api.Tests` y confirmar que las pruebas de T023 a T027 **fallan** por la causa esperada (no por errores de compilación); guardar el resumen del resultado en el informe de la sesión (depende de T023 a T027)

### Implementation for User Story 1

> T029 a T032 editan el mismo archivo, `<TRANSFER>/Application/Features/Process/ProcessService.cs`: son secuenciales.

- [X] T029 [US1] Reescribir el inicio de `TransferInitiated` en `<TRANSFER>/Application/Features/Process/ProcessService.cs`: validar el mensaje (cuerpo interpretable, `CorrelationId` no vacío y de **máximo 64 caracteres**); si no es válido, registrar con `ILogger` en nivel Warning (`CorrelationId`, `MessageType = "transfer-initiated"`, `Reason = INVALID_MESSAGE`), no escribir nada y terminar normalmente (el mensaje se completa); calcular `PayloadFingerprint` como SHA-256 en hexadecimal de `Amount|SourceAccount|DestinationAccount|CustomerId` (`Amount` formateado con `F2` y cultura invariante) (depende de T006, T008, T028)
- [X] T030 [US1] En el mismo archivo, implementar la rama "no existe `TransferRequest`": en **una sola** `SaveAsync` insertar `Transfer` y `TransferRequest` (`Outcome = CONFIRMED`, `CreatedAt` y `ClaimedAt` = `DateTime.UtcNow`, `EmittedAt = NULL`); si lanza violación de unicidad (`DatabaseErrors.IsUniqueViolation`), registrar `DUPLICATE_IN_PROGRESS` y terminar; si lanza error permanente (`DatabaseErrors.IsPermanent`), `ChangeTracker.Clear()` e insertar solo `TransferRequest` con `Outcome = FAILED` (tratando de nuevo la unicidad como duplicado); un error transitorio se propaga sin capturar (depende de T029)
- [X] T031 [US1] En el mismo archivo, implementar la rama "ya existe `TransferRequest`": si `EmittedAt` está informado registrar `DUPLICATE_ALREADY_EMITTED` y terminar; si no está emitido y `ClaimedAt` es posterior a `ahora - EMISSION_LEASE_SECONDS` registrar `DUPLICATE_IN_PROGRESS` y terminar; en ambos casos, si la huella difiere de la guardada registrar además `CONTENT_MISMATCH` y no modificar nada (depende de T030)
- [X] T032 [US1] En el mismo archivo, emitir el resultado según `Outcome` (`transfer-confirmed` o `transfer-failed`, mismo payload `{ CorrelationId, CustomerId }`) y marcar `EmittedAt` con `ExecuteUpdateAsync` condicional (`WHERE Id = @id AND EmittedAt IS NULL`); mantener la compensación existente y los registros de error con el `CorrelationId` (depende de T031)
- [X] T033 [US1] Ejecutar `dotnet test tests/Bank.Transfer.Api.Tests` y confirmar que T023 a T027 pasan; ejecutar `dotnet build src/Bank.Transfer/Bank.Transfer.Api` sin errores ni advertencias nuevas (depende de T032)

**Checkpoint**: US1 funciona y se prueba de forma independiente (el riesgo de cobro doble queda cerrado)

---

## Phase 4: User Story 2 - Una transacción finalizada nunca vuelve a Pendiente (Priority: P1)

**Goal**: `Completed` y `Canceled` son estados finales inmutables: ningún mensaje duplicado o tardío los cambia ni dispara eventos nuevos; las transiciones válidas desde `Pending` siguen funcionando.

**Independent Test**: sembrar transacciones en `Completed` y en `Canceled`, entregar cada tipo de mensaje y comprobar que el estado no cambia y que no se emite ningún evento.

### Tests for User Story 2 (escribir primero; deben FALLAR antes de implementar) ⚠️

- [X] T034 [P] [US2] Crear `tests/Bank.Transaction.Api.Tests/StateProtection/FinalStateProtectionTests.cs`: teoría 2 estados finales × 5 mensajes (`transaction-initiated`, `balance-confirmed`, `balance-failed`, `transfer-failed`, `transfer-confirmed`) ⇒ `CurrentState` sin cambios (incluido no pasar al otro estado final), 0 eventos emitidos y registro `FinalStateProtected` con el `CorrelationId` (escenarios 1 a 3; SC-003)
- [X] T035 [P] [US2] Crear `tests/Bank.Transaction.Api.Tests/StateProtection/ValidTransitionTests.cs`: desde `Pending`, `balance-failed` ⇒ `Canceled` y `transaction-failed`; `transfer-failed` ⇒ `Canceled`, `transaction-failed` y `transfer-failed-balance`; `transfer-confirmed` ⇒ `Completed`, `transaction-completed` y `transfer-confirmed-balance`; `balance-confirmed` ⇒ `Pending` y `transfer-initiated` con `Amount`, cuentas y cliente; primer `transaction-initiated` ⇒ fila `Pending` y `balance-initiated`; `transaction-initiated` repetido en `Pending` ⇒ vuelve a emitir `balance-initiated` sin cambiar el estado (escenario 4)
- [X] T036 [P] [US2] Crear `tests/Bank.Transaction.Api.Tests/StateProtection/OrphanAndInvalidMessageTests.cs`: cada mensaje distinto de `transaction-initiated` sin fila existente ⇒ 0 filas creadas, 0 eventos, registro `NoSuchTransaction`; mensajes sin `CorrelationId` o de más de 64 caracteres ⇒ sin cambios y registro `InvalidMessage`
- [X] T037 [P] [US2] Crear `tests/Bank.Transaction.Api.Tests/StateProtection/ConcurrencyTests.cs`: varios `transaction-initiated` en paralelo ⇒ 1 fila; varios `transfer-confirmed` en paralelo ⇒ `Completed`; `transfer-confirmed` y `transfer-failed` simultáneos ⇒ exactamente un estado final y nunca `Pending` (la emisión duplicada en esta ventana es un riesgo aceptado, ver research.md R4: afirmar solo sobre el estado)
- [X] T038 [P] [US2] Crear `tests/Bank.Transaction.Api.Tests/StateProtection/EmissionFailureTests.cs`: el sender falla en `transfer-confirmed` ⇒ la excepción se propaga y el estado sigue en `Pending`; la reentrega emite y deja `Completed`; una tercera entrega se ignora sin eventos (orden "emitir y luego confirmar", research.md R4)
- [X] T039 [P] [US2] Crear `tests/Bank.Transaction.Api.Tests/StateProtection/SchemaTests.cs`: existe el índice único `IX_Transaction_CorrelationId`, `CorrelationId` es `nvarchar(64)` y un segundo `INSERT` con el mismo identificador falla con número 2627 o 2601
- [X] T040 [US2] Ejecutar `dotnet test tests/Bank.Transaction.Api.Tests` y confirmar que T034 a T039 **fallan** por la causa esperada (depende de T034 a T039)

### Implementation for User Story 2

> T041 a T044 editan el mismo archivo, `<TRANSACTION>/Application/Features/Process/ProcessService.cs`: son secuenciales.

- [X] T041 [US2] En `<TRANSACTION>/Application/Features/Process/ProcessService.cs`, añadir ayudas privadas: validar el mensaje (`CorrelationId` no vacío y de **máximo 64 caracteres**; si no, registrar `INVALID_MESSAGE` en nivel Warning y terminar sin cambios); cargar la transacción por `CorrelationId`; un método que aplique la transición condicional con `ExecuteUpdateAsync` (`WHERE CorrelationId = @id AND CurrentState = 'Pending'`) y devuelva las filas afectadas; y un método que registre el motivo de ignorado con `CorrelationId`, `MessageType` y `Reason` (depende de T011, T013, T040)
- [X] T042 [US2] En el mismo archivo, reescribir `TransactionInitiated`: si no existe, insertar `Pending` y emitir `balance-initiated`, tratando una violación de unicidad (`DatabaseErrors.IsUniqueViolation`) como "ya existe"; si existe en `Pending`, emitir de nuevo `balance-initiated` sin cambiar el estado; si existe en estado final, registrar `FINAL_STATE_PROTECTED` y no emitir; conservar la compensación existente (`DatabaseErrors.IsPermanent` ⇒ `transaction-failed`) (depende de T041)
- [X] T043 [US2] En el mismo archivo, reescribir `BalanceConfirmed`: solo con la transacción en `Pending`, emitir `transfer-initiated` con `CorrelationId`, `Amount`, `SourceAccount`, `DestinationAccount` y `CustomerId` tomados de la fila; sin fila registrar `NO_SUCH_TRANSACTION`; en estado final registrar `FINAL_STATE_PROTECTED`; en ambos casos sin eventos (depende de T042)
- [X] T044 [US2] En el mismo archivo, reescribir `BalanceFailed`, `TransferFailed` y `TransferConfirmed` con el orden **leer estado → emitir eventos → transición condicional** (`Canceled`, `Canceled` y `Completed`) y los eventos de `contracts/events.md`; si la transición devuelve 0 filas tras emitir, registrar el motivo y terminar; sin fila `NO_SUCH_TRANSACTION`; en estado final `FINAL_STATE_PROTECTED` sin eventos (depende de T043)
- [X] T045 [US2] Eliminar el método público `ProcessDataBase` (el *upsert* que regresaba estados) de `<TRANSACTION>/Application/Features/Process/ProcessService.cs` tras comprobar con una búsqueda que no queda ninguna referencia (depende de T044)
- [X] T046 [US2] Ejecutar `dotnet test tests/Bank.Transaction.Api.Tests` y confirmar que T034 a T039 pasan; `dotnet build src/Bank.Transaction/Bank.Transaction.Api` sin errores ni advertencias nuevas (depende de T045)

**Checkpoint**: US2 funciona y se prueba de forma independiente, sin depender de US1

---

## Phase 5: User Story 3 - Recuperación tras un procesamiento interrumpido (Priority: P2)

**Goal**: si el procesamiento se interrumpe tras registrar la transferencia y antes de emitir el resultado, la reentrega emite el resultado sin crear una segunda transferencia; un solo procesador recupera la emisión.

**Independent Test**: forzar un fallo del sender tras el registro y reentregar el mensaje; comprobar 1 transferencia y el resultado emitido.

### Tests for User Story 3 (escribir primero; deben FALLAR antes de implementar) ⚠️

- [X] T047 [P] [US3] Crear `tests/Bank.Transfer.Api.Tests/TransferInitiated/EmissionRecoveryTests.cs`: `FailNextSend()` y primera entrega ⇒ la excepción se propaga, hay 1 fila en `Transfer` y `ClaimedAt` queda en `NULL` con `EmittedAt` en `NULL`; la reentrega emite exactamente un `transfer-confirmed` sin crear otra fila; una tercera entrega se ignora (`DUPLICATE_ALREADY_EMITTED`) (escenarios 1 y 2; SC-004)
- [X] T048 [P] [US3] Crear `tests/Bank.Transfer.Api.Tests/TransferInitiated/LeaseTests.cs`: sembrar una solicitud registrada con `ClaimedAt` 31 s en el pasado y `EmittedAt` en `NULL` ⇒ la entrega la recupera, emite una vez y registra `RECOVERED_EMISSION`; dos entregas simultáneas tras vencer el lease ⇒ solo una emite; con `ClaimedAt` reciente la entrega se ignora (`DUPLICATE_IN_PROGRESS`); repetir para `Outcome = Failed`
- [X] T049 [US3] Ejecutar `dotnet test tests/Bank.Transfer.Api.Tests --filter "FullyQualifiedName~EmissionRecovery|FullyQualifiedName~Lease"` y confirmar que T047 y T048 **fallan** por la causa esperada (depende de T047 y T048)

### Implementation for User Story 3

> T050 y T051 editan `<TRANSFER>/Application/Features/Process/ProcessService.cs`: son secuenciales.

- [X] T050 [US3] En `<TRANSFER>/Application/Features/Process/ProcessService.cs`, envolver la emisión de T032 en `try/catch`: ante cualquier excepción, liberar el *claim* con `ExecuteUpdateAsync` (`ClaimedAt = NULL WHERE Id = @id AND EmittedAt IS NULL`) y relanzarla para que la mensajería reintente (depende de T049)
- [X] T051 [US3] En el mismo archivo, ampliar la rama "ya existe `TransferRequest`" de T031: si no está emitida y el lease está vencido (`ClaimedAt` es `NULL` o anterior a `ahora - EMISSION_LEASE_SECONDS`), tomar el *claim* con `ExecuteUpdateAsync` condicional (`ClaimedAt = ahora WHERE Id = @id AND EmittedAt IS NULL AND (ClaimedAt IS NULL OR ClaimedAt < ahora - lease)`); si afecta 1 fila, emitir el `Outcome` guardado, marcar `EmittedAt` y registrar `RECOVERED_EMISSION`; si afecta 0 filas, registrar `DUPLICATE_IN_PROGRESS` y terminar (depende de T050)
- [X] T052 [US3] Ejecutar la suite completa `dotnet test tests/Bank.Transfer.Api.Tests` y confirmar que US1 y US3 pasan juntas (depende de T051)

**Checkpoint**: las tres historias funcionan de forma independiente y en conjunto

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: documentación, herramienta de validación manual y verificación final

- [X] T053 [P] Actualizar `CLAUDE.md`: sustituir "No hay proyecto de pruebas aún" por los comandos `dotnet test tests/Bank.Transfer.Api.Tests` y `dotnet test tests/Bank.Transaction.Api.Tests` (requieren Docker), documentar la tabla `TransferRequest` y los registros de mensajes ignorados, y quitar de "Deuda técnica conocida" lo que esta feature resuelve
- [X] T054 [P] Actualizar `README.md`: añadir la sección de pruebas automáticas y el comportamiento idempotente (qué ocurre con duplicados y tardíos), y retirar de "Problemas conocidos del diseño actual" lo resuelto
- [X] T055 [P] Crear la herramienta manual `infra/local/duplicate-test/Program.cs` y `infra/local/duplicate-test/DuplicateTest.csproj` (consola, mismo estilo y cadena de emulador que `infra/local/smoke-test`; sin secretos): publica N copias de `transfer-initiated` con un `CorrelationId` dado y deja de ser parte de `Bank.slnx`
- [X] T056 Comprobar el Principio I: buscar `ProjectReference` en ambos `.csproj` de `tests/` y confirmar que cada uno referencia solo a su servicio (depende de T052 y T046)
- [X] T057 Ejecutar `dotnet build Bank.slnx` y ambos proyectos de pruebas completos; confirmar que no hay advertencias nuevas en los archivos modificados (depende de T052 y T046)
- [X] T058 Detectar duplicados previos con las consultas de `quickstart.md` sección 4, aplicar `.\infra\local\apply-migrations.ps1` y ejecutar la validación manual de `quickstart.md` secciones 2 y 3 con los servicios en el host y la herramienta de T055 (depende de T055 y T057)
- [X] T059 [P] Actualizar `C:\Users\jdroj\OneDrive\Documentos\CV\HALLAZGOS-PENDIENTES-Bank.md` y `GUIA-ESTUDIO-Hallazgos-Bank.md` (fuera del repo) marcando los hallazgos 2 y 3 como resueltos y añadiendo las decisiones de `research.md`; no tocar el repo desde esta tarea

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: sin dependencias
- **Foundational (Phase 2)**: depende de Setup; **bloquea todas las historias**. Dentro de la fase, los bloques Transfer (T005 a T010 y T015 a T018) y Transaction (T011 a T014 y T019 a T022) son independientes entre sí
- **User Stories (Phase 3+)**: dependen de la fase Foundational
  - US1 (Transfer) y US2 (Transaction) son independientes y pueden avanzar en paralelo
  - US3 depende de US1 (extiende el mismo método y archivo)
- **Polish (Phase 6)**: depende de las historias que se quieran entregar

### User Story Dependencies

- **US1 (P1)**: tras Foundational; sin dependencias de otras historias
- **US2 (P1)**: tras Foundational; sin dependencias de US1 ni de US3 (otro servicio)
- **US3 (P2)**: tras US1 (T032 y T031)

### Within Each User Story

- Las pruebas se escriben primero y deben fallar (T028, T040, T049) antes de implementar
- Esquema y constantes (Foundational) antes que la lógica
- La lógica de un mismo archivo se implementa de forma secuencial
- Cada historia termina con su suite en verde antes de pasar a la siguiente

### Parallel Opportunities

- T001 y T002 (proyectos de prueba); T005, T006, T009, T011, T012 y toda la infraestructura de pruebas por servicio (T015 a T017, T019 a T021)
- Todas las pruebas de una misma historia (T023 a T027; T034 a T039; T047 y T048) son archivos distintos
- US1 y US2 completas, en paralelo, por personas distintas
- Polish: T053, T054, T055 y T059

---

## Parallel Example: User Story 1

```bash
# Escribir juntas todas las pruebas de US1 (archivos distintos):
Task: "DuplicateDeliveryTests en tests/Bank.Transfer.Api.Tests/TransferInitiated/DuplicateDeliveryTests.cs"
Task: "ConcurrentDeliveryTests en tests/Bank.Transfer.Api.Tests/TransferInitiated/ConcurrentDeliveryTests.cs"
Task: "RejectedTransferTests en tests/Bank.Transfer.Api.Tests/TransferInitiated/RejectedTransferTests.cs"
Task: "InvalidAndMismatchedMessageTests en tests/Bank.Transfer.Api.Tests/TransferInitiated/InvalidAndMismatchedMessageTests.cs"
Task: "SchemaTests en tests/Bank.Transfer.Api.Tests/TransferInitiated/SchemaTests.cs"
```

## Parallel Example: US1 y US2 a la vez

```bash
# Tras Foundational, dos frentes independientes (servicios distintos):
Frente A: T023 a T033  (Transfer: registro idempotente)
Frente B: T034 a T046  (Transaction: protección de estados finales)
```

---

## Implementation Strategy

### MVP First (User Story 1)

1. Phase 1: Setup
2. Phase 2: Foundational (bloquea todo)
3. Phase 3: US1 (cierra el riesgo de cobro doble)
4. **PARAR y VALIDAR**: `dotnet test tests/Bank.Transfer.Api.Tests` en verde
5. Como US2 es también P1, se recomienda completarla antes de dar la feature por entregada

### Incremental Delivery

1. Setup + Foundational ⇒ base lista
2. US1 ⇒ probar de forma independiente
3. US2 ⇒ probar de forma independiente (puede ir en paralelo con US1)
4. US3 ⇒ probar con la suite completa de Transfer
5. Polish ⇒ documentación, herramienta manual y validación de punta a punta

### Notas de riesgo

- Crear los índices únicos (T010, T014) falla si ya hay `CorrelationId` repetidos en las bases locales: ejecutar antes las consultas de `quickstart.md` sección 4.
- Las pruebas requieren Docker en marcha; la primera ejecución descarga o reutiliza la imagen de SQL Server.
- Los reintentos inmediatos de la mensajería (hallazgo 1) y la ausencia de Outbox (hallazgo 5) siguen fuera de alcance: señalarlos si se tocan.

---

## Notes

- [P] = archivos distintos y sin dependencias pendientes
- La etiqueta [Story] vincula cada tarea con la historia de la spec
- Cada historia debe poder completarse y probarse de forma independiente
- Verificar que las pruebas fallan antes de implementar
- No hacer commits sin instrucción expresa del responsable del repositorio
- Evitar: tareas vagas, conflictos en un mismo archivo, dependencias entre historias que rompan la independencia
