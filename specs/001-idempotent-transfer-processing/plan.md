# Implementation Plan: Idempotent transfer processing

**Branch**: `001-idempotent-transfer-processing` (branch not created; the work continues on `wb-feat-s301001`) | **Date**: 2026-11-03 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/001-idempotent-transfer-processing/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

Main requirement: a repeated `transfer-initiated` with the same `CorrelationId` must leave **a single transfer and a single
result**, and a transaction in a final state (`Completed`/`Canceled`) must **never** go back to `Pending` or trigger events because
of a duplicate or late message.

Technical approach (details in [research.md](research.md)):
- **Transfer**: new `TransferRequest` table with a unique index by `CorrelationId` that remembers the result (rejections included)
  and the emission state, with a 30 s *claim* with *lease* so that only one processor emits; `CorrelationId` bounded to 64 characters
  with a unique index in `Transfer` as well.
- **Transaction**: state machine with valid transitions only from `Pending`, through an atomic conditional update,
  emitting before confirming the transition so that recovery is not lost; final states are ignored without events.
- **Record** of ignored messages with structured logging; **automated tests** with a real SQL Server in a container, one test project
  per service.
- No changes to event names or payloads.

## Technical Context

**Language/Version**: C# / .NET 10 (`net10.0`), nullable and implicit usings enabled

**Primary Dependencies**: ASP.NET Core (Web SDK), EF Core 10.0.12 (SqlServer), Azure.Messaging.ServiceBus 7.21.0, MediatR 14.2.0, Newtonsoft.Json 13.0.4. Tests: xUnit, Microsoft.NET.Test.Sdk and Testcontainers for SQL Server (versions to be pinned when implementing; not verified in this session)

**Storage**: SQL Server (one database per service: `BankTransfer`, `BankTransaction`); EF Core migrations per service

**Testing**: xUnit with a real SQL Server in a container (Docker) and a double of `IServiceBusSenderService`; one project per service. There is no test project today

**Target Platform**: .NET services on the host (locally with `dotnet run --launch-profile http`), with an Azure Service Bus emulator and SQL Server in containers

**Project Type**: event-driven microservices (solution `Bank.slnx`); two services are modified here

**Performance Goals**: no quantitative goal of its own; the cost added per message is one indexed query and one to three small writes

**Constraints**: no project references or HTTP between services; no changes to the event contract; migrations applied by command; the immediate retries of the messaging system (finding 1) remain in force; `LockDuration` of the subscriptions = 1 min

**Scale/Scope**: two services (Transfer and Transaction), 2 migrations, 1 new table, 2 test projects; Balance, Notification and Gateway out of scope

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle / rule | Assessment before research | Re-assessment after the design |
|---|---|---|
| I. Decoupled by events only | ✅ Only the behavior on events changes; no references or HTTP between services | ✅ Each test project references only its service; no contract changes |
| II. Idempotent by `CorrelationId` | ✅ It is the goal of the feature | ✅ Unique index in the database + requests table + *claim*; the prior lookup is not the only guarantee |
| III. Every error path emits its failure | ⚠️ Messages without `CorrelationId` do not admit a correlatable failure event | ⚠️ Justified deviation (see *Complexity Tracking*). Permanent rejections keep emitting `*-failed` and, now, **only once** |
| IV. Each service owns its DB | ✅ | ✅ `TransferRequest` and the indexes live in their service's database |
| V. Every fix comes with an automated test | ✅ The test projects that do not exist today are created | ✅ Each scenario of the spec has a test (see `quickstart.md`); tests are written before the code |
| Technical constraints: event contract | ✅ | ✅ Same names and fields (`contracts/events.md`); `Config.json` is not touched |
| Technical constraints: secrets | ✅ | ✅ The tests use the container's ephemeral credentials; no secret in the repo |
| Technical constraints: migrations by command | ✅ | ✅ Two new migrations, applied with `apply-migrations.ps1` |
| Workflow: technical debt is only pointed out | ✅ | ✅ Retries without wait, Outbox and Balance idempotency are not fixed; they are pointed out |
| Workflow: no commits without an express order | ✅ | ✅ |

**Result**: ✅ No blocking violations. One justified deviation recorded below.

## Project Structure

### Documentation (this feature)

```text
specs/001-idempotent-transfer-processing/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output (/speckit-plan command)
├── data-model.md        # Phase 1 output (/speckit-plan command)
├── quickstart.md        # Phase 1 output (/speckit-plan command)
├── contracts/
│   └── events.md        # Phase 1 output (/speckit-plan command)
├── checklists/
│   └── requirements.md  # Specification quality (/speckit-specify)
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

### Source Code (repository root)

```text
src/
├── Bank.Transfer/Bank.Transfer.Api/
│   ├── Domain/
│   │   ├── Constants/            # + result (Confirmed/Failed) and EMISSION_LEASE
│   │   └── Entities/             # + TransferRequestEntity
│   ├── Application/
│   │   ├── Database/             # IDatabaseService: + TransferRequest DbSet
│   │   └── Features/Process/     # ProcessService (rewrites TransferInitiated); DatabaseErrors (+ uniqueness violation)
│   └── Persistence/
│       ├── Database/             # DatabaseService: length and unique indexes
│       └── Migrations/           # + AddIdempotency migration
└── Bank.Transaction/Bank.Transaction.Api/
    ├── Application/
    │   └── Features/Process/     # ProcessService (state machine); DatabaseErrors (+ uniqueness violation)
    └── Persistence/
        ├── Database/             # DatabaseService: length and unique index
        └── Migrations/           # + AddCorrelationIdUniqueness migration

tests/
├── Bank.Transfer.Api.Tests/      # new: scenarios of stories 1 and 3 and edge cases
└── Bank.Transaction.Api.Tests/   # new: scenarios of story 2

Bank.slnx                         # + the two test projects
CLAUDE.md, README.md              # update: tests now exist and how to run them
```

**Structure Decision**: the per-service structure of `CLAUDE.md` is respected (`Domain/`, `Application/`, `Persistence/`); the logic goes in
`ProcessService` and the new entity in `Domain/Entities`. The test projects live in `tests/`, **one per service**, so that
no project references more than one service.

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Principle III: a message without `CorrelationId` or unreadable is recorded and completed without emitting `*-failed` | Without an identifier there is no failure event that can be correlated with any transfer, and retrying it 10 times is useless | Sending it to the DLQ with reason `PoisonMessage` is more correct, but requires modifying each service's receiver (finding 4, another feature) |
| Two new test projects instead of one | Keep Principle I strict: no project references more than one service | A single project would reference Transfer and Transaction at the same time |
| `TransferRequest` table in addition to `Transfer` | Remember rejections and the emission state (stories 1 and 3); a rejection leaves no row in `Transfer` | Extra columns in `Transfer` cannot remember a rejection (`research.md` R2) |
