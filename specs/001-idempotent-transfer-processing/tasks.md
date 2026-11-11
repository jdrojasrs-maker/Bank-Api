---

description: "Task list: Idempotent transfer processing"
---

# Tasks: Idempotent transfer processing

**Input**: Design documents in `/specs/001-idempotent-transfer-processing/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/events.md, quickstart.md

**Tests**: INCLUDED and mandatory. The spec requires them (SC-007) and the constitution imposes them (Principle V, NON-NEGOTIABLE):
each story starts with tests that must **fail** before implementing.

**Organization**: Tasks are grouped by user story. US1 (Transfer) and US2 (Transaction) are P1 and live in different services,
so they can advance in parallel; US3 (P2) extends US1.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no pending dependencies)
- **[Story]**: US1, US2 or US3 (only in story phases)
- All paths are relative to the repository root

## Path Conventions

- Services: `src/Bank.Transfer/Bank.Transfer.Api/` and `src/Bank.Transaction/Bank.Transaction.Api/` (below `<TRANSFER>` and `<TRANSACTION>`)
- Tests: `tests/Bank.Transfer.Api.Tests/` and `tests/Bank.Transaction.Api.Tests/` (one project per service; none references more than one service)
- Repo style: *file-scoped* namespaces, `UPPER_SNAKE_CASE` constants, one `Execute` per service, `Service`/`Handler` suffixes
- Existing files use CRLF and BOM: keep them when editing
- Log and exception messages remain in Spanish, like the rest of the repo
- **Do not make commits** until the repository owner expressly says so

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: create the test projects that do not exist today

- [X] T001 Create `tests/Bank.Transfer.Api.Tests/Bank.Transfer.Api.Tests.csproj` (`net10.0`, nullable and implicit usings, `IsPackable=false`) with xUnit, `Microsoft.NET.Test.Sdk`, the xUnit runner for Visual Studio and `Testcontainers.MsSql`; add **only** a `ProjectReference` to `src/Bank.Transfer/Bank.Transfer.Api/Bank.Transfer.Api.csproj`. Pin with `dotnet add package` the latest stable version compatible with `net10.0` and note the chosen versions in a `.csproj` comment (not verified yet; see research.md R7)
- [X] T002 [P] Create `tests/Bank.Transaction.Api.Tests/Bank.Transaction.Api.Tests.csproj` with the same configuration and packages as T001; `ProjectReference` **only** to `src/Bank.Transaction/Bank.Transaction.Api/Bank.Transaction.Api.csproj`
- [X] T003 Add both test projects to `Bank.slnx` inside a new `<Folder Name="/tests/">` (depends on T001 and T002)
- [X] T004 Check that `dotnet build Bank.slnx` compiles and that `dotnet test` discovers 0 tests without error in both projects (depends on T003)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: data schema, constants and test infrastructure that all stories need

**⚠️ CRITICAL**: no story can start until this phase is finished

### Transfer: schema and constants

- [X] T005 [P] Create `<TRANSFER>/Domain/Entities/TransferRequestEntity.cs` with the fields of data-model.md: `Id` int identity, PK; `CorrelationId` `nvarchar(64)` NOT NULL; `Outcome` `nvarchar(16)` NOT NULL; `PayloadFingerprint` `nvarchar(64)` NOT NULL; `CreatedAt` `datetime2` NOT NULL; `ClaimedAt` `datetime2` NULL; `EmittedAt` `datetime2` NULL (nullable as `DateTime?`)
- [X] T006 [P] Add three classes in `<TRANSFER>/Domain/Constants/Constants.cs`: `TransferOutcomeConstants` (`CONFIRMED = "Confirmed"`, `FAILED = "Failed"`), `IgnoredMessageReasonConstants` (`DUPLICATE_ALREADY_EMITTED = "DuplicateAlreadyEmitted"`, `DUPLICATE_IN_PROGRESS = "DuplicateInProgress"`, `CONTENT_MISMATCH = "ContentMismatch"`, `INVALID_MESSAGE = "InvalidMessage"`, `RECOVERED_EMISSION = "RecoveredEmission"`) and `EmissionConstants` (`EMISSION_LEASE_SECONDS = 30`)
- [X] T007 Add `DbSet<TransferRequestEntity> TransferRequest { get; set; }` to `<TRANSFER>/Application/Database/IDatabaseService.cs` (depends on T005)
- [X] T008 Configure in `<TRANSFER>/Persistence/Database/DatabaseService.cs` (`OnModelCreating` and `DbSet`): `Transfer.CorrelationId` with `HasMaxLength(64)` and unique index `IX_Transfer_CorrelationId`; `TransferRequest.CorrelationId` with `HasMaxLength(64)` and unique index `IX_TransferRequest_CorrelationId`; `Outcome` `HasMaxLength(16)`; `PayloadFingerprint` `HasMaxLength(64)`; implement the `DbSet` of T007 (depends on T005 and T007)
- [X] T009 [P] Add `IsUniqueViolation(Exception)` to `<TRANSFER>/Application/Features/Process/DatabaseErrors.cs`: `DbUpdateException` whose `SqlException` has number 2601 or 2627 (do not add them to `PermanentSqlErrors`; see the existing comment)
- [X] T010 Generate the migration with `dotnet tool run dotnet-ef migrations add AddIdempotency --project src/Bank.Transfer/Bank.Transfer.Api --output-dir Persistence/Migrations` and check that it: alters `Transfer.CorrelationId` to `nvarchar(64)`, creates `IX_Transfer_CorrelationId`, creates the `TransferRequest` table with its columns and unique index, and that `Down` reverts everything (depends on T008)

### Transaction: schema and constants

- [X] T011 [P] Add in `<TRANSACTION>/Domain/Constants/Constant.cs` the class `IgnoredMessageReasonConstants` (`FINAL_STATE_PROTECTED = "FinalStateProtected"`, `NO_SUCH_TRANSACTION = "NoSuchTransaction"`, `INVALID_MESSAGE = "InvalidMessage"`)
- [X] T012 [P] Add `IsUniqueViolation(Exception)` (SQL 2601 or 2627) to `<TRANSACTION>/Application/Features/Process/DatabaseErrors.cs`, without including them in `PermanentSqlErrors`
- [X] T013 Configure in `<TRANSACTION>/Persistence/Database/DatabaseService.cs`: `Transaction.CorrelationId` with `HasMaxLength(64)` (`nvarchar(64)` NOT NULL) and unique index `IX_Transaction_CorrelationId`
- [X] T014 Generate the migration with `dotnet tool run dotnet-ef migrations add AddCorrelationIdUniqueness --project src/Bank.Transaction/Bank.Transaction.Api --output-dir Persistence/Migrations` and check that it alters the column to `nvarchar(64)`, creates the unique index and that `Down` reverts (depends on T013)

### Test infrastructure (per service)

- [X] T015 [P] Create `tests/Bank.Transfer.Api.Tests/Infrastructure/SqlServerFixture.cs`: xUnit collection fixture that starts **one** `Testcontainers.MsSql` container with the image `mcr.microsoft.com/mssql/server:2022-latest` and exposes `CreateDatabaseAsync()`, which creates a database with a unique name per test and applies the **real migrations** to it with `DatabaseService.Database.MigrateAsync()` (`Bank.Transfer.Api.Persistence.Database`)
- [X] T016 [P] Create `tests/Bank.Transfer.Api.Tests/Infrastructure/FakeServiceBusSender.cs` implementing `IServiceBusSenderService` (namespace `Bank.Transfer.Api.Application.External.ServiceBusSender`): stores each call `(subscriptionName, eventModel)`, is *thread-safe* and has `FailNextSend()` to force an exception on the next send
- [X] T017 [P] Create `tests/Bank.Transfer.Api.Tests/Infrastructure/CapturingLogger.cs`: `ILogger<T>` that stores the level, the message and the structured properties (`CorrelationId`, `MessageType`, `Reason`) so that assertions can be made on the records
- [X] T018 Create `tests/Bank.Transfer.Api.Tests/Infrastructure/TransferHarness.cs`: builds a `ProcessService` with a new `DatabaseService` per call (simulates a *scope* per delivery), the fake sender and the capturing logger; includes a builder of `transfer-initiated` JSON messages (`CorrelationId`, `Amount`, `SourceAccount`, `DestinationAccount`, `CustomerId`) and SQL helpers to count rows, add a `CHECK` and delay `ClaimedAt` (depends on T015, T016, T017)
- [X] T019 [P] Create `tests/Bank.Transaction.Api.Tests/Infrastructure/SqlServerFixture.cs` with the same behavior as T015, applying Transaction's real migrations (`Bank.Transaction.Api.Persistence.Database`)
- [X] T020 [P] Create `tests/Bank.Transaction.Api.Tests/Infrastructure/FakeServiceBusSender.cs` implementing `IServiceBusSenderService` (namespace `Bank.Transaction.API.Application.External.ServiceBusSender`, with `API` in uppercase), with the same capabilities as T016
- [X] T021 [P] Create `tests/Bank.Transaction.Api.Tests/Infrastructure/CapturingLogger.cs` with the same behavior as T017
- [X] T022 Create `tests/Bank.Transaction.Api.Tests/Infrastructure/TransactionHarness.cs`: builds `ProcessService` as in T018; includes JSON message builders for `transaction-initiated`, `balance-confirmed`, `balance-failed`, `transfer-failed` and `transfer-confirmed`, and `SeedTransactionAsync(state)` which inserts a transaction in `Pending`, `Completed` or `Canceled` (depends on T019, T020, T021)

**Checkpoint**: schema, constants and test infrastructure ready; the stories can start

---

## Phase 3: User Story 1 - A repeated start message does not duplicate the transfer (Priority: P1) 🎯 MVP

**Goal**: on repeated or simultaneous deliveries of `transfer-initiated` with the same `CorrelationId`, there is a single transfer and a single result is emitted (confirmed or failure), rejections included.

**Independent Test**: deliver the same message several times (serial and parallel) and check 1 row in `Transfer`, 1 row in `TransferRequest` and 1 event emitted; repeat with a rejection and check 1 `transfer-failed`.

### Tests for User Story 1 (write first; they must FAIL before implementing) ⚠️

- [X] T023 [P] [US1] Create `tests/Bank.Transfer.Api.Tests/TransferInitiated/DuplicateDeliveryTests.cs`: theory with 2 to 10 serial deliveries of the same message ⇒ 1 row in `Transfer`, 1 in `TransferRequest`, exactly one `transfer-confirmed`, and the original record unchanged (`Id`, `TransferDate`, `Amount`) (scenarios 1 and 4; SC-001, SC-002)
- [X] T024 [P] [US1] Create `tests/Bank.Transfer.Api.Tests/TransferInitiated/ConcurrentDeliveryTests.cs`: 2 to 10 deliveries **in parallel** (each with its own `DatabaseService`) repeated in 20 iterations with different identifiers ⇒ always 1 row and 1 event (scenario 2)
- [X] T025 [P] [US1] Create `tests/Bank.Transfer.Api.Tests/TransferInitiated/RejectedTransferTests.cs`: add `CHECK (Amount < 20000)` to `Transfer`, send `Amount = 25000` ⇒ 1 `transfer-failed`, `TransferRequest.Outcome = "Failed"`, 0 rows in `Transfer`; repeat 3 times ⇒ there is still a single `transfer-failed` (scenario 3)
- [X] T026 [P] [US1] Create `tests/Bank.Transfer.Api.Tests/TransferInitiated/InvalidAndMismatchedMessageTests.cs`: messages without `CorrelationId`, with an empty identifier or one longer than 64 characters, invalid JSON and a `null` body ⇒ 0 rows, 0 events and an `InvalidMessage` record; same `CorrelationId` with another `Amount` ⇒ the first prevails, 0 new events and a `ContentMismatch` record (FR-008, FR-010)
- [X] T027 [P] [US1] Create `tests/Bank.Transfer.Api.Tests/TransferInitiated/SchemaTests.cs`: after applying the migrations the unique indexes `IX_Transfer_CorrelationId` and `IX_TransferRequest_CorrelationId` exist, `CorrelationId` is `nvarchar(64)` and a second `INSERT` with the same identifier fails with SQL number 2627 or 2601
- [X] T028 [US1] Run `dotnet test tests/Bank.Transfer.Api.Tests` and confirm that the tests of T023 to T027 **fail** for the expected cause (not because of compilation errors); save the result summary in the session report (depends on T023 to T027)

### Implementation for User Story 1

> T029 to T032 edit the same file, `<TRANSFER>/Application/Features/Process/ProcessService.cs`: they are sequential.

- [X] T029 [US1] Rewrite the start of `TransferInitiated` in `<TRANSFER>/Application/Features/Process/ProcessService.cs`: validate the message (interpretable body, non-empty `CorrelationId` of **at most 64 characters**); if it is not valid, log with `ILogger` at Warning level (`CorrelationId`, `MessageType = "transfer-initiated"`, `Reason = INVALID_MESSAGE`), write nothing and finish normally (the message is completed); compute `PayloadFingerprint` as the SHA-256 in hexadecimal of `Amount|SourceAccount|DestinationAccount|CustomerId` (`Amount` formatted with `F2` and invariant culture) (depends on T006, T008, T028)
- [X] T030 [US1] In the same file, implement the branch "`TransferRequest` does not exist": in a **single** `SaveAsync` insert `Transfer` and `TransferRequest` (`Outcome = CONFIRMED`, `CreatedAt` and `ClaimedAt` = `DateTime.UtcNow`, `EmittedAt = NULL`); if it throws a uniqueness violation (`DatabaseErrors.IsUniqueViolation`), log `DUPLICATE_IN_PROGRESS` and finish; if it throws a permanent error (`DatabaseErrors.IsPermanent`), `ChangeTracker.Clear()` and insert only `TransferRequest` with `Outcome = FAILED` (treating uniqueness again as a duplicate); a transient error propagates uncaught (depends on T029)
- [X] T031 [US1] In the same file, implement the branch "`TransferRequest` already exists": if `EmittedAt` is filled in, log `DUPLICATE_ALREADY_EMITTED` and finish; if it is not emitted and `ClaimedAt` is later than `now - EMISSION_LEASE_SECONDS`, log `DUPLICATE_IN_PROGRESS` and finish; in both cases, if the fingerprint differs from the stored one, also log `CONTENT_MISMATCH` and modify nothing (depends on T030)
- [X] T032 [US1] In the same file, emit the result according to `Outcome` (`transfer-confirmed` or `transfer-failed`, same payload `{ CorrelationId, CustomerId }`) and mark `EmittedAt` with a conditional `ExecuteUpdateAsync` (`WHERE Id = @id AND EmittedAt IS NULL`); keep the existing compensation and the error logs with the `CorrelationId` (depends on T031)
- [X] T033 [US1] Run `dotnet test tests/Bank.Transfer.Api.Tests` and confirm that T023 to T027 pass; run `dotnet build src/Bank.Transfer/Bank.Transfer.Api` with no errors or new warnings (depends on T032)

**Checkpoint**: US1 works and is tested independently (the double-charge risk is closed)

---

## Phase 4: User Story 2 - A finished transaction never goes back to Pending (Priority: P1)

**Goal**: `Completed` and `Canceled` are immutable final states: no duplicate or late message changes them or triggers new events; valid transitions from `Pending` keep working.

**Independent Test**: seed transactions in `Completed` and `Canceled`, deliver each type of message and check that the state does not change and that no event is emitted.

### Tests for User Story 2 (write first; they must FAIL before implementing) ⚠️

- [X] T034 [P] [US2] Create `tests/Bank.Transaction.Api.Tests/StateProtection/FinalStateProtectionTests.cs`: theory 2 final states × 5 messages (`transaction-initiated`, `balance-confirmed`, `balance-failed`, `transfer-failed`, `transfer-confirmed`) ⇒ `CurrentState` unchanged (including not moving to the other final state), 0 events emitted and a `FinalStateProtected` record with the `CorrelationId` (scenarios 1 to 3; SC-003)
- [X] T035 [P] [US2] Create `tests/Bank.Transaction.Api.Tests/StateProtection/ValidTransitionTests.cs`: from `Pending`, `balance-failed` ⇒ `Canceled` and `transaction-failed`; `transfer-failed` ⇒ `Canceled`, `transaction-failed` and `transfer-failed-balance`; `transfer-confirmed` ⇒ `Completed`, `transaction-completed` and `transfer-confirmed-balance`; `balance-confirmed` ⇒ `Pending` and `transfer-initiated` with `Amount`, accounts and customer; first `transaction-initiated` ⇒ `Pending` row and `balance-initiated`; repeated `transaction-initiated` in `Pending` ⇒ emits `balance-initiated` again without changing the state (scenario 4)
- [X] T036 [P] [US2] Create `tests/Bank.Transaction.Api.Tests/StateProtection/OrphanAndInvalidMessageTests.cs`: every message other than `transaction-initiated` with no existing row ⇒ 0 rows created, 0 events, `NoSuchTransaction` record; messages without `CorrelationId` or longer than 64 characters ⇒ no changes and an `InvalidMessage` record
- [X] T037 [P] [US2] Create `tests/Bank.Transaction.Api.Tests/StateProtection/ConcurrencyTests.cs`: several `transaction-initiated` in parallel ⇒ 1 row; several `transfer-confirmed` in parallel ⇒ `Completed`; simultaneous `transfer-confirmed` and `transfer-failed` ⇒ exactly one final state and never `Pending` (duplicate emission in this window is an accepted risk, see research.md R4: assert only on the state)
- [X] T038 [P] [US2] Create `tests/Bank.Transaction.Api.Tests/StateProtection/EmissionFailureTests.cs`: the sender fails on `transfer-confirmed` ⇒ the exception propagates and the state stays `Pending`; the redelivery emits and leaves `Completed`; a third delivery is ignored without events ("emit and then confirm" order, research.md R4)
- [X] T039 [P] [US2] Create `tests/Bank.Transaction.Api.Tests/StateProtection/SchemaTests.cs`: the unique index `IX_Transaction_CorrelationId` exists, `CorrelationId` is `nvarchar(64)` and a second `INSERT` with the same identifier fails with number 2627 or 2601
- [X] T040 [US2] Run `dotnet test tests/Bank.Transaction.Api.Tests` and confirm that T034 to T039 **fail** for the expected cause (depends on T034 to T039)

### Implementation for User Story 2

> T041 to T044 edit the same file, `<TRANSACTION>/Application/Features/Process/ProcessService.cs`: they are sequential.

- [X] T041 [US2] In `<TRANSACTION>/Application/Features/Process/ProcessService.cs`, add private helpers: validate the message (non-empty `CorrelationId` of **at most 64 characters**; otherwise log `INVALID_MESSAGE` at Warning level and finish without changes); load the transaction by `CorrelationId`; a method that applies the conditional transition with `ExecuteUpdateAsync` (`WHERE CorrelationId = @id AND CurrentState = 'Pending'`) and returns the affected rows; and a method that logs the ignored reason with `CorrelationId`, `MessageType` and `Reason` (depends on T011, T013, T040)
- [X] T042 [US2] In the same file, rewrite `TransactionInitiated`: if it does not exist, insert `Pending` and emit `balance-initiated`, treating a uniqueness violation (`DatabaseErrors.IsUniqueViolation`) as "already exists"; if it exists in `Pending`, emit `balance-initiated` again without changing the state; if it exists in a final state, log `FINAL_STATE_PROTECTED` and do not emit; keep the existing compensation (`DatabaseErrors.IsPermanent` ⇒ `transaction-failed`) (depends on T041)
- [X] T043 [US2] In the same file, rewrite `BalanceConfirmed`: only with the transaction in `Pending`, emit `transfer-initiated` with `CorrelationId`, `Amount`, `SourceAccount`, `DestinationAccount` and `CustomerId` taken from the row; with no row log `NO_SUCH_TRANSACTION`; in a final state log `FINAL_STATE_PROTECTED`; in both cases without events (depends on T042)
- [X] T044 [US2] In the same file, rewrite `BalanceFailed`, `TransferFailed` and `TransferConfirmed` with the order **read state → emit events → conditional transition** (`Canceled`, `Canceled` and `Completed`) and the events of `contracts/events.md`; if the transition returns 0 rows after emitting, log the reason and finish; with no row `NO_SUCH_TRANSACTION`; in a final state `FINAL_STATE_PROTECTED` without events (depends on T043)
- [X] T045 [US2] Remove the public method `ProcessDataBase` (the *upsert* that regressed states) from `<TRANSACTION>/Application/Features/Process/ProcessService.cs` after checking with a search that no reference remains (depends on T044)
- [X] T046 [US2] Run `dotnet test tests/Bank.Transaction.Api.Tests` and confirm that T034 to T039 pass; `dotnet build src/Bank.Transaction/Bank.Transaction.Api` with no errors or new warnings (depends on T045)

**Checkpoint**: US2 works and is tested independently, without depending on US1

---

## Phase 5: User Story 3 - Recovery after an interrupted processing (Priority: P2)

**Goal**: if processing is interrupted after recording the transfer and before emitting the result, the redelivery emits the result without creating a second transfer; a single processor recovers the emission.

**Independent Test**: force a sender failure after the record and redeliver the message; check 1 transfer and the emitted result.

### Tests for User Story 3 (write first; they must FAIL before implementing) ⚠️

- [X] T047 [P] [US3] Create `tests/Bank.Transfer.Api.Tests/TransferInitiated/EmissionRecoveryTests.cs`: `FailNextSend()` and first delivery ⇒ the exception propagates, there is 1 row in `Transfer` and `ClaimedAt` is `NULL` with `EmittedAt` `NULL`; the redelivery emits exactly one `transfer-confirmed` without creating another row; a third delivery is ignored (`DUPLICATE_ALREADY_EMITTED`) (scenarios 1 and 2; SC-004)
- [X] T048 [P] [US3] Create `tests/Bank.Transfer.Api.Tests/TransferInitiated/LeaseTests.cs`: seed a recorded request with `ClaimedAt` 31 s in the past and `EmittedAt` `NULL` ⇒ the delivery recovers it, emits once and logs `RECOVERED_EMISSION`; two simultaneous deliveries after the lease expires ⇒ only one emits; with a recent `ClaimedAt` the delivery is ignored (`DUPLICATE_IN_PROGRESS`); repeat for `Outcome = Failed`
- [X] T049 [US3] Run `dotnet test tests/Bank.Transfer.Api.Tests --filter "FullyQualifiedName~EmissionRecovery|FullyQualifiedName~Lease"` and confirm that T047 and T048 **fail** for the expected cause (depends on T047 and T048)

### Implementation for User Story 3

> T050 and T051 edit `<TRANSFER>/Application/Features/Process/ProcessService.cs`: they are sequential.

- [X] T050 [US3] In `<TRANSFER>/Application/Features/Process/ProcessService.cs`, wrap the emission of T032 in `try/catch`: on any exception, release the *claim* with `ExecuteUpdateAsync` (`ClaimedAt = NULL WHERE Id = @id AND EmittedAt IS NULL`) and rethrow it so that messaging retries (depends on T049)
- [X] T051 [US3] In the same file, extend the branch "`TransferRequest` already exists" of T031: if it is not emitted and the lease has expired (`ClaimedAt` is `NULL` or earlier than `now - EMISSION_LEASE_SECONDS`), take the *claim* with a conditional `ExecuteUpdateAsync` (`ClaimedAt = now WHERE Id = @id AND EmittedAt IS NULL AND (ClaimedAt IS NULL OR ClaimedAt < now - lease)`); if it affects 1 row, emit the stored `Outcome`, mark `EmittedAt` and log `RECOVERED_EMISSION`; if it affects 0 rows, log `DUPLICATE_IN_PROGRESS` and finish (depends on T050)
- [X] T052 [US3] Run the full suite `dotnet test tests/Bank.Transfer.Api.Tests` and confirm that US1 and US3 pass together (depends on T051)

**Checkpoint**: the three stories work independently and together

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: documentation, manual validation tool and final verification

- [X] T053 [P] Update `CLAUDE.md`: replace "There is no test project yet" with the commands `dotnet test tests/Bank.Transfer.Api.Tests` and `dotnet test tests/Bank.Transaction.Api.Tests` (they require Docker), document the `TransferRequest` table and the ignored-message records, and remove from "Known technical debt" what this feature resolves
- [X] T054 [P] Update `README.md`: add the automated tests section and the idempotent behavior (what happens with duplicates and late messages), and remove what was resolved from "Known problems of the current design"
- [X] T055 [P] Create the manual tool `infra/local/duplicate-test/Program.cs` and `infra/local/duplicate-test/DuplicateTest.csproj` (console, same style and emulator connection string as `infra/local/smoke-test`; no secrets): publishes N copies of `transfer-initiated` with a given `CorrelationId` and is not part of `Bank.slnx`
- [X] T056 Check Principle I: search for `ProjectReference` in both `tests/` `.csproj` files and confirm that each references only its service (depends on T052 and T046)
- [X] T057 Run `dotnet build Bank.slnx` and both complete test projects; confirm there are no new warnings in the modified files (depends on T052 and T046)
- [X] T058 Detect previous duplicates with the queries of `quickstart.md` section 4, apply `.\infra\local\apply-migrations.ps1` and run the manual validation of `quickstart.md` sections 2 and 3 with the services on the host and the tool of T055 (depends on T055 and T057)
- [X] T059 [P] Update `C:\Users\jdroj\OneDrive\Documentos\CV\HALLAZGOS-PENDIENTES-Bank.md` and `GUIA-ESTUDIO-Hallazgos-Bank.md` (outside the repo) marking findings 2 and 3 as resolved and adding the decisions of `research.md`; do not touch the repo from this task

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: no dependencies
- **Foundational (Phase 2)**: depends on Setup; **blocks all stories**. Within the phase, the Transfer blocks (T005 to T010 and T015 to T018) and Transaction blocks (T011 to T014 and T019 to T022) are independent of each other
- **User Stories (Phase 3+)**: depend on the Foundational phase
  - US1 (Transfer) and US2 (Transaction) are independent and can advance in parallel
  - US3 depends on US1 (it extends the same method and file)
- **Polish (Phase 6)**: depends on the stories one wants to deliver

### User Story Dependencies

- **US1 (P1)**: after Foundational; no dependencies on other stories
- **US2 (P1)**: after Foundational; no dependencies on US1 or US3 (another service)
- **US3 (P2)**: after US1 (T032 and T031)

### Within Each User Story

- Tests are written first and must fail (T028, T040, T049) before implementing
- Schema and constants (Foundational) before the logic
- Logic in a single file is implemented sequentially
- Each story ends with its suite green before moving on to the next

### Parallel Opportunities

- T001 and T002 (test projects); T005, T006, T009, T011, T012 and all the test infrastructure per service (T015 to T017, T019 to T021)
- All the tests of a single story (T023 to T027; T034 to T039; T047 and T048) are different files
- Complete US1 and US2, in parallel, by different people
- Polish: T053, T054, T055 and T059

---

## Parallel Example: User Story 1

```bash
# Write all the US1 tests together (different files):
Task: "DuplicateDeliveryTests in tests/Bank.Transfer.Api.Tests/TransferInitiated/DuplicateDeliveryTests.cs"
Task: "ConcurrentDeliveryTests in tests/Bank.Transfer.Api.Tests/TransferInitiated/ConcurrentDeliveryTests.cs"
Task: "RejectedTransferTests in tests/Bank.Transfer.Api.Tests/TransferInitiated/RejectedTransferTests.cs"
Task: "InvalidAndMismatchedMessageTests in tests/Bank.Transfer.Api.Tests/TransferInitiated/InvalidAndMismatchedMessageTests.cs"
Task: "SchemaTests in tests/Bank.Transfer.Api.Tests/TransferInitiated/SchemaTests.cs"
```

## Parallel Example: US1 and US2 at the same time

```bash
# After Foundational, two independent fronts (different services):
Front A: T023 to T033  (Transfer: idempotent record)
Front B: T034 to T046  (Transaction: final-state protection)
```

---

## Implementation Strategy

### MVP First (User Story 1)

1. Phase 1: Setup
2. Phase 2: Foundational (blocks everything)
3. Phase 3: US1 (closes the double-charge risk)
4. **STOP and VALIDATE**: `dotnet test tests/Bank.Transfer.Api.Tests` green
5. Since US2 is also P1, completing it before considering the feature delivered is recommended

### Incremental Delivery

1. Setup + Foundational ⇒ base ready
2. US1 ⇒ test independently
3. US2 ⇒ test independently (can go in parallel with US1)
4. US3 ⇒ test with the complete Transfer suite
5. Polish ⇒ documentation, manual tool and end-to-end validation

### Risk notes

- Creating the unique indexes (T010, T014) fails if there are already repeated `CorrelationId`s in the local databases: run the queries of `quickstart.md` section 4 first.
- The tests require Docker running; the first run downloads or reuses the SQL Server image.
- The immediate retries of messaging (finding 1) and the absence of Outbox (finding 5) remain out of scope: point them out if touched.

---

## Notes

- [P] = different files and no pending dependencies
- The [Story] label links each task to the spec's story
- Each story must be completable and testable independently
- Verify that the tests fail before implementing
- Do not make commits without an express instruction from the repository owner
- Avoid: vague tasks, conflicts in the same file, dependencies between stories that break independence
