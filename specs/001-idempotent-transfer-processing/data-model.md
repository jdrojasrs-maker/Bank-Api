# Data model (Phase 1): Idempotent transfer processing

Each service owns its database (Principle IV): no table is shared. The changes are EF Core migrations per service.

## `BankTransfer` database

### `Transfer` (changes)
| Field | Type | Change |
|---|---|---|
| `CorrelationId` | `nvarchar(64)` NOT NULL | Goes from `nvarchar(max)` to a bounded length (`HasMaxLength(64)`) and a **unique index** `IX_Transfer_CorrelationId` |
| remaining fields | unchanged | `Id`, `TransferDate`, `CurrentState` (`Complete`), `Amount`, `SourceAccount`, `DestinationAccount`, `CustomerId` |

Rule (FR-001): at most one row per `CorrelationId`.

### `TransferRequest` (new; record of processed requests)
| Field | Type | Description |
|---|---|---|
| `Id` | `int` identity, PK | |
| `CorrelationId` | `nvarchar(64)` NOT NULL, **unique index** `IX_TransferRequest_CorrelationId` | Idempotency key |
| `Outcome` | `nvarchar(16)` NOT NULL | `Confirmed` or `Failed`: result decided at the first processing |
| `PayloadFingerprint` | `nvarchar(64)` NOT NULL | SHA-256 in hexadecimal of `Amount|SourceAccount|DestinationAccount|CustomerId`; detects different content with the same identifier (FR-008) |
| `CreatedAt` | `datetime2` NOT NULL | Moment of the record |
| `ClaimedAt` | `datetime2` NULL | Moment a processor took the emission (start of the lease) |
| `EmittedAt` | `datetime2` NULL | Moment the result was emitted; once reported it is **not erased** |

Relationship: a `Transfer` row exists only if `Outcome = Confirmed`; both are recorded in the **same transaction**.
A rejection (`Failed`) leaves a row only in `TransferRequest`.

#### `TransferRequest` lifecycle

```text
(does not exist) --atomic record--> Recorded(ClaimedAt = t0, EmittedAt = NULL)
Recorded --successful emission--> Emitted (EmittedAt = t1)            [terminal state]
Recorded --emission failure--> Recorded (ClaimedAt = NULL)             [the redelivery can recover]
Recorded --lease expired (ClaimedAt < now - 30 s)--> Recorded (new ClaimedAt)  [recovery after a crash]
```

Atomic operations (conditional updates; they return affected rows):
- **Claim**: `ClaimedAt = now` where `Id = @id AND EmittedAt IS NULL AND (ClaimedAt IS NULL OR ClaimedAt < now - lease)`. 1 row ⇒ the emission is taken.
- **Mark emitted**: `EmittedAt = now` where `Id = @id AND EmittedAt IS NULL`.
- **Release**: `ClaimedAt = NULL` where `Id = @id AND EmittedAt IS NULL`.

Domain constant: `EMISSION_LEASE = 30 s`.

#### `transfer-initiated` decision tree

| Situation when the message arrives | Action | Event | Recorded reason |
|---|---|---|---|
| No `CorrelationId` or unreadable | Nothing; complete | none | `InvalidMessage` |
| `TransferRequest` does not exist | Record `Transfer` + `TransferRequest(Confirmed)` atomically | `transfer-confirmed` | — |
| same, and the record throws a **permanent** error | Record only `TransferRequest(Failed)` | `transfer-failed` | — |
| same, and the record throws a **uniqueness violation** | It was a simultaneous duplicate; complete | none | `DuplicateInProgress` |
| same, and the error is **transient** | Propagate (messaging retries) | none | — |
| Exists, `EmittedAt` filled in | Complete | none | `DuplicateAlreadyEmitted` |
| Exists, not emitted, lease **valid** | Complete | none | `DuplicateInProgress` |
| Exists, not emitted, lease **expired**, *claim* obtained | Emit the stored `Outcome`; mark emitted | `transfer-confirmed` or `transfer-failed` | `RecoveredEmission` (informational) |
| Exists, different fingerprint | Like the previous rows and, in addition, record | as appropriate | `ContentMismatch` |

## `BankTransaction` database

### `Transaction` (changes)
| Field | Type | Change |
|---|---|---|
| `CorrelationId` | `nvarchar(64)` NOT NULL | Bounded and **unique index** `IX_Transaction_CorrelationId` |
| `CurrentState` | text | No type change; now governed by the state machine |
| remaining fields | unchanged | |

### `Transaction` state machine

```text
            transaction-initiated
(does not exist) ----------------------> Pending --balance-confirmed--> Pending (the flow advances)
                                       |
                 transfer-confirmed    |    balance-failed / transfer-failed
                       v               |               v
                   Completed <---------+---------> Canceled
                   (final)                          (final)
```

| Message | Current state | Result | Events emitted |
|---|---|---|---|
| `transaction-initiated` | does not exist | Inserts `Pending` | `balance-initiated` |
| `transaction-initiated` (repeated) | `Pending` | No state change | `balance-initiated` (recovery: at-least-once delivery) |
| `transaction-initiated` (repeated) | `Completed` / `Canceled` | **Ignored** | none (`FinalStateProtected`) |
| `balance-confirmed` | `Pending` | No state change | `transfer-initiated` |
| `balance-failed` | `Pending` | → `Canceled` | `transaction-failed` |
| `transfer-failed` | `Pending` | → `Canceled` | `transaction-failed`, `transfer-failed-balance` |
| `transfer-confirmed` | `Pending` | → `Completed` | `transaction-completed`, `transfer-confirmed-balance` |
| any message | `Completed` / `Canceled` | **Ignored**, never changes state (including to the other final state) | none (`FinalStateProtected`) |
| anything except `transaction-initiated` | does not exist | **Ignored** | none (`NoSuchTransaction`) |

Order of operations for the `Pending → final` transitions: read state → emit events → conditional transition
(`WHERE CorrelationId = @id AND CurrentState = 'Pending'`). If the transition returns 0 rows after emitting, another delivery
already applied it: it is recorded and that is the end (see `research.md` R4).

## Spec entities → model

| Spec entity | Model |
|---|---|
| Transfer | `Transfer` |
| Transaction | `Transaction` |
| Transfer start message | `transfer-initiated` event (see `contracts/events.md`) |
| Transfer result | `transfer-confirmed` / `transfer-failed` events; their emission state lives in `TransferRequest` |
| Ignored-message record | Structured log (`ILogger`) with `CorrelationId`, `MessageType`, `Reason` |
