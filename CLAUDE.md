# CLAUDE.md

Guide for working in this repository. The full flow and the Mermaid diagram are in [README.md](README.md).

## Overview

.NET 10 microservices (`net10.0`, nullable + implicit usings) with an event-driven architecture on **Azure Service Bus** (locally, its emulator; see "Local environment"). Choreographed saga for a transfer:
`Gateway → Transaction → Balance → Transaction → Transfer → Transaction → Notification (+ Balance)`.

Solution: `Bank.slnx`. Services in `src/Bank.<Service>/Bank.<Service>.Api/` (Gateway uses `.API`).
`src/Bank.Notification/Bank.Notification` is an unused template outside the solution: do not touch it or base anything on it.

## Commands

```bash
dotnet build Bank.slnx
dotnet run --project src/Bank.<Service>/Bank.<Service>.Api --launch-profile http
dotnet user-secrets set "<KEY>" "<value>" --project <project>
```

Always run the services on the host with the **`http`** profile. The Visual Studio "Container (Dockerfile)" profile does not work locally: inside the container `localhost` reaches neither the emulator nor SQL (symptom: `Connection refused` in `ServiceBusSenderService`).

Ports (`http` profile): Gateway 5204, Transaction 5110, Balance 5287, Transfer 5197, Notification 5267. The entry endpoint is `POST http://localhost:5204/api-gateway`.

Automated tests (one project per service: Transfer, Transaction, Balance and Notification). They use xUnit; Transfer, Transaction and Balance use a real SQL Server in a container (Testcontainers) with the real migrations, so they **require Docker to be running** (the Notification tests do not use a database):

```bash
dotnet test tests/Bank.Transfer.Api.Tests
dotnet test tests/Bank.Transaction.Api.Tests
dotnet test tests/Bank.Balance.Api.Tests
dotnet test tests/Bank.Notification.Api.Tests
```

No test project may reference more than one service. Every fix comes with its test (see `.specify/memory/constitution.md`, Principle V).

## Configuration

All sensitive configuration arrives through user-secrets / environment variables (UPPERCASE keys: `SERVICEBUSCONSTR`, `SERVICEBUSTOPIC`, `*SQLDBCONSTR`, `NOTIFICATIONDB*`, `SENDGRID*`). **Never** put secrets in `appsettings*.json` or in commits. Do not read or display the values of secrets or of `.env`; when verifying them, mask passwords.

Current state of the user-secrets (the original Azure resources no longer exist):

| Key | Current value |
|---|---|
| `SERVICEBUSCONSTR` (all 5 services) | Local emulator: `Endpoint=sb://localhost;...;UseDevelopmentEmulator=true;` |
| `SERVICEBUSTOPIC` (all 5) | `bank-transfer` |
| `TRANSACTIONSQLDBCONSTR` / `BALANCESQLDBCONSTR` / `TRANSFERSQLDBCONSTR` | `Server=127.0.0.1,1433;Database=BankTransaction` / `BankBalance` / `BankTransfer`; user `sa`, `TrustServerCertificate=True` |
| `NOTIFICATIONDB*`, `SENDGRID*` | **Pending**: Cosmos DB and SendGrid are not being worked on yet; Notification will not work end to end. Do not try to fix it unless asked. |

Use `127.0.0.1`, not `localhost`: `localhost` resolves to `::1` (IPv6) first and the port is published only on IPv4, which causes timeouts.

## Local environment (`infra/local/`)

The whole local environment lives here; the services run on the host with `dotnet run` and use the following in containers:

| File | Purpose |
|---|---|
| `Config.json` | Service Bus emulator entities: namespace `sbemulatorns`, topic `bank-transfer` and 11 subscriptions (one per event), each with a `CorrelationFilter` rule whose `Label` is the event name (equivalent to the message `Subject`). |
| `docker-compose.yml` | Two containers: `bank-sqlserver` (`mssql/server:2022-latest`, published on `127.0.0.1:1433`) and `bank-servicebus-emulator` (ports `5672` AMQP and `5300` health/admin). The emulator uses SQL Server as internal storage and waits for it to be `healthy`. |
| `.env.example` | Variables template. Copy to `.env` (ignored by git) and set `SQL_PASSWORD` (min. 8 characters and 3 of 4 classes: uppercase, lowercase, digits, symbols). **`.env.example` never contains a password.** |
| `apply-migrations.ps1` | Applies the EF Core migrations to the three databases. |
| `smoke-test/` | Console app that verifies that routing by `Subject`/`Label` works (`dotnet run`, 4 `[OK]` checks). |

### Operation

```powershell
cd infra/local
docker compose up -d                  # SQL Server + emulator
.\apply-migrations.ps1                # create/update the schema (after every "down")
curl.exe http://localhost:5300/health # 200 = emulator ready
docker compose restart emulator       # after editing Config.json
docker compose down                   # shut down (wipes everything, see below)
```

Emulator connection string: `Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;`

### Non-obvious behaviors

- **No persistence** (documented by Microsoft): after restarting the emulator, messages and entities are lost; only those in `Config.json` are recreated. SQL Server has no volume either: `docker compose down` wipes the three databases, so `apply-migrations.ps1` must be run again.
- **If the SQL container is recreated, restart the emulator too** (`docker compose restart emulator`): its metadata lived in that database.
- Changes to `Config.json` are not applied live: they require restarting the emulator. `Config.json` takes precedence over entities created with the Administration Client (natively supported only in .NET, with port 5300 in the connection string).
- Emulator limits: a single namespace (`sbemulatorns`, cannot be renamed), 50 subscriptions per topic, 256 KB messages, maximum TTL of 1 h, 10 concurrent connections, AMQP over TCP only, no Entra ID.
- When adding or renaming an event: update the constants in sender and receiver, `infra/local/Config.json` (subscription + rule) and the README table.

### EF Core migrations (Transaction, Balance, Transfer)

- Tool pinned in `dotnet-tools.json` (`dotnet-ef` 10.0.12); use `dotnet tool run dotnet-ef ...` from the root, not the global one.
- Each service has its migration in `Persistence/Migrations`. For a model change:
  ```powershell
  dotnet tool run dotnet-ef migrations add <Name> --project src/Bank.<Service>/Bank.<Service>.Api --output-dir Persistence/Migrations
  .\infra\local\apply-migrations.ps1
  ```
- `Microsoft.EntityFrameworkCore.Design` is a development-only dependency (`PrivateAssets=all`).
- Migrations are applied by command, not when the service starts.
- Notification uses Cosmos DB, with no migrations.

## Continuous integration (GitHub Actions)

`.github/workflows/ci.yml` runs on every pull request to `main` or `develop` and on every push to those branches:

- **Build**: restores and compiles `Bank.slnx` in Release (plus an informational audit of vulnerable packages that does not block).
- **Tests (Transfer)**, **Tests (Transaction)**, **Tests (Balance)** and **Tests (Notification)**: one job per service, in parallel. The first three use the runner's Docker to start SQL Server with Testcontainers; Notification does not use a database. None requires secrets.
- The five checks are required in the protection of `main` and `develop` (a GitHub web setting, not a file); their names must match the job names exactly (`Build`, not `build`). Do not disable or relax a check to make a change pass: fix the cause.
- Flow: new branch from an up-to-date `develop` (`feat/`, `fix/`, `ci/`) → push → pull request to `develop` with the five checks green → merge. Never commit directly to `main`/`develop`.
- Each test job uploads its `.trx` as an artifact, and a new run on the same PR cancels the previous one.
- `System.Security.Cryptography.Pkcs` is pinned to 10.0.14 in Transfer, Transaction and Balance only to fix the vulnerable version (9.0.11) that `Microsoft.Data.SqlClient` 6.1.6 drags in; remove the reference when SqlClient fixes it. The CI audit step must come out clean.
- The runner is Linux (case-sensitive): take care with the capitalization of paths and of references between projects.
- Workflows cannot be run locally; they are validated by pushing the branch and looking at the *Actions* tab.

## Structure per service (respect it)

- `Domain/` — `Constants` (event and state names), `Entities`, `Events` (`ProcessEvents : INotification`). No external dependencies.
- `Application/` — interfaces (`IProcessService`, `IDatabaseService`, `IServiceBusSenderService`, ...), `Handlers`, `Models`. Business logic in `ProcessService`.
- `External/` — adapters: `ServiceBusRecive` (BackgroundService), `ServiceBusSender`, `SendGridEmail`.
- `Persistence/` — `DatabaseService` (EF Core `DbContext` or `CosmosClient`).
- `Program.cs` — DI registration only.

Consumption pattern: `ServiceBusReciveService` → `IMediator.Publish(ProcessEvents)` → `ProcessHandler` (creates a scope) → `ProcessService.Execute`. The `ProcessService` classes are *scoped*, the senders *singleton*.

## Event conventions

- A single topic (`SERVICEBUSTOPIC`); the event name goes in `ServiceBusMessage.Subject` and matches the subscription name (kebab-case, e.g. `transfer-initiated`).
- The names live in `Domain/Constants` of each service (`ReceivedSubscriptionsConstants` / `SendSubscriptionConstants`). **The strings must match across services**; when adding or renaming an event, update sender, receiver, `infra/local/Config.json` (and the subscription in Azure, if it exists) and the README table.
- JSON payload; the `CorrelationId` created in the Gateway travels in every event and must be the idempotency/lookup key in every database.
- States: `Pending`, `Completed`, `Canceled` (Transfer uses `Complete`). In Transaction, `Completed` and `Canceled` are **final**: no duplicate or late message changes them.
- Serialization: senders with `System.Text.Json`, receivers with `Newtonsoft.Json` (case-insensitive). Keep field compatibility.

## Idempotency and ignored messages

- **Transfer**: `TransferRequest` (one row per `CorrelationId`, unique index) stores the result (`Confirmed`/`Failed`, rejections too), the content fingerprint and whether it was already emitted. Emission is taken with a *claim* of `EMISSION_LEASE_SECONDS` (30 s): a duplicate neither registers nor emits again, and a pending emission is only recovered once the lease has expired.
- **Transaction**: only `transaction-initiated` creates the row; transitions are applied only from `Pending` with a conditional update, and the event is emitted **before** the transition is confirmed (a send failure leaves it `Pending` and the redelivery retries).
- `CorrelationId` is `nvarchar(64)` with a unique index in `Transfer`, `TransferRequest` and `Transaction`. A uniqueness violation (SQL 2601/2627) means "already processed", **not** a failure: `DatabaseErrors.IsUniqueViolation` tells it apart from `IsPermanent`.
- An ignored message (duplicate, late, without `CorrelationId`) is completed and logged with `CorrelationId`, `MessageType` and `Reason` (`IgnoredMessageReasonConstants` constants).
- Design and decisions: `specs/001-idempotent-transfer-processing/` (`research.md`, `data-model.md`).

## Resilient consumption (feature 002)

Details and decisions: `specs/002-resilient-message-consumption/` (`contracts/message-handling.md`, `research.md`). The code lives in `Application/Resilience/` of each service and is **deliberately duplicated** across the four (the constitution forbids a shared project).

- The receivers use `AutoCompleteMessages = false`; `ResilientMessageProcessor.HandleAsync` decides whether to complete, reschedule or set aside each message.
- **Three classes of error**: *poison* (empty or unreadable body, no `CorrelationId`, `CorrelationId` longer than 64 characters, field with the wrong type) → DLQ on the first delivery with reason `PoisonMessage`; *permanent data error* (SQL 515/547/8115/8152) → resolved by `ProcessService` (emits `*-failed` and completes); *transient* (everything else) → retry. Uniqueness violation (2601/2627) = "already processed".
- **Retries**: a copy is scheduled with `ScheduleMessageAsync` (same `Subject`, `RetryAttempt` property) and the original is completed **afterwards**. Up to 5 retries with a delay of `5 s·3^(n−1)`, capped at 300 s, ±20 % (`RetryPolicy`). A graceful service shutdown neither reschedules nor counts as a failure.
- **Retries exhausted**: the failure is compensated and the message goes to the DLQ with reason `RetriesExhausted` (`Attempts = 6`, last error in the detail, max. 1,024 characters). If the failure event could not be emitted, the reason is `CompensationFailed`.
- **Compensation of the first step only**: `transaction-initiated` → `transaction-failed`, `balance-initiated` → `balance-failed`, `transfer-initiated` → `transfer-failed`. Before emitting, `FailureCompensator` checks that its own DB has **no** record of the `CorrelationId` (if it does, it does not emit: the saga has already progressed); if the DB does not respond, it emits. The payload is `{CorrelationId, CustomerId}` and **omits** `CustomerId` if it could not be read (an explicit `null` would make the consumer reject it as poison). Handlers that only update state do not compensate: they are logged and set aside.
- `ProcessError` **only logs** (`ProcessorErrorHandler`): it does not set aside, complete or compensate.
- Every message set aside or rescheduled is logged with `CorrelationId`, `MessageId`, `MessageType`, `Attempt`, `Classification` and `Reason`.
- Manual messages for testing (invalid body, `RetryAttempt`): `infra/local/duplicate-test` with `--raw`.

## EDD principles to respect when changing code

- Decoupled services: no project references or HTTP calls between services; events only.
- **Idempotent** handlers (Service Bus delivers *at-least-once*): check by `CorrelationId` before inserting.
- Every error path must emit its failure/compensation event (`*-failed`) instead of getting lost.
- Each service owns its DB; do not share tables.

## Known technical debt (see README)

No Outbox, update-only handlers (e.g. `balance-confirmed`, `transfer-confirmed-balance`) without a failure event when retries are exhausted (they are only logged and go to the DLQ), no DLQ length alert or reprocessing tool, Balance and Notification without idempotency, hardcoded email recipient in Notification, senders created per message without dispose, Gateway without auth or `CorrelationId` in the response, empty `Transfer/External/SendGridEmail`. If a change touches these areas, point it out, but do not fix it unless asked.

## Style

Follow the existing code: file-scoped namespaces, `UPPER_SNAKE_CASE` constants, `Service`/`Handler` suffixes, one `Execute` per service/adapter. Note: the `ServiceBusRecive` names (without the "e") are the real ones; do not "fix" them partially.

## Language

All repository documentation (Markdown files, workflow and infrastructure comments) is written in English. Runtime strings that already exist in the code (log messages, dead-letter details) are still in Spanish; do not mix: change them in code and tests together, never in documentation alone.
