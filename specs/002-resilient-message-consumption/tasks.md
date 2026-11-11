---

description: "Task list: Resilient message consumption"
---

# Tasks: Resilient message consumption

**Input**: Design documents in `/specs/002-resilient-message-consumption/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/message-handling.md, quickstart.md

**Tests**: INCLUDED and mandatory. The spec requires them (FR-015) and the constitution imposes them (Principle V, NON-NEGOTIABLE): each story
phase starts with tests that must **fail** before implementing (red → green).

**Organization**: Tasks are grouped by user story. US1 (poison) and US2 (transient) are P1; US3 (retries exhausted)
and US4 (logging) are P2. US3 reuses the compensator of US1 and the retry of US2.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no pending dependencies)
- **[Story]**: US1 to US4 (only in story phases)
- All paths are relative to the repository root
- **A task that lists several paths applies to each service**: they are independent files that can be done in parallel, but the
  task is not finished until the four are covered (or the three, if indicated)

## Path Conventions

- Services (abbreviations used below):
  - `<TRANSACTION>` = `src/Bank.Transaction/Bank.Transaction.Api`
  - `<BALANCE>` = `src/Bank.Balance/Bank.Balance.Api`
  - `<TRANSFER>` = `src/Bank.Transfer/Bank.Transfer.Api`
  - `<NOTIFICATION>` = `src/Bank.Notification/Bank.Notification.Api` (**not** the template `src/Bank.Notification/Bank.Notification`, which is not touched)
- Tests (one project per service; none references more than one service):
  - `<TX-TESTS>` = `tests/Bank.Transaction.Api.Tests`, `<BAL-TESTS>` = `tests/Bank.Balance.Api.Tests` (new),
    `<TRF-TESTS>` = `tests/Bank.Transfer.Api.Tests`, `<NOT-TESTS>` = `tests/Bank.Notification.Api.Tests` (new)
- New code per service in `<SERVICE>/Application/Resilience/` (namespace `Bank.<Service>.Api.Application.Resilience`) and
  `<SERVICE>/External/ServiceBusRecive/`
- Repo style: *file-scoped* namespaces, `UPPER_SNAKE_CASE` constants, `Service`/`Handler` suffixes. Existing files use
  CRLF and BOM: keep them when editing. Log and exception messages in Spanish
- **No service's `ProcessService` is modified** and **no existing test is modified** (SC-007). If a task seems to require it, stop and review
- **Do not make commits** until the repository owner expressly says so

## Shared contracts (reference for the tasks)

```text
public record IncomingMessage(string Body, string MessageType, string MessageId, int RetryAttempt);

public interface IMessageActions {
    Task CompleteAsync(CancellationToken ct);
    Task DeadLetterAsync(string reason, string detail, string? correlationId, int attempts, CancellationToken ct);
    Task ScheduleRetryAsync(int nextAttempt, TimeSpan delay, CancellationToken ct);
}

public interface IFailureCompensator {
    // true if it emitted a failure event; false if that step has none (it does nothing)
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

**Purpose**: create the two missing test projects and set the baseline

- [X] T001 Create `<BAL-TESTS>/Bank.Balance.Api.Tests.csproj` (`net10.0`, nullable and implicit usings, `IsPackable=false`, `<Using Include="Xunit" />`) copying the same versions of `coverlet.collector`, `Microsoft.NET.Test.Sdk`, `xunit` and `xunit.runner.visualstudio` as `<TRF-TESTS>/Bank.Transfer.Api.Tests.csproj`, **including** `Testcontainers.MsSql` (Balance's compensator is tested with a real SQL Server, FR-016; it requires Docker); `ProjectReference` **only** to `<BALANCE>/Bank.Balance.Api.csproj`
- [X] T002 [P] Create `<NOT-TESTS>/Bank.Notification.Api.Tests.csproj` with the same configuration as T001 but **without** `Testcontainers.MsSql` (Notification has no compensator or relational database: it does not need Docker); `ProjectReference` **only** to `<NOTIFICATION>/Bank.Notification.Api.csproj`
- [X] T003 Add both projects to `Bank.slnx` inside the existing `<Folder Name="/tests/">` (depends on T001 and T002)
- [X] T004 Baseline: `dotnet build Bank.slnx` without errors and `dotnet test` in the Transfer and Transaction projects with **31 and 72 tests green**; the two new projects discover 0 tests without error. Note the results (depends on T003; requires Docker)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: common scaffolding in the four services, **without changing behavior**: the receiver delegates to a processor that for now
only executes and completes, and propagates any exception as it does today

**⚠️ CRITICAL**: no story can start until this phase is finished

### Constants and contracts

- [X] T005 [P] Add in `<TRANSACTION>/Domain/Constants/Constant.cs`, `<BALANCE>/Domain/Constants/Constant.cs`, `<TRANSFER>/Domain/Constants/Constants.cs` and `<NOTIFICATION>/Domain/Constants/Constant.cs` two classes: `DeadLetterReasonConstants` (`POISON_MESSAGE = "PoisonMessage"`, `RETRIES_EXHAUSTED = "RetriesExhausted"`, `COMPENSATION_FAILED = "CompensationFailed"`) and `RetryConstants` (`MAX_RETRIES = 5`, `BASE_DELAY_SECONDS = 5`, `GROWTH_FACTOR = 3`, `MAX_DELAY_SECONDS = 300`, `JITTER = 0.2` as `double`, `RETRY_ATTEMPT_PROPERTY = "RetryAttempt"`, `MAX_DETAIL_LENGTH = 1024`, `MAX_CORRELATION_ID_LENGTH = 64`)
- [X] T006 [P] Create in `<TRANSACTION>`, `<BALANCE>`, `<TRANSFER>` and `<NOTIFICATION>` under `Application/Resilience/`: `IncomingMessage.cs`, `IMessageActions.cs`, `IFailureCompensator.cs` and `NullFailureCompensator.cs` (returns `false` and does nothing), with the exact signatures of the *Shared contracts* section

### Test infrastructure

- [X] T007 [P] Create `Infrastructure/FakeMessageActions.cs` in `<TX-TESTS>`, `<BAL-TESTS>`, `<TRF-TESTS>` and `<NOT-TESTS>`: implements `IMessageActions`; stores in an ordered `Calls` list each invocation (`"Complete"`, `"DeadLetter:<reason>"`, `"ScheduleRetry:<n>"`) with its arguments in readable properties (`Reason`, `Detail`, `CorrelationId`, `Attempts`, `NextAttempt`, `Delay`); allows configuring an operation to throw an exception (`FailOn`). In `<BAL-TESTS>` also copy `CapturingLogger.cs`, `FakeServiceBusSender.cs` and `SqlServerFixture.cs` from `<TX-TESTS>/Infrastructure/` (changing the namespace) and create a `BalanceHarness.cs` analogous to `TransactionHarness.cs` (the container's connection string, schema created with Balance's **real migrations**, and utilities to insert and count `Balance` rows); in `<NOT-TESTS>`, only `CapturingLogger.cs`

### Scaffolding tests (write first; they must FAIL because they do not compile)

- [X] T008 [P] `Resilience/ResilientMessageProcessorTests.cs` in `<TX-TESTS>`, `<BAL-TESTS>`, `<TRF-TESTS>` and `<NOT-TESTS>`: (a) with a `process` that finishes well, `HandleAsync` invokes `process` once with (body, message type) and calls `CompleteAsync` once; (b) with a `process` that throws `InvalidOperationException`, the exception **propagates** and `CompleteAsync` is not called. Use `NullFailureCompensator`, `CapturingLogger<ResilientMessageProcessor>` and `FakeMessageActions`. Run and confirm that it fails (depends on T006 and T007)

### Implementation

- [X] T009 [P] Create `Application/Resilience/ResilientMessageProcessor.cs` in `<TRANSACTION>`, `<BALANCE>`, `<TRANSFER>` and `<NOTIFICATION>` with the constructor and `HandleAsync` of *Shared contracts*; for now: `await _process(...)`, `await actions.CompleteAsync(ct)`, without catching exceptions (depends on T006)
- [X] T010 [P] Create `External/ServiceBusRecive/ServiceBusMessageActions.cs` in the four services: implements `IMessageActions` over a `ProcessMessageEventArgs` and a retry `ServiceBusSender`. `CompleteAsync` → `args.CompleteMessageAsync`. `DeadLetterAsync` → `args.DeadLetterMessageAsync(args.Message, properties, reason, detail)` with `detail` **truncated to `MAX_DETAIL_LENGTH` (1,024)** and properties `CorrelationId` (only if not null) and `Attempts`. `ScheduleRetryAsync` → creates a `ServiceBusMessage` with the same body, `Subject`, `ContentType`, `MessageId` and `ApplicationProperties` as the original plus `RetryAttempt = nextAttempt`, and schedules it with `sender.ScheduleMessageAsync(copy, DateTimeOffset.UtcNow + delay)`; **if it fails, the exception propagates** (the original is not completed)
- [X] T011 [P] Modify `External/ServiceBusRecive/ServiceBusReciveService.cs` in the four services: create the processors with `new ServiceBusProcessorOptions { AutoCompleteMessages = false }`; create **one** retry `ServiceBusSender` with `_client.CreateSender(configuration["SERVICEBUSTOPIC"])` and dispose it in `StopAsync`; receive `IFailureCompensator` and `ILogger<ResilientMessageProcessor>` through the constructor and build a `ResilientMessageProcessor` whose `process` is `(body, type, ct) => _mediator.Publish(new ProcessEvents(body, type), ct)`; `Process` builds the `IncomingMessage` (`RetryAttempt` read from `ApplicationProperties["RetryAttempt"]`, 0 if missing or not an integer; the message's `MessageId`; `MessageType` = subscription), creates the `ServiceBusMessageActions` and calls `HandleAsync`. **`ProcessError` stays as it is** for now (depends on T009 and T010)
- [X] T012 [P] Register in `<TRANSACTION>/Program.cs`, `<BALANCE>/Program.cs`, `<TRANSFER>/Program.cs` and `<NOTIFICATION>/Program.cs`: `builder.Services.AddSingleton<IFailureCompensator, NullFailureCompensator>();` (the real ones arrive in US1). `Program.cs` only registers dependencies (depends on T006)

**Checkpoint**:
- [X] T013 `dotnet build Bank.slnx` without errors; the T008 tests pass green; the 31 + 72 existing ones stay green **without modifying them**; and, with the services running, a transfer through the Gateway still ends `Completed` (the change to `AutoCompleteMessages = false` must not break the flow). Depends on T008 to T012

---

## Phase 3: User Story 1 - A message that is impossible to process is set aside immediately with its reason (Priority: P1) 🎯 MVP

**Goal**: a poison message goes to the DLQ on the first delivery, with reason and detail, and, if its `CorrelationId` is readable, the
transfer is canceled with the step's failure event.

**Independent Test**: send each service messages with an empty body, `null`, broken JSON and no `CorrelationId`: they end up in the DLQ
with `DeliveryCount = 1`, reason `PoisonMessage` and detail; with a readable `CorrelationId` the `*-failed` is also emitted.

### Tests for User Story 1 (write first; they must FAIL)

- [X] T014 [P] [US1] `Resilience/MessageClassifierTests.cs` in `<TX-TESTS>`, `<BAL-TESTS>`, `<TRF-TESTS>` and `<NOT-TESTS>` on `MessageClassifier.Check(string body)` (returns `PoisonCheck { IsPoison, Detail, CorrelationId }`). Each poison case with a non-empty `Detail`: empty body; only spaces; text that is not JSON; JSON `null`; JSON that is not an object (`[]`); no `CorrelationId`; null or blank `CorrelationId`; `CorrelationId` of **65 characters** (with a `Detail` that says so); body with `"CustomerId":"abc"` (does not deserialize to the service's entity). Valid cases: `CorrelationId` of exactly **64** characters and a complete well-formed message (`IsPoison = false`, `CorrelationId` returned). When the `CorrelationId` is readable but the message is poison for another reason (e.g. invalid `CustomerId`), `CorrelationId` is returned anyway
- [X] T015 [P] [US1] `Resilience/PoisonMessageTests.cs` in the four test projects on `ResilientMessageProcessor`: with each poison body of T014, `process` is **never** invoked, `Calls` contains exactly `["DeadLetter:PoisonMessage"]` with `Attempts = 1`, non-empty `Detail` and no `Complete`; if the `CorrelationId` is readable the compensator is called once (use a `FakeFailureCompensator` in `Infrastructure/` that counts calls and returns `true`); if the compensator returns `false` (e.g. because of FR-016), the message is set aside anyway and there is no other effect; if it is not readable, the compensator is **not** called; a `JsonException` thrown by `process` is treated like a poison message (second line of defense, FR-002)
- [X] T016 [P] [US1] `Resilience/FailureCompensatorTests.cs` in `<TX-TESTS>`, `<BAL-TESTS>` and `<TRF-TESTS>` on `FailureCompensator` with a real SQL Server (the project's harness) and `FakeServiceBusSender`. **Safety rule FR-016**: (a) **without a record** of the `CorrelationId`, for the first-step subscription (`transaction-initiated` / `balance-initiated` / `transfer-initiated`) it emits **one** `transaction-failed` / `balance-failed` / `transfer-failed` event with payload `{ CorrelationId, CustomerId }` and returns `true`; if `CustomerId` is missing, it travels null *(superseded: it is omitted from the payload, see [contracts/message-handling.md](contracts/message-handling.md) §3)*; (b) **with an existing record** it emits nothing and returns `false`: in Transaction test the `Pending`, `Completed` and `Canceled` rows; in Balance an existing row; in Transfer a `TransferRequest` row with result `Confirmed` and another `Failed`; (c) with the **database unreachable** it **emits** and logs a `Warning`. To build the compensator in the tests: a `ServiceCollection` that registers `IDatabaseService` pointing to the harness's container (normal case) and another with a connection string to a closed port with `Connect Timeout=1` (unreachable case), and use its `IServiceScopeFactory`; (d) for any other subscription (e.g. `balance-confirmed` in Transaction) it neither queries nor sends anything and returns `false`; (e) if the send throws, the exception propagates; (f) unreadable `CorrelationId` → `false` without querying. Full SC-008 scenario: insert the row, invoke with a defective body and check 0 events. In `<NOT-TESTS>`: `NullFailureCompensator` returns `false` and sends nothing
- [X] T017 [US1] Run T014 to T016 and **confirm that they fail** for the right reason (they do not compile for lack of `MessageClassifier`, `PoisonCheck` and `FailureCompensator`)

### Implementation for User Story 1

- [X] T018 [P] [US1] Create `Application/Resilience/MessageClassifier.cs` (static class with `PoisonCheck Check(string body)` and the `PoisonCheck` type) in the four services, with the 6 rules of data-model.md: empty/blank body; unreadable JSON; JSON `null` or non-object; `CorrelationId` missing/blank; `CorrelationId` longer than `MAX_CORRELATION_ID_LENGTH` (64) characters; and that `JsonConvert.DeserializeObject<TEntity>` (`TransactionEntity`, `BalanceEntity`, `TransferEntity`, `NotificationEntity`, with Newtonsoft like `ProcessService`) does not throw `JsonException`. The `CorrelationId` is extracted tolerantly from the JSON even if another rule fails; for logging it is truncated to 64 characters
- [X] T019 [P] [US1] Create `Application/Resilience/FailureCompensator.cs` in `<TRANSACTION>`, `<BALANCE>` and `<TRANSFER>`: implements `IFailureCompensator` with `IServiceBusSenderService` (the service's own), `ILogger` and `IServiceScopeFactory` (the compensator is *singleton* and `IDatabaseService` is *scoped*: create a scope per query). **Rule FR-016**: before emitting, `AnyAsync` with `AsNoTracking` of the own record (Transaction: `Transaction`; Balance: `Balance`; Transfer: `TransferRequest`) by `CorrelationId`; if it exists, do not emit and return `false` (log a `Warning` with `Reason = "AlreadyRegistered"`); if the query throws, log a `Warning` and emit; it is only queried for the first-step subscription. Map from first step to failure event (Transaction: `TRANSACTION_INITIATED` → `TRANSACTION_FAILED`; Balance: `BALANCE_INITIATED` → `BALANCE_FAILED`; Transfer: `TRANSFER_INITIATED` → `TRANSFER_FAILED`, using the existing constants of `SendSubscriptionConstants`/`ReceivedSubscriptionsConstants`); payload `new { CorrelationId, CustomerId }` read tolerantly (if missing, `null`) *(superseded: omitted from the payload)*. In `<NOTIFICATION>` `NullFailureCompensator` is kept
- [X] T020 [P] [US1] Extend `ResilientMessageProcessor.HandleAsync` in the four services: before invoking `process`, `MessageClassifier.Check(body)`; if it is poison: call the compensator **only if** `CorrelationId` is not null, `DeadLetterAsync(POISON_MESSAGE, detail, correlationId, attempts: RetryAttempt + 1)` and finish without completing or invoking `process`; catch `JsonException` thrown by `process` and treat it as poison (same path). The compensator's exceptions propagate for now (FR-009 is completed in US3)
- [X] T021 [P] [US1] Replace in `<TRANSACTION>/Program.cs`, `<BALANCE>/Program.cs` and `<TRANSFER>/Program.cs` the registration of `NullFailureCompensator` with `AddSingleton<IFailureCompensator, FailureCompensator>()` (Notification keeps the null one)
- [X] T022 [US1] Run T014 to T016 green and the existing suites green without modifying them; with the services running, run section B of [quickstart.md](quickstart.md) and confirm `DeliveryCount = 1`, reason and detail in the DLQ, and the transfer canceled when the `CorrelationId` is readable **and there is no previous record**; also check the safety scenario of the same section (defective message with the `CorrelationId` of an already `Completed` transfer ⇒ DLQ and **no** `*-failed` or email) (depends on T018 to T021)

**Checkpoint**: story 1 works and is tested independently. **It is the MVP.**

---

## Phase 4: User Story 2 - A transient failure is retried with a growing delay (Priority: P1)

**Goal**: a transient failure of a valid message is retried with a growing delay and random variation, without using up the 10
deliveries in seconds and without losing the message if the service restarts.

**Independent Test**: take `BankTransfer` offline for ~90 s with a transfer in progress; when it comes back, it ends `Completed`, the DLQ
stays empty and the log shows retries with growing delays.

### Tests for User Story 2 (write first; they must FAIL)

- [X] T023 [P] [US2] `Resilience/RetryPolicyTests.cs` in the four test projects on `RetryPolicy.NextDelay(int retry, Random random)` (retry from 1 to 5): with the neutral seed, the value is in `[0.8 × nominal ; 1.2 × nominal]` for the table 5 s / 15 s / 45 s / 135 s / 300 s; with a `Random` that always returns the minimum and always the maximum, exactly `0.8×` and `1.2×` are obtained; the nominals are strictly increasing up to the cap; the sum of the five nominals is at least 300 s (SC-003: accumulated window ≥ 5 min); retry 5 does not exceed `300 × 1.2`; `retry` less than 1 or greater than `MAX_RETRIES` throws `ArgumentOutOfRangeException`
- [X] T024 [P] [US2] `Resilience/TransientRetryTests.cs` in the four projects on `ResilientMessageProcessor` with a `process` that throws `TimeoutException` and a `Random` with a fixed seed: with `RetryAttempt = 0`, `Calls` is exactly `["ScheduleRetry:1", "Complete"]` **in that order** and `Delay` within the range of retry 1; with `RetryAttempt = 3` it schedules attempt 4 with the corresponding range; if `ScheduleRetryAsync` throws, the exception propagates and `Complete` is **not** called; with the cancellation token canceled and `process` throwing `OperationCanceledException`, nothing is scheduled or completed and the exception propagates (FR-014); a transient failure never calls `DeadLetterAsync` while retries remain
- [X] T025 [US2] `Resilience/PermanentDataErrorTests.cs` in `<TRF-TESTS>` (and an equivalent one in `<BAL-TESTS>` for Balance's permanent path, with the real `ProcessService`, the `BalanceHarness` and a NOT NULL violation, SQL 515, which must emit `balance-failed` and complete without retry; Transaction already has coverage in its existing tests). For Transfer: using `TransferHarness`, `AddAmountCheckAsync` and a `ResilientMessageProcessor` whose `process` runs the real `ProcessService`: (a) a message whose amount violates the CHECK constraint (SQL 547) emits `transfer-failed`, `Calls` is `["Complete"]` and there is **no** `ScheduleRetry` or `DeadLetter` (FR-010); (b) the same message delivered twice completes both times without retry or DLQ (uniqueness violation = "already processed", FR-011). First review the `TransferHarness` API (`DeliverAsync`, `Message(...)`, `Sender`)
- [X] T026 [US2] Run T023 to T025 and **confirm that they fail** (T023 and T024 for lack of `RetryPolicy`; T025 may pass because it already compiles with the scaffolding processor: in that case note it and keep it as a regression test)

### Implementation for User Story 2

- [X] T027 [P] [US2] Create `Application/Resilience/RetryPolicy.cs` (static class) in the four services with `TimeSpan NextDelay(int retry, Random random)`: `min(BASE_DELAY_SECONDS × GROWTH_FACTOR^(retry−1), MAX_DELAY_SECONDS) × u`, with `u = 1 − JITTER + random.NextDouble() × 2 × JITTER`; pure function, no clock; validate `retry` between 1 and `MAX_RETRIES`
- [X] T028 [P] [US2] Extend `ResilientMessageProcessor.HandleAsync` in the four services: wrap `process` in `try/catch (Exception)`; if `ct.IsCancellationRequested` (graceful shutdown), rethrow without scheduling or completing; if `RetryAttempt < MAX_RETRIES`, compute `RetryPolicy.NextDelay(RetryAttempt + 1, _random)`, `await actions.ScheduleRetryAsync(RetryAttempt + 1, delay, ct)` and **afterwards** `await actions.CompleteAsync(ct)`; if `RetryAttempt >= MAX_RETRIES`, rethrow for now (US3 completes it). Mandatory order: schedule first, complete afterwards
- [X] T029 [US2] Run T023 to T025 green and the existing suites green; with the services running, run section C of [quickstart.md](quickstart.md) (`ALTER DATABASE BankTransfer SET OFFLINE …` ~90 s) including the step of **restarting the service during the wait** (FR-006), and confirm `Completed`, empty DLQ and growing delays in the log (depends on T027 and T028)

**Checkpoint**: stories 1 and 2 work independently.

---

## Phase 5: User Story 3 - When retries are exhausted, the transfer is compensated and the message is explained (Priority: P2)

**Goal**: if the transient failure persists beyond the retry window, the first step's `*-failed` is emitted and the message
is set aside with reason `RetriesExhausted`; if it cannot be compensated, it is set aside as `CompensationFailed` without being lost.

**Independent Test**: take `BankTransfer` offline for more than ~9 min with a transfer in progress: Transaction ends `Canceled`
and the original message is in the DLQ with reason `RetriesExhausted`.

### Tests for User Story 3 (write first; they must FAIL)

- [X] T030 [P] [US3] `Resilience/ExhaustedRetriesTests.cs` in the four projects on `ResilientMessageProcessor` with a `process` that throws `TimeoutException`: with `RetryAttempt = 5` on a first-step message, the compensator is called once, `Calls` is exactly `["DeadLetter:RetriesExhausted"]` with `Attempts = 6`, `Detail` that contains the number of attempts and the type of the last exception and **never** exceeds 1,024 characters (test with an exception with a very long message), with no `ScheduleRetry` or `Complete`. Additional cases: a subscription that only updates state (e.g. `balance-confirmed` in Transaction, `transfer-confirmed-balance` in Balance) → the compensator returns `false` and there is only `DeadLetter:RetriesExhausted`; with a compensator that returns `false` because of FR-016 (there is already a record of the `CorrelationId`) the message is set aside with `RetriesExhausted` without any additional effect; in Notification → only `DeadLetter:RetriesExhausted`; if the compensator throws (the bus does not respond) → `DeadLetter:CompensationFailed` with a detail that mentions the compensation failure and the original error; the same `CompensationFailed` happens on the poison-message path with a readable `CorrelationId` (FR-009)
- [X] T031 [US3] Run T030 and **confirm that it fails** (the processor rethrows when exhausted and propagates the compensator's exceptions)

### Implementation for User Story 3

- [X] T032 [P] [US3] Extend `ResilientMessageProcessor` in the four services: when `RetryAttempt >= MAX_RETRIES` on a transient failure, `try { await compensator.CompensateAsync(...) } catch → DeadLetterAsync(COMPENSATION_FAILED, ...)`; if it compensated or does not apply, `DeadLetterAsync(RETRIES_EXHAUSTED, detail, correlationId, attempts: RetryAttempt + 1)`; the detail follows the form "Reintentos agotados tras {n} intentos. Último error: {Type}: {message}" (a runtime string, in Spanish) truncated to `MAX_DETAIL_LENGTH`; apply the same capture of the compensator's exception to the poison path of T020. An unreadable `CorrelationId` does not call the compensator
- [X] T033 [US3] Run T030 green and the existing suites green; run section D of [quickstart.md](quickstart.md) (database offline > 9 min) or, if time does not allow, record that the logic is covered by T030 and test the path in the emulator by forcing `RetryAttempt = 5` with a manually sent message (depends on T032)

**Checkpoint**: stories 1 to 3 work independently.

---

## Phase 6: User Story 4 - Processor errors are logged with their context (Priority: P2)

**Goal**: every processing and processor error is logged with origin, entity and, whenever possible, `CorrelationId`,
message type, attempt and classification.

**Independent Test**: provoke a processing error and a connection error; both appear in the log with their fields.

### Tests for User Story 4 (write first; they must FAIL)

- [X] T034 [P] [US4] `Resilience/ProcessorErrorHandlerTests.cs` in the four projects on `ProcessorErrorHandler.Log(ILogger logger, Exception exception, string errorSource, string entityPath, string fullyQualifiedNamespace, bool cancellationRequested)`: logs at `Error` level with the parameters `ErrorSource`, `EntityPath` and `FullyQualifiedNamespace` and the exception attached; with `cancellationRequested = true` and `OperationCanceledException`, the level is `Debug` and not `Error` (FR-014); the handler neither receives nor uses `IMessageActions` (it cannot set aside, complete or compensate: FR-012)
- [X] T035 [P] [US4] `Resilience/ProcessorLoggingTests.cs` in the four projects on `ResilientMessageProcessor` with `CapturingLogger<ResilientMessageProcessor>`: a poison message produces an `Error` entry with `CorrelationId`, `MessageId`, `MessageType`, `Attempt`, `Classification = "Poison"` and `Reason = "PoisonMessage"`; a transient failure with a retry produces a `Warning` entry with `Classification = "Transient"`, `Attempt`, `DelaySeconds` and `CorrelationId`; exhausted retries produce `Error` with `Reason = "RetriesExhausted"`; a message without a readable `CorrelationId` logs `CorrelationId = null` and `MessageId` present (FR-013). Parameter names exactly as in [contracts/message-handling.md](contracts/message-handling.md) §4
- [X] T036 [US4] Run T034 and T035 and **confirm that they fail**

### Implementation for User Story 4

- [X] T037 [P] [US4] Create `Application/Resilience/ProcessorErrorHandler.cs` (static class with `Log(...)`, signature of T034) in the four services
- [X] T038 [P] [US4] Add in `ResilientMessageProcessor` of the four services the log entries of [contracts/message-handling.md](contracts/message-handling.md) §4 (templates with named parameters `{CorrelationId}`, `{MessageId}`, `{MessageType}`, `{Attempt}`, `{Classification}`, `{Reason}`, `{DelaySeconds}`), without changing the decisions of T020, T028 and T032
- [X] T039 [P] [US4] Modify `ProcessError` in `External/ServiceBusRecive/ServiceBusReciveService.cs` of the four services to call `ProcessorErrorHandler.Log` with `args.Exception`, `args.ErrorSource.ToString()`, `args.EntityPath`, `args.FullyQualifiedNamespace` and `args.CancellationToken.IsCancellationRequested`, injecting `ILogger<ServiceBusReciveService>`. Return `Task.CompletedTask`; do **not** set aside or complete anything here
- [X] T040 [US4] Run T034 and T035 green; run section E of [quickstart.md](quickstart.md) (restart the emulator and stop a service with Ctrl+C) and confirm that the processor error is logged and that the graceful shutdown does not count as a failure (depends on T037 to T039)

**Checkpoint**: the four stories work independently.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: CI, governance, tools, documentation and final verification

- [X] T041 Extend the matrix of `.github/workflows/ci.yml` to `service: [Transfer, Transaction, Balance, Notification]` (the job names become `Tests (Balance)` and `Tests (Notification)`); update the header comments that list the required checks and clarify that `Tests (Balance)`, like Transfer and Transaction, uses the runner's Docker (Testcontainers) whereas `Tests (Notification)` does not
- [X] T042 Amend `.specify/memory/constitution.md` to version **1.2.0** (MINOR): the workflow rule that names the required checks becomes `Build`, `Tests (Transfer)`, `Tests (Transaction)`, `Tests (Balance)` and `Tests (Notification)`; update *Last Amended*, the impact report and the gaps of Principle III (closed for the first steps; the update-only handlers remain). **Requires the express approval of the repository owner before applying it**
- [X] T043 **[MANUAL — repository owner]** In GitHub, adjust the protection of `main` and `develop` to also require `Tests (Balance)` and `Tests (Notification)`. They can only be chosen after they have run once; the names must match exactly
- [X] T044 [P] Extend `infra/local/duplicate-test/Program.cs` with an option to send an arbitrary body and the indicated `Subject` (needed for section B of [quickstart.md](quickstart.md)); document the usage in the file header
- [X] T045 [P] Update `CLAUDE.md`: new section "Resilient consumption" (classification into three classes, policy `5 s·3^(n−1)` capped at 5 min ±20 %, DLQ reasons, first-step compensation, `ProcessError` only logs, `RetryAttempt`), list of test projects (4), CI checks (5) and the technical debt (remove "immediate retries without wait" and empty `ProcessError`; keep the update-only handlers without compensation)
- [X] T046 [P] Update `README.md` (**UTF-16 LE with BOM**, LF line endings; edit it with PowerShell keeping the encoding): tests section with the 4 projects, CI table with 5 checks, resilience subsection and "Known problems" without the resolved points
- [X] T047 [P] Update the study documents in `C:\Users\jdroj\OneDrive\Documentos\CV\` (standing rule): mark findings 1 and 4 as resolved in `HALLAZGOS-PENDIENTES-Bank.md` and `GUIA-ESTUDIO-Hallazgos-Bank.md` with date, commit, what was done, verification and interview phrase; add the entry in `Plan_de_estudio.md`. Outside the repository: they are not committed
- [X] T048 Final verification: `dotnet build Bank.slnx -c Release` without errors or NU1902/NU1903 warnings; `dotnet test` of the 4 projects green; `git diff --stat origin/develop -- tests/Bank.Transfer.Api.Tests tests/Bank.Transaction.Api.Tests` shows **only added files** (no existing test modified, SC-007); `dotnet list Bank.slnx package --vulnerable --include-transitive` clean; `git diff origin/develop -- infra/local/Config.json` empty
- [X] T049 Run end to end sections A, B, C and E of [quickstart.md](quickstart.md) (D if time allows) and note the results and observations (e.g. whether the emulator keeps the properties of `DeadLetterMessageAsync`, research.md R7 [VERIFY])

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: no dependencies
- **Foundational (Phase 2)**: depends on Setup; **blocks** all the stories
- **US1 (Phase 3)**: depends on Foundational. **MVP**
- **US2 (Phase 4)**: depends on Foundational; shares `ResilientMessageProcessor.cs` with US1, so its T028 tasks come **after** T020 in each service
- **US3 (Phase 5)**: depends on US1 (compensator) and on US2 (retries)
- **US4 (Phase 6)**: depends on Foundational; adds logging over the processor of US1 to US3, so T038 comes after T020, T028 and T032
- **Polish (Phase 7)**: depends on the desired stories; T042 and T043 are decisions of the owner

### Within Each Story

- Tests are written first and must **fail** (T017, T026, T031, T036) before implementing
- Constants and contracts → tests → implementation → verification
- A single file per service is not edited in parallel (`ResilientMessageProcessor.cs`, `ServiceBusReciveService.cs`, `Program.cs`): the tasks that touch it go in order across phases, but the four services among themselves are parallel

### Parallel Opportunities

- T001/T002; T005 to T007; T008; T009 to T012 are [P] across services and across different files
- Within each story, all the [P] tests (different files) and all the [P] implementations (one file per service) can advance at the same time
- The four services are independent of each other: one person per service is viable (Notification is the simplest: no real compensator or relational database)
- T044 to T047 in parallel

---

## Parallel Example: User Story 1

```bash
# US1 tests in parallel (different files):
Task: "MessageClassifierTests in the 4 test projects"
Task: "PoisonMessageTests in the 4 test projects"
Task: "FailureCompensatorTests in Transaction, Balance and Transfer"

# US1 implementation in parallel (one file per service):
Task: "MessageClassifier in the 4 services"
Task: "FailureCompensator in Transaction, Balance and Transfer"
```

---

## Implementation Strategy

### MVP First (User Story 1)

1. Phase 1 (Setup) and Phase 2 (Foundational): behavior does not change and the flow keeps completing (T013)
2. Phase 3 (US1): poison messages to the DLQ with a reason, and cancellation of the transfer when there is a `CorrelationId`
3. **STOP and VALIDATE**: section B of quickstart.md and the suites green

### Incremental Delivery

1. Setup + Foundational → scaffolding without behavior changes
2. + US1 → poison messages set aside (MVP)
3. + US2 → retries with delay (closes the cause of transfers lost on brief outages)
4. + US3 → guaranteed closure when retries are exhausted
5. + US4 → complete observability
6. Polish → 5-check CI, constitution 1.2.0, documentation

### Notes

- If a shorter scope is preferred: US1 + US2 in Transfer and Transaction first (the services with tests and closest to the flow), and
  Balance and Notification afterwards. It must be decided before starting T001, because it changes which test projects are created
- Commit after each phase **only if the repository owner says so**
- Before opening the pull request: all tests verified red-green, the 5 CI checks green, and T042/T043 resolved if the new checks are to be required
