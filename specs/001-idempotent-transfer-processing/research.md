# Research (Phase 0): Idempotent transfer processing

Resolves the open design decisions before modeling. Evidence: **[CODE]** read in the repo,
**[TEST]** observed in previous sessions, **[ANALYSIS]** reasoned and not tested, **[VERIFY]** to be checked when implementing.

## R1. Idempotency key and its guarantee

- **Decision**: `CorrelationId` is the key. The final guarantee is given by a **unique index** in each service's database
  (Transfer and Transaction), not just by a prior lookup.
- **Finding [CODE]**: `CorrelationId` is a `string` and the migrations create it as `nvarchar(max)`; SQL Server **does not allow
  indexing `nvarchar(max)`**. It has to be bounded with `HasMaxLength(64)` (a GUID as text takes 36 characters) through a
  migration that alters the column and creates the index.
- **Rationale**: two simultaneous deliveries can both pass the prior lookup and insert (*check-then-insert*);
  only the constraint in the database prevents it. Meets Principle II.
- **Rejected alternatives**:
  - Prior lookup only: not safe under concurrency.
  - Changing `CorrelationId` to `Guid`: breaks the compatibility of the JSON contract and forces migrating data and senders.

## R2. Where to remember that a transfer was already processed (rejections included)

- **Decision**: new table **`TransferRequest`** in Transfer's database, one row per `CorrelationId`, with the result
  (`Confirmed`/`Failed`), a content fingerprint and emission marks.
- **Rationale**: story 1 requires that a duplicate of a **rejected** message does not emit a second failure
  (scenario 3). A rejection leaves no row in `Transfer`, so adding columns to `Transfer` cannot remember it.
  The fingerprint allows detecting the same identifier with different content (FR-008).
- **Rejected alternatives**:
  - Extra columns in `Transfer`: does not cover rejections.
  - Storing the rejection as a `Transfer` row with state `Rejected`: the `INSERT` could fail again because of the same constraint.
  - The broker's own duplicate detection by `MessageId`: it does not cover redeliveries of the same message after a failure nor
    recovery, and its support in the emulator is **[VERIFY]**.

## R3. Single emission of the result with simultaneous duplicates

- **Decision**: *claim* with **lease** on `TransferRequest`. Whoever records the row becomes the emitter (`ClaimedAt = now`).
  A duplicate that finds the row:
  1. with `EmittedAt` filled in: ignores and records the reason;
  2. not emitted and with a valid lease: ignores (the original message provides the recovery);
  3. not emitted and with an expired lease: tries to take the *claim* with an atomic update and, if it succeeds, emits.
  If the emission throws an exception, the emitter **releases** the *claim* before rethrowing so that the redelivery can recover.
  Proposed lease: **30 s** (longer than any normal emission and shorter than the 1 min `LockDuration` of the subscriptions, so
  a process crash is recovered on redelivery through lock expiry). **[CODE]** `LockDuration` PT1M in `Config.json`.
- **Rationale**: the "almost simultaneous duplicate" case is not only that of two inserts at the same time: a second copy can
  arrive after the record's *commit* and before the emission is marked; without a *claim* it would emit a second result (SC-002).
- **Accepted residual risk**: an emission that takes longer than the lease can coincide with a redelivery that emits it again.
  Consumers must be idempotent (Principle II) and this is the limit already declared in the Spec ("Single-emission guarantee").
- **Rejected alternatives**:
  - No *claim* (only an `Emitted` flag): leaves a millisecond window with a duplicate result; fails SC-002.
  - Transactional Outbox: solves atomicity, but it is finding 5, of much larger scope; out of this scope.
  - Rejecting by exception while the lease is valid: burns deliveries (retries are immediate, see finding 1).

## R4. Final-state protection in Transaction

- **Decision**: state machine with valid transitions only from `Pending` and an **atomic conditional update**
  (`UPDATE … WHERE CorrelationId = @id AND CurrentState = 'Pending'`, with `ExecuteUpdateAsync`); `Completed` and `Canceled` are final.
  **Emit first, confirm afterwards** order for the transitions (`balance-failed`, `transfer-failed`, `transfer-confirmed`):
  the state is read, if it is `Pending` the events are emitted and the conditional transition is confirmed.
- **Rationale**: once the transition is made, a redelivery finds the final state and is **ignored without emitting** (FR-005, FR-006).
  If the transition were confirmed *before* emitting, a send failure would leave the final state and the events unsent, and the
  redelivery could no longer recover them (a regression compared with today). With *emit first*, a send failure leaves the state
  in `Pending` and the redelivery retries, preserving at-least-once delivery.
- **Known cost**: two simultaneous copies can both emit before one confirms; it is a millisecond window with
  duplicate events (not with a contradictory state). Consumers are idempotent (Transfer will be after this feature;
  Balance does *upsert*). The spec only requires protecting the **final state**, not single emission in Transaction.
- **Rejected alternatives**:
  - `RowVersion` (optimistic concurrency) with *upsert*: protects against overwriting each other, but forces retrying and keeps the
    *upsert* that already regresses states.
  - Confirm and then emit: loses recovery (above).
  - Keep the current *upsert* and only compare states in memory: no atomicity (*lost update*).
- **Additional rule**: only `transaction-initiated` can **create** the row; any other message with no existing row is ignored and recorded
  (today the *upsert* would create a row with an empty amount).

## R5. Record of ignored messages (FR-007, SC-006)

- **Decision**: **structured logging** with `ILogger` (Warning level) with properties `CorrelationId`, `MessageType`, `Reason`
  (`DuplicateAlreadyEmitted`, `DuplicateInProgress`, `FinalStateProtected`, `NoSuchTransaction`, `ContentMismatch`, `InvalidMessage`).
- **Rationale**: meets "locate by `CorrelationId` with its reason" at minimal cost and without one more table per service.
  The Spec does not set retention.
- **Rejected alternative**: an `IgnoredMessage` table per service: more schema, migrations and maintenance without a requirement that demands it.
  It can be added later without changing the behavior.
- **Limit**: console logs are not durable; collection and retention depend on the observability platform.

## R6. Message without `CorrelationId` or unreadable (FR-010)

- **Decision**: it is validated at the start of `Execute`; if the identifier is missing or the body cannot be interpreted, it is recorded
  (`InvalidMessage`), nothing is written and the message is completed.
- **Rationale [CODE]**: today `DeserializeObject` can return `null` and the next line throws `NullReferenceException`,
  with 10 useless retries until the DLQ. Without an identifier there is no correlatable failure event to emit.
  It is recorded as a justified deviation from Principle III in *Complexity Tracking*.
- **Alternative**: `DeadLetterMessageAsync` with reason `PoisonMessage` from the receiver: more correct, but requires changing the
  receiver (finding 4, another feature).

## R7. Testing strategy (Principle V)

- **Decision**: one test project **per service** (`tests/Bank.Transfer.Api.Tests`, `tests/Bank.Transaction.Api.Tests`), xUnit,
  with a **real SQL Server in a container** (Testcontainers for MsSql) and a double of `IServiceBusSenderService` that records the
  emitted events. The database is created by applying the **real migrations**.
- **Rationale**:
  - A real SQL Server is needed: error codes 547/2627, the unique index, `ExecuteUpdateAsync` and concurrency cannot be
    faithfully reproduced with EF InMemory (which does not support these operations) nor with SQLite (other error codes).
  - One project per service prevents a single test project from referencing several services (Principle I, strict).
  - Testing `ProcessService` directly avoids depending on the Service Bus emulator; the contract is covered by the events it emits.
  - Applying the real migrations validates the new migration at the same time.
- **Prerequisite**: Docker running. The image `mcr.microsoft.com/mssql/server:2022-latest` is already on the machine **[TEST]**.
- **Rejected alternatives**: reusing the SQL of the local `docker compose` (not self-contained, shared state and wiped by `down`);
  EF InMemory; SQLite.
- **[VERIFY]** when implementing: versions of `Testcontainers.MsSql`, `xunit` and `Microsoft.NET.Test.Sdk` compatible with `net10.0`
  (the package search in this session returned inconsistent versions and is not used as evidence); pin them with `dotnet add package`.

## R8. Migrations over existing data

- **Decision**: two new migrations (Transfer: `AddIdempotency`; Transaction: `AddCorrelationIdUniqueness`), applied with
  `infra/local/apply-migrations.ps1` following the repo's flow.
- **Risk [ANALYSIS]**: creating the unique index fails if repeated `CorrelationId`s already exist. Locally the databases are disposable
  (`docker compose down` wipes them) and `quickstart.md` has a prior query to detect duplicates. A `CorrelationId`
  longer than 64 characters would also make the type change fail; the current GUIDs are 36 long.

## R9. Interaction with the existing error handling

- The changes to `DatabaseErrors.IsPermanent` (first-step compensation) are kept. The detection of a uniqueness violation
  (SQL 2601/2627) is added as **duplicate, not failure** (the reason it was excluded from permanent errors).
- The immediate retries and the exhaustion of deliveries remain unresolved (finding 1); this feature does not worsen them and the
  recovery by *claim* coexists with them.
