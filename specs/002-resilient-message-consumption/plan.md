# Implementation Plan: Resilient message consumption

**Branch**: `002-resilient-message-consumption` (branch not created; the work continues on `feat/retry-and-poison-messages`) | **Date**: 2026-11-07 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/002-resilient-message-consumption/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

Main requirement: the four services that consume messages (Transaction, Balance, Transfer, Notification) must **distinguish
poison messages, permanent data failures and transient failures**; set poison messages aside to the DLQ on the first delivery with
a reason and detail; **retry the transient ones with a growing delay** instead of using up the 10 deliveries in seconds; **compensate**
(`*-failed`) and set aside when the attempts are exhausted; and log everything with `CorrelationId`.

Technical approach (details and evidence in [research.md](research.md)):
- **One resilience layer per service** (`Application/Resilience/`), without touching `ProcessService`: a processor that decides
  (complete / set aside / retry) from a classification, and a thin adapter in `External/ServiceBusRecive` that executes those
  decisions with the SDK. That way the 103 existing tests do not change.
- **Poison message**: validation before MediatR (empty body, unreadable or `null` JSON, no `CorrelationId`, more than 64 characters).
  `DeadLetterMessageAsync` with reason `PoisonMessage`, detail and `CorrelationId` as a property.
- **Growing delay**: a **copy** of the message is **scheduled** (`ScheduleMessageAsync`) with the `RetryAttempt` counter and the
  original is completed. 5 retries with a delay `min(5 s · 3^(n-1), 5 min)` ±20 % variation (nominal window ≈ 8 min). **Tested in the
  emulator** ([TEST] in research.md): the scheduled delivery works.
- **Retries exhausted**: the first step's `*-failed` is emitted (Transaction, Balance, Transfer) and the message is set aside with reason
  `RetriesExhausted`. Notification and the handlers that only update state have no failure event: they are logged and set aside.
- **`ProcessError`** only logs (origin, entity, exception); it does not set aside or complete.
- **Automated tests** for the decision logic (without a broker, with doubles) and for the compensation; two new test projects
  (Balance with SQL Server in a container, Notification without a database), and an extension of the CI.
- **Compensation safety rule (FR-016)**: before emitting a `*-failed`, the compensator queries the `CorrelationId` record in the
  service's own database; if it already exists, it does not emit (avoids canceling healthy sagas or sending false failure emails).
  If there is no record or the database does not respond, it emits.
- No changes to schema, event names, payloads or `infra/local/Config.json`.

## Technical Context

**Language/Version**: C# / .NET 10 (`net10.0`), nullable and implicit usings enabled

**Primary Dependencies**: Azure.Messaging.ServiceBus 7.21.0, MediatR 14.2.0, Newtonsoft.Json 13.0.4 (reading), System.Text.Json (emission). **No new dependencies** in the services. Tests: xUnit and the packages already used in the Transfer and Transaction projects

**Storage**: N/A. There are no new tables or migrations; the retry state travels in a message property

**Testing**: xUnit. The decision logic is tested with doubles of `IMessageActions` and `IFailureCompensator` and a `process` delegate (no broker or database). The compensator (FR-016) is tested with a real SQL Server in a container in Transaction, Balance and Transfer; Balance gets a new project with Testcontainers (Docker) and Notification a new project without Docker

**Target Platform**: .NET services on the host (`dotnet run --launch-profile http`) with an Azure Service Bus emulator and SQL Server in containers

**Project Type**: event-driven microservices (`Bank.slnx`); four services are modified

**Performance Goals**: no quantitative goal of its own; the cost per healthy message is an in-memory JSON validation. A retry costs one scheduled send and one completion

**Constraints**: no project references or HTTP between services (Principle I) ⇒ the resilience code is **duplicated per service**; no changes to the event contract; `LockDuration` = 1 min, `MaxDeliveryCount` = 10 and TTL = 1 h are kept (safety net); the emulator limits the TTL to 1 h, so the ≈ 8 min window fits comfortably

**Scale/Scope**: 4 services, ≈ 7 new files per service, 2 new test projects, 2 new CI jobs, 1 minor amendment of the constitution

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle / rule | Assessment before research | Re-assessment after the design |
|---|---|---|
| I. Decoupled by events only | ⚠️ The common code cannot be shared in a project | ✅ Duplicated per service (see *Complexity Tracking*); no references or HTTP; the retries use the same topic |
| II. Idempotent by `CorrelationId` | ⚠️ Balance and Notification are not, and they will receive retries | ⚠️ Risk pointed out, **does not worsen**: today they already receive up to 10 deliveries; with a delay it is 6. No changes to their logic (debt: finding 5) |
| III. Every error path emits its failure | ⚠️ It is the goal, but not all handlers have a failure event | ✅ Covers the first steps (`*-initiated`) and the poison messages with `CorrelationId`. ⚠️ The update-only handlers stay logged and set aside, without compensation (finding 6; see *Complexity Tracking*) |
| IV. Each service owns its DB | ✅ | ✅ No new schema. The compensator **reads** only its own service's database (FR-016) |
| V. Every fix comes with an automated test | ✅ | ✅ Each scenario of the spec has a test (see `quickstart.md`); they are written before the code |
| Constraints: event contract | ✅ | ✅ Same names and payloads; only an application property (`RetryAttempt`) is added on the rescheduled messages ([contracts/message-handling.md](contracts/message-handling.md)) |
| Constraints: structure per service | ✅ | ✅ `Application/Resilience/` and `External/ServiceBusRecive/`; `Program.cs` only registers dependencies |
| Constraints: secrets | ✅ | ✅ None new; the emulator connection string is already documented |
| Workflow: an event change requires updating Config.json and the README | ✅ | ✅ No events are added or renamed |
| Workflow: PR with CI green and required checks | ⚠️ | ⚠️ Two jobs are added (`Tests (Balance)`, `Tests (Notification)`): they require a **constitution amendment** and **adjusting the branch protection** (manual step in the GitHub web UI) |
| Workflow: technical debt is only pointed out | ✅ | ✅ Outbox, reconciler, circuit breaker, Balance/Notification idempotency and `ServiceBusSender` without dispose are pointed out, not fixed |
| Workflow: no commits without an express order | ✅ | ✅ |

**Result**: ✅ No blocking violations. Two justified deviations (below) and two manual steps identified.

## Project Structure

### Documentation (this feature)

```text
specs/002-resilient-message-consumption/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output (/speckit-plan command)
├── data-model.md        # Phase 1 output (/speckit-plan command)
├── quickstart.md        # Phase 1 output (/speckit-plan command)
├── contracts/
│   └── message-handling.md   # Phase 1 output (/speckit-plan command)
├── checklists/
│   └── requirements.md  # Specification quality (/speckit-specify)
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

### Source Code (repository root)

```text
src/
├── Bank.Transaction/Bank.Transaction.Api/      # same shape in Balance and Transfer
│   ├── Domain/Constants/                       # + DeadLetterReasonConstants, RetryConstants
│   ├── Application/Resilience/                 # new
│   │   ├── RetryPolicy.cs                      #   maximum retries and delay with variation
│   │   ├── MessageClassifier.cs                #   detects poison messages
│   │   ├── IMessageActions.cs                  #   complete / set aside / reschedule (broker abstraction)
│   │   ├── IFailureCompensator.cs              #   emit the first step's *-failed
│   │   ├── FailureCompensator.cs               #   implementation: queries its own record (IServiceScopeFactory → IDatabaseService) and emits with IServiceBusSenderService
│   │   ├── ProcessorErrorHandler.cs            #   logs the processor's errors (ProcessError)
│   │   └── ResilientMessageProcessor.cs        #   decides what to do with each message
│   ├── External/ServiceBusRecive/
│   │   ├── ServiceBusReciveService.cs          #   gets slimmer; ProcessError logs
│   │   └── ServiceBusMessageActions.cs         #   adapter over ProcessMessageEventArgs
│   └── Program.cs                              #   + DI registration
├── Bank.Notification/Bank.Notification.Api/    # same, without FailureCompensator (registers a null compensator)
└── (Bank.Gateway does not consume messages: no changes)

tests/
├── Bank.Transfer.Api.Tests/       # + Resilience/ (decisions, compensation with real SQL)
├── Bank.Transaction.Api.Tests/    # + Resilience/
├── Bank.Balance.Api.Tests/        # new, with SQL Server in a container (only the compensator uses it)
└── Bank.Notification.Api.Tests/   # new, without a database

.github/workflows/ci.yml           # + Balance and Notification in the test matrix
.specify/memory/constitution.md    # amendment: list of required checks (1.2.0)
Bank.slnx                          # + the two test projects
infra/local/duplicate-test/        # option to send an arbitrary body (poison message) in manual verification
CLAUDE.md, README.md               # document the behavior and how to verify it
```

**Structure Decision**: the per-service structure of `CLAUDE.md` is respected. The decision logic lives in `Application/Resilience/`
(independent of the SDK, hence testable without a broker) and the SDK stays confined to `External/ServiceBusRecive/`. **`ProcessService` is
not modified**: the prior validation and the classification happen before MediatR, and the compensation is a separate class. The test
projects remain **one per service**.

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

| Violation | Why Needed | Simpler Alternative Rejected Because |
|---|---|---|
| Principle I: the resilience code is duplicated across four services | Sharing it would require a project reference or a common package between services | A shared library (or linked files) couples versions and deployments, exactly what Principle I forbids. It is small and stable code; the cost of duplicating it is low |
| Principle III: the update-only handlers (e.g. `balance-confirmed`, `transfer-confirmed` in Transaction; `transfer-confirmed-balance` in Balance; Notification) do not emit `*-failed` when retries are exhausted | There is no failure event defined for those steps; inventing one changes the contract and the saga flow | Inventing new events or a reconciler exceeds this feature (finding 6). They are logged and set aside with a reason, so the DLQ explains them |
| Two new test projects and two new CI jobs | Principle V requires tests for the new behavior of Balance and Notification, and Principle I limits a test project to one service | Testing them from another test project would reference more than one service; not testing them breaks Principle V |
| Amendment of the constitution (1.1.0 → 1.2.0) and manual change of the branch protection | The required checks go from 3 to 5 and the constitution names them expressly | Leaving the new jobs as informational allows merging with broken tests, which contradicts the rule of not relaxing checks |
