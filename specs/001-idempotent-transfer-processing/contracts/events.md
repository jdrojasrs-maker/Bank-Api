# Event contract (Phase 1): idempotent behavior

The interface this feature exposes to other services is the **events of the `bank-transfer` topic**. This feature **does not change
any event name or any payload field** (compatibility required by the constitution and by `CLAUDE.md`):
there is no need to touch `infra/local/Config.json` or the README events table. What changes is the **behavior** when
repeated or late messages are received, described here.

Common rules:
- The event name goes in `ServiceBusMessage.Subject` and matches the subscription (kebab-case).
- `CorrelationId` is the idempotency key and travels in all events.
- At-least-once delivery: any message can arrive more than once.
- An ignored message **is completed** (not retried) and leaves a structured record with `CorrelationId`, `MessageType` and `Reason`.

## Consumed events with new behavior

### `transfer-initiated` → consumed by Transfer

| Aspect | Contract |
|---|---|
| Sender | Transaction (after `balance-confirmed`) |
| Payload (unchanged) | `{ "CorrelationId": string, "Amount": decimal, "SourceAccount": string, "DestinationAccount": string, "CustomerId": int }` |
| Precondition | Non-empty `CorrelationId` (≤ 64 characters) and an interpretable body |
| Effect the first time | Records a transfer and emits **exactly one** of `transfer-confirmed` / `transfer-failed` |
| Effect on repetitions | None if the result was already emitted or is being emitted; it does not modify the original record; it does not emit again |
| Recovery | If the record was made and the result was never emitted, the redelivery emits it without creating another transfer |
| Different content, same id | The first one prevails; the later one is ignored and `ContentMismatch` is recorded |
| Invalid | No effect; `InvalidMessage` is recorded and the message is completed |

### Events consumed by Transaction

| Event | Current state `Pending` | State `Completed` / `Canceled` | No row |
|---|---|---|---|
| `transaction-initiated` | Creates the row (first time) or repeats `balance-initiated` | Ignored, no events | Creates the row |
| `balance-confirmed` | Emits `transfer-initiated` | Ignored, no events | Ignored |
| `balance-failed` | → `Canceled`, emits `transaction-failed` | Ignored, no events | Ignored |
| `transfer-failed` | → `Canceled`, emits `transaction-failed` and `transfer-failed-balance` | Ignored, no events | Ignored |
| `transfer-confirmed` | → `Completed`, emits `transaction-completed` and `transfer-confirmed-balance` | Ignored, no events | Ignored |

A final state **never** changes: neither to `Pending` nor to the other final state.

## Emitted events (payloads unchanged)

| Event | Sender | Payload | New guarantee |
|---|---|---|---|
| `transfer-confirmed` | Transfer | `{ CorrelationId, CustomerId }` | At most once per `CorrelationId` on repetitions without interruption |
| `transfer-failed` | Transfer | `{ CorrelationId, CustomerId }` | Same; a repeated rejection does not emit a second failure |
| `balance-initiated` | Transaction | `{ CorrelationId, CustomerId }` | May be repeated only while the transaction is still `Pending` (recovery) |
| `transfer-initiated` | Transaction | `{ CorrelationId, Amount, SourceAccount, DestinationAccount, CustomerId }` | Never emitted from a final state |
| `transaction-failed` | Transaction | `{ CorrelationId, Amount, CustomerId }` | Never emitted from a final state |
| `transaction-completed` | Transaction | `{ CorrelationId, Amount, CustomerId }` | Never emitted from a final state |
| `transfer-failed-balance` | Transaction | `{ CorrelationId, Amount, CustomerId }` | Never emitted from a final state |
| `transfer-confirmed-balance` | Transaction | `{ CorrelationId, Amount, CustomerId }` | Never emitted from a final state |

## Compatibility
- **Non-breaking** change: same events, same fields, same subscription names.
- Consumers that already receive events more than once (Balance with *upsert*, Notification) need no changes for this feature.
- The idempotency of Balance and Notification is out of scope (see the spec).
