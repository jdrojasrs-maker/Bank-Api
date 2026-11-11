# Data Model: Resilient message consumption

This feature **adds no tables, columns or migrations**. All the retry state travels in the message itself. The following entities
are design concepts (in-memory classes or configuration values), not persistence.

## FailureClassification

Result of classifying a failure. It determines what is done with the message.

| Value | When | Action |
|---|---|---|
| `Poison` | The message cannot be interpreted or lacks mandatory data | Immediate DLQ (+ `*-failed` if there is a `CorrelationId`) |
| `Permanent` | The database rejects the data (SQL 515, 547, 8115, 8152) | Resolved by `ProcessService`: it emits its `*-failed` and completes (unchanged) |
| `Transient` | Any other processing exception (except `JsonException`, which is `Poison`) | Scheduled retry with a growing delay |

Note: uniqueness (SQL 2601/2627) **is not a failure**: it means "already processed" and `ProcessService` absorbs it (FR-011).

## PoisonCheck (result of `MessageClassifier`)

| Field | Type | Description |
|---|---|---|
| `IsPoison` | bool | True if the message is poison |
| `Detail` | string | What was wrong, readable (e.g. "El cuerpo del mensaje está vacío"; the runtime strings are in Spanish) |
| `CorrelationId` | string? | The `CorrelationId` if it could be read; `null` if not. Max. 64 characters (longer ones are truncated only for logging) |

**Validation rules** (each one produces `Poison`):
1. Empty body or only whitespace.
2. Not valid JSON.
3. It is JSON `null` or not an object.
4. `CorrelationId` missing, null or blank.
5. `CorrelationId` longer than 64 characters (the same rule Transfer and Transaction already apply).

6. The body does not deserialize to the service's entity (for example a field with the wrong type, `"CustomerId":"abc"`, since `CustomerId` is an integer in the four entities): it is the same
   deserialization attempt that `ProcessService` makes, done earlier so that the message can be set aside.

As a second line of defense, a `JsonException` thrown **during** processing is also treated as `Poison` in
`ResilientMessageProcessor`: it is a permanent error, retrying does not fix it.

## RetryPolicy

| Value | Type | Description |
|---|---|---|
| `MAX_RETRIES` | int = 5 | Retries after the first execution (6 executions in total) |
| `BASE_DELAY_SECONDS` | int = 5 | Base delay |
| `GROWTH_FACTOR` | int = 3 | Multiplier per retry |
| `MAX_DELAY_SECONDS` | int = 300 | Cap on the delay |
| `JITTER` | decimal = 0.2 | Random variation ±20 % |

`NextDelay(retry, random)` is a pure function: `min(BASE · FACTOR^(retry−1), MAX) × (1 ± JITTER)`. Table in [research.md](research.md) R2.

## RetryAttempt (message property)

| Aspect | Value |
|---|---|
| Name | `RetryAttempt` (application property of the message) |
| Type | integer |
| Absent | equivalent to 0 (first delivery of the original message) |
| Who writes it | the consumer, when scheduling the copy: `RetryAttempt = previous attempt + 1` |
| Who reads it | the consumer, when receiving a message, to know how many retries it has |
| Maximum value | `MAX_RETRIES`; if a transient failure occurs with `RetryAttempt = MAX_RETRIES`, it is compensated and set aside |

The remaining properties and the body of the original message are copied unchanged, so that the copy is indistinguishable to the
service except for `RetryAttempt`.

## DeadLetterRecord (what stays in the DLQ)

| Field | Source | Example |
|---|---|---|
| `DeadLetterReason` | Fixed constant | `PoisonMessage` · `RetriesExhausted` · `CompensationFailed` |
| `DeadLetterErrorDescription` | Readable detail, ≤ 1,024 characters | "Reintentos agotados tras 6 intentos. Último error: SqlException: Timeout expired" (runtime string, in Spanish) |
| `CorrelationId` (property) | From the body, if readable | `a3f1…` |
| `Attempts` (property) | `RetryAttempt + 1` | `6` |

## LogEntry (logging contract)

Structured fields, with fixed names in the template (see [contracts/message-handling.md](contracts/message-handling.md)):
`CorrelationId`, `MessageId`, `MessageType`, `Attempt`, `Classification`, `Reason`.

## CompensationDecision (safety rule, FR-016)

Decision taken by `FailureCompensator` before emitting a `*-failed`. It applies only to the first-step subscription of Transaction,
Balance and Transfer; for any other it returns `false` without querying.

| Situation | Decision | What is logged |
|---|---|---|
| There is no record of the `CorrelationId` in the service's database | **Emit** the `*-failed` | `Information` |
| A record exists (any state) | **Do not emit**: the step already had effect | `Warning` with `Reason = "AlreadyRegistered"` |
| The database cannot be queried | **Emit** | `Warning` with the cause of the failed query |
| Unreadable `CorrelationId` | **Do not emit** (nothing to correlate with) | `Warning` |

Record queried: `Transaction` (Transaction), `Balance` (Balance) and `TransferRequest` (Transfer). The message is set aside to the DLQ
in all cases; this decision only governs the failure event.

## Transitions of a message's processing

```text
received ──► poison? ──yes──► [compensate if there is a CorrelationId and no own record] ──► DLQ (PoisonMessage)
   │
   no
   ▼
 ProcessService.Execute
   │
   ├─ success ─────────────────────────────────────────► complete
   ├─ permanent data error / duplicate ─ (resolved by ProcessService) ─► complete
   └─ exception (transient)
        ├─ RetryAttempt < MAX_RETRIES ─► schedule copy (RetryAttempt+1, growing delay) ─► complete the original
        └─ RetryAttempt = MAX_RETRIES ─► [compensate if there is no own record] ─► DLQ (RetriesExhausted)
        (cancellation due to shutdown ─► no retry; the broker redelivers after the restart)
```
