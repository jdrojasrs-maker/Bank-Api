# Contract: message handling (poison messages, retries and DLQ)

This feature **does not change** any event name or payload. It only adds conventions about how messages are rescheduled, set aside
and logged. It applies to the four services that consume: Transaction, Balance, Transfer and Notification.

## 1. Rescheduled message (retry)

A retry is a **new** message published to the same topic (`SERVICEBUSTOPIC`) with scheduled delivery.

| Element | Value |
|---|---|
| Body | Identical to the original |
| `Subject` | The same as the original (the event name ⇒ it reaches the same subscription) |
| `ContentType` | The same as the original |
| Application properties | Those of the original plus `RetryAttempt` (integer) |
| `RetryAttempt` | Previous attempt + 1; the original does not have it (= 0) |
| Delivery | `ScheduledEnqueueTimeUtc` = now + delay (see table) |

| Retry | Nominal delay | Range with ±20 % variation |
|:-:|--:|--:|
| 1 | 5 s | 4 – 6 s |
| 2 | 15 s | 12 – 18 s |
| 3 | 45 s | 36 – 54 s |
| 4 | 135 s | 108 – 162 s |
| 5 | 300 s | 240 – 360 s |

**Order**: the copy is scheduled and the original is completed **afterwards** (a duplicate is preferable to a loss; consumers
are idempotent — see research.md R1 and R9).

## 2. Message set aside to the DLQ

| Reason (`DeadLetterReason`) | When | Compensates with `*-failed` |
|---|---|---|
| `PoisonMessage` | Empty body, unreadable or `null` JSON, no `CorrelationId` or one longer than 64 characters, or deserialization error while processing (field with the wrong type) | Yes, if the `CorrelationId` is readable and the service has a failure event at that step |
| `RetriesExhausted` | A transient failure with `RetryAttempt = 5` | Yes, in the first steps (see §3) |
| `CompensationFailed` | The `*-failed` could not be emitted when compensating | No (it is the reason it is set aside) |

| Field | Content |
|---|---|
| `DeadLetterErrorDescription` | Readable detail, **maximum 1,024 characters** (truncated) |
| `CorrelationId` property | The `CorrelationId` read from the body, if it exists |
| `Attempts` property | Number of executions made (`RetryAttempt + 1`) |

A message set aside is **not redelivered** automatically. Reprocessing it is a manual operation (out of scope).

## 3. Failure events emitted when compensating

The same events and payloads that the permanent paths already emit. **No contract changes.**

| Service | Input subscription | Event emitted | Payload |
|---|---|---|---|
| Transaction | `transaction-initiated` | `transaction-failed` | `{ CorrelationId, CustomerId }` |
| Balance | `balance-initiated` | `balance-failed` | `{ CorrelationId, CustomerId }` |
| Transfer | `transfer-initiated` | `transfer-failed` | `{ CorrelationId, CustomerId }` |
| Notification | — | — | It has no failure event: it only logs and sets aside |
| Subscriptions that only update state | — | — | Justified deviation (finding 6): they only log and set aside |

`CustomerId` is omitted from the payload if it could not be read: consumers declare it `int` and an explicit `null` would make them reject the event as poison (found in the real test of US1).

**Emission rule (FR-016)**: the failure event is emitted **only if** the service does not already have a record of the `CorrelationId` in its
own database (Transaction: `Transaction`; Balance: `Balance`; Transfer: `TransferRequest`) or if it cannot query it.
With an existing record the message is set aside without emitting. Reason: `balance-failed` and `transfer-failed` cancel any
`Pending` saga in Transaction, and `transaction-failed` generates a failure email in Notification without checking state.

## 4. Logging contract

Each message set aside, rescheduled or failed produces an entry with these structured parameters (fixed names):

| Parameter | Description |
|---|---|
| `CorrelationId` | From the body; `null` and explicit if it could not be read |
| `MessageId` | Message identifier in the broker (always available) |
| `MessageType` | Subscription/event name (e.g. `transfer-initiated`) |
| `Attempt` | Current execution number (`RetryAttempt + 1`) |
| `Classification` | `Poison` · `Permanent` · `Transient` |
| `Reason` | Set-aside reason when it applies (`PoisonMessage`, `RetriesExhausted`, `CompensationFailed`) |

The templates in the last column are the runtime strings, which are still in Spanish in the code; the rest of the table is descriptive.

| Situation | Level | Template (summary) |
|---|---|---|
| Message set aside | `Error` | "Mensaje {MessageType} apartado a la DLQ ({Reason}); CorrelationId {CorrelationId}…" |
| Transient failure with a retry scheduled | `Warning` | "Mensaje {MessageType} falló (transitorio); reintento {Attempt} en {DelaySeconds}s; CorrelationId {CorrelationId}…" |
| Compensation skipped: a record of the `CorrelationId` already exists (FR-016) | `Warning` | "Compensación omitida para {MessageType}: ya hay registro; CorrelationId {CorrelationId}" with `Reason = "AlreadyRegistered"` |
| Compensation emitted | `Information` | "Evento de fallo {Event} emitido para {MessageType}; CorrelationId {CorrelationId}" |
| The record could not be queried when deciding to compensate | `Warning` | "No se pudo comprobar el registro de {CorrelationId}; se emite {Event}" + exception |
| Processor error (`ProcessError`) | `Error` | "Error del procesador {ErrorSource} en {EntityPath}" + exception |
| Processor error due to graceful shutdown | `Debug` | Same template; it is not treated as a failure (FR-014) |
| Message already ignored by `ProcessService` | `Warning` | Unchanged (existing template with `Reason`) |

## 5. What does not change

- Event names, `Subject`, payloads and `infra/local/Config.json` (subscriptions, rules, `MaxDeliveryCount`, `LockDuration`, TTL).
- The `IgnoredMessageReasonConstants` constants and the behavior of ignored messages inside `ProcessService`.
- The permanent data path (SQL 515/547/8115/8152): it compensates and completes, with no retry.
