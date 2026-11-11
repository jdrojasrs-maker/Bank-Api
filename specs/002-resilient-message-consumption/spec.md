# Feature Specification: Resilient message consumption (permanent and transient errors, and observability)

**Feature Branch**: `002-resilient-message-consumption` (branch not created; the work continues on `feat/retry-and-poison-messages`)

**Created**: 2026-11-07

**Status**: Draft

**Input**: User description: "The Service Bus consumers must distinguish permanent errors from transient ones. Poison messages go to the DLQ on the first attempt with a reason and detail. Transient failures are retried with a growing delay instead of using up the 10 attempts in seconds. Processor errors are logged with the CorrelationId. See HALLAZGOS-PENDIENTES-Bank.md findings 1 and 4."

## Clarifications

### Session 2026-11-07

- Q: When a message is set aside to the DLQ (because it is poison or because its retries were exhausted) and its `CorrelationId` is known, must the service also emit the `*-failed` failure event to cancel the transfer, or is setting it aside enough? → A: Option B: it is set aside to the DLQ and the failure event is also emitted in both cases, provided the `CorrelationId` is readable; if it is not, it is only set aside.
- Owner's clarification: `ProcessError` (errors of the processor itself) only logs; it does not set aside, complete or compensate messages, because it does not receive the message. Setting aside to the DLQ happens in the message handler.
- Owner's clarification: failures are grouped in three classes: poison (set aside on the first attempt), permanent data error (keeps compensating with its failure event, FR-010) and transient (retry with a growing delay).
- Q: What rule must the service follow to emit the `*-failed` when compensating: always emit it, do not emit it if there is already a record of that `CorrelationId` in its own database, or emit it only when retries are exhausted and not for poison messages? → A: Option B: it is not emitted if the service already has a record of that `CorrelationId` (the step already had effect); if there is no record or the database cannot be queried, it is emitted. Reason: a spurious `*-failed` cancels a saga in flight (Transaction only protects the final states) or triggers a false failure email (Notification does not check state).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A message that is impossible to process is set aside immediately with its reason (Priority: P1)

A system operator needs a message that can never be processed (empty body, unreadable content, missing mandatory data) to stop occupying the consumer from the first attempt. Today that message is redelivered 10 times in seconds before being set aside without explanation; the operator does not know why it is in the dead-letter queue (DLQ) nor which transfer it concerns.

**Why this priority**: It is the simplest case and the one with the most diagnostic value: it eliminates useless retries, leaves the DLQ with explained messages and does not depend on any other story.

**Independent Test**: Send each consumer service a message with an empty body and another with unreadable content. Each one must end up in the DLQ after a single delivery, with a readable reason and detail, without altering data or emitting success events.

**Acceptance Scenarios**:

1. **Given** a message whose body is empty or not interpretable, **When** the consumer receives it, **Then** the message goes to the DLQ on the first delivery with reason "poison message" and a detail that says what was wrong, and it is not delivered again.
2. **Given** an interpretable message without `CorrelationId` or with mandatory data missing, **When** the consumer receives it, **Then** it is immediately set aside to the DLQ with reason and detail, and no business data is modified.
3. **Given** a poison message whose `CorrelationId` is readable, **When** it is set aside, **Then** the event record includes that `CorrelationId` and the service also emits the step's failure event (if it does not already have a record of that `CorrelationId`, FR-016), so that the transfer ends up canceled and not `Pending`.
4. **Given** a poison message whose `CorrelationId` is not readable, **When** it is set aside, **Then** it is only logged and set aside (there is no transfer to associate a failure event with).

---

### User Story 2 - A transient failure is retried with a growing delay (Priority: P1)

A customer starts a transfer just when a service's database restarts for a few seconds or minutes. The system must tolerate that interruption and complete the transfer by itself, without a valid message getting lost in the DLQ. Today the 10 deliveries are used up in less than 15 seconds.

**Why this priority**: It is the cause of valid transfers being lost on any brief outage. It shares priority with story 1 because both complete the distinction between permanent and transient errors.

**Independent Test**: Take a service's database offline for less than the total retry window, send a transfer and restore the database. The transfer ends `Completed` without intervention and the DLQ stays empty.

**Acceptance Scenarios**:

1. **Given** a transient failure while processing a valid message, **When** the consumer cannot complete it, **Then** the message is retried after a wait, and each later retry waits longer than the previous one.
2. **Given** a dependency interruption shorter than the total retry window, **When** the dependency is restored, **Then** the message is processed successfully and the transfer ends without manual intervention.
3. **Given** a retry scheduled while the service restarts, **When** the service starts again, **Then** the message has not been lost and continues its course.
4. **Given** the same message processed more than once because of the retries, **When** its effect has already been applied, **Then** the final result is the same as if it had been processed only once (without duplicating records or events).

---

### User Story 3 - When retries are exhausted, the transfer is compensated and the message is explained (Priority: P2)

When a dependency stays down beyond the retry window, the customer must not be left with a transfer in `Pending` forever nor the operator with a message set aside without explanation. The system must close the transfer as failed and leave a record of the reason.

**Why this priority**: It closes the last gap of Principle III of the constitution (every error path ends in its failure event). It depends on retries with delay existing (story 2).

**Independent Test**: Keep a dependency down longer than the retry window and send a transfer. The service emits its step's failure event, the saga ends `Canceled` and the original message stays in the DLQ with reason "retries exhausted".

**Acceptance Scenarios**:

1. **Given** a message with a transient failure that has already used up all its attempts, **When** it fails for the last time, **Then** the service emits the failure event corresponding to that step and the transfer ends canceled, not pending (if the service already has a record of that `CorrelationId`, FR-016, the message is set aside without emitting it).
2. **Given** the same case, **When** the message is set aside, **Then** the DLQ receives it with reason "retries exhausted", the detail of the last error and the number of attempts.
3. **Given** that sending the failure event itself is not possible at that moment, **When** the consumer detects it, **Then** the message is not silently discarded: it is logged and set aside with the reason explaining that it could not be compensated.

---

### User Story 4 - Processor errors are logged with their context (Priority: P2)

An operator investigates an incident: today the consumer's connection, lock or processing errors are lost because the error handler is empty. They need to see each error with its origin, the affected queue and, whenever possible, the transfer's `CorrelationId`.

**Why this priority**: It does not change the functional behavior, but without it none of the other stories can be verified or diagnosed in production.

**Independent Test**: Provoke a processing error and a connection error. Both appear in the log with origin, queue and, in the first one, `CorrelationId`.

**Acceptance Scenarios**:

1. **Given** an error in the processing of a message, **When** it occurs, **Then** the log contains the `CorrelationId`, the message type, the attempt number, whether the error is considered permanent or transient and the exception detail.
2. **Given** an error of the processor itself (connection, expired lock), **When** it occurs, **Then** the log contains the origin of the error, the affected queue and the exception.
3. **Given** a message without a readable `CorrelationId`, **When** it fails, **Then** the log uses the message identifier instead and makes it clear that the `CorrelationId` is missing.

---

### Edge Cases

- Message with a `null`, empty or broken-JSON body: today it causes a null-reference exception that is retried 10 times; it must be treated as poison.
- Message with an impossible value: a `CorrelationId` longer than 64 characters or a field with the wrong type is a poison message (DLQ on the first delivery); a well-formed value rejected by the database (e.g. a negative amount) is a permanent data failure (it compensates, it is not retried).
- Database data error (integrity violation): it is already permanent and compensates; the current behavior must be preserved.
- Uniqueness violation: it means "already processed" and is neither a permanent nor a transient failure; it must be neither retried nor set aside.
- Defective duplicate delivery (damaged body or exhausted retries) of a message whose original was already processed: the service already has a record of its `CorrelationId`, so it is set aside without emitting the failure event and the healthy transfer is neither canceled nor generates a failure email (FR-016).
- Database down when deciding whether to compensate: the record cannot be checked; the failure event is emitted (it is the case the compensation exists for).
- Service stopped while a retry waits: the message is not lost on restart. Delivery is at-least-once: if the process crashes right between scheduling the retry and completing the original there may be a duplicate, which idempotent consumers absorb.
- Service shutdown with a message in progress: it is not treated as an error of the message and does not use up unnecessary attempts.
- Transient failure that repeats more times than the attempts available: after the last attempt it is compensated (story 3).
- Dead-letter queue with accumulation: this change only guarantees that each message gets there with an explanation; alerting and reprocessing are out of scope.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Each service that consumes messages (Transaction, Balance, Transfer, Notification) MUST classify each message or processing failure into one of three classes (poison message, permanent data failure or transient failure) before deciding what to do with the message.
- **FR-002**: A message that cannot be interpreted, is empty or lacks mandatory data (including the `CorrelationId`) MUST be set aside to the DLQ on its first delivery, without retries and without modifying business data. If its `CorrelationId` is readable, the step's failure event (`*-failed`) MUST also be emitted to cancel the transfer; if it is not, it is only set aside. The emission is subject to the rule of FR-016.
- **FR-003**: Every message set aside MUST carry a stable reason (category) and a readable detail of the specific problem; the reasons MUST be a closed and stable set, the same in all services.
- **FR-004**: A transient failure of a valid message MUST be retried with a wait between attempts that grows with each retry, instead of being redelivered immediately.
- **FR-005**: The wait between retries MUST include a bounded random variation so that several messages do not retry at the same time.
- **FR-006**: Scheduled retries MUST survive a service restart: a message waiting is not lost because of a restart, nor does the restart by itself process it twice. At-least-once delivery is guaranteed; an occasional duplicate (crash between scheduling and completing) is absorbed by idempotent consumers.
- **FR-007**: The current attempt number MUST be known to the consumer and be available in the log and in the set-aside reason.
- **FR-008**: When the retries of a transient failure are exhausted, the service MUST emit the failure event (`*-failed`) corresponding to that step (subject to the rule of FR-016) and MUST set the original message aside to the DLQ with reason "retries exhausted", the detail of the last error and the number of attempts.
- **FR-009**: If on the last attempt the failure event cannot be emitted, the message MUST NOT be silently discarded: it must be logged and set aside with a reason indicating that it could not be compensated.
- **FR-010**: Permanent errors with an existing compensation path (integrity violations) MUST keep their current behavior: they emit their failure event immediately.
- **FR-011**: A uniqueness violation MUST keep being treated as "message already processed": the message is completed, not retried and not set aside.
- **FR-012**: The processor's error handler MUST log the origin of the error, the affected queue or subscription and the exception. That handler MUST NOT set aside, complete or compensate messages: those decisions are made only in the message handler.
- **FR-013**: Every message processing error MUST be logged with the `CorrelationId` (or, if missing, the message identifier), the message type, the attempt number and the permanent or transient classification.
- **FR-014**: A graceful service shutdown MUST NOT be counted as a failure of the message in progress.
- **FR-015**: The behavior of FR-001 to FR-014 and FR-016 MUST be covered by automated tests that fail before the change and pass after it (Principle V of the constitution), including tests of growing wait, immediate setting aside, compensation when attempts are exhausted and non-compensation of already recorded steps.
- **FR-016**: Before emitting a `*-failed` to compensate (because of a poison message or exhausted retries), the service MUST check in its own database whether a record of that `CorrelationId` already exists: if it exists, the step already had effect and the failure event MUST NOT be emitted (the message is set aside anyway). If there is no record, or the database cannot be queried, it MUST be emitted. The check applies only to the services with a failure event (Transaction, Balance, Transfer).

### Key Entities *(include if feature involves data)*

- **Poison message**: message that can never be processed successfully, whatever the number of retries; it is identified by unreadable or empty content, or with mandatory data missing or impossible.
- **Transient failure**: failure whose cause can disappear by itself (database restarting, intermittent network, timeout); the same message can be processed successfully later.
- **Permanent failure**: failure that retrying does not solve because the message's data is not valid for the database (integrity or constraint violation, text longer than its column, null value or overflow); it is compensated with the step's failure event. It is distinguished from the poison message, which does not even reach the database.
- **Retry policy**: maximum number of attempts and growing waits (with random variation) applicable to a transient failure.
- **Set-aside record**: reason, detail, number of attempts and `CorrelationId` associated with a message sent to the DLQ.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100 % of the poison messages tested (empty body, `null`, unreadable content, missing `CorrelationId`) reach the DLQ on the first delivery, with reason and detail, and none uses more than 1 attempt.
- **SC-002**: A dependency interruption of up to 2 minutes leaves no valid message in the DLQ: the transfer ends `Completed` without manual intervention.
- **SC-003**: Between the first transient failure and the final setting aside there is an accumulated window of at least 5 minutes (today less than 15 seconds), and each wait is longer than the previous one.
- **SC-004**: After retries are exhausted, or when a poison message with a readable `CorrelationId` is set aside, 100 % of the affected transfers end canceled by the failure event of the step that failed and do not remain in `Pending`, for the steps that already have a failure event defined and that had no previous record (FR-016, SC-008).
- **SC-005**: 100 % of the DLQ messages generated by this mechanism have reason, detail and number of attempts.
- **SC-006**: 100 % of the processing and processor errors in the tests appear in the log, and the processing ones include `CorrelationId` or message identifier.
- **SC-007**: The existing tests (31 of Transfer and 72 of Transaction) keep passing without modifying them, and continuous integration stays green.
- **SC-008**: In the tests of the scenario "defective duplicate delivery of an already recorded step", 0 failure events are emitted (no healthy transfer is canceled) and the message stays in the DLQ with its reason.

## Assumptions

- Retries with delay apply to the four services that consume messages (Transaction, Balance, Transfer and Notification) with the same behavior. Notification does not work end to end locally (Cosmos DB and SendGrid are missing), so its verification will be by tests and not end to end.
- The Gateway does not consume messages and is out of scope.
- The specific policy (maximum number of attempts, waits, random variation) is set in planning; the minimum required is that there are several retries, that each wait is longer than the previous one and that the total window is several minutes.
- "Missing mandatory data" (FR-002) means: no `CorrelationId`, or a field that does not deserialize to the expected type. Any other missing field takes its default value and is rejected, if applicable, by the database (permanent data failure, FR-010), not by the poison-message classifier.
- Transient failures are recognized by exclusion: every failure that is neither permanent nor a uniqueness one is transient (the same whitelist of permanent ones the code already uses).
- What already compensates (permanent errors of the first step of Balance, Transfer and Transaction) does not change behavior.
- Consumers of the failure events (verified in the code): Transaction consumes `balance-failed` and `transfer-failed` (it cancels if still `Pending`, ignores final states) and Notification consumes `transaction-failed` (it sends a failure email without checking state). That is why FR-016 avoids emitting them for already recorded steps. The race between the check and the emission (milliseconds) is accepted, like the duplicate-emission window already documented in Transaction.
- The handlers that only update state and have no failure event defined (finding 6) are out of scope: when retries are exhausted they are logged and set aside with a reason, but the saga is not compensated until that path exists.
- The idempotency of Balance and Notification (finding 5) is not part of this change. Retries with delay do not worsen their current situation, since today they are redelivered 10 times anyway; it is pointed out in the plan as a risk.
- Out of scope: the Outbox pattern, the reconciler of `Pending` transactions, the circuit breaker, DLQ length alerts and the DLQ reprocessing tool.
- The local emulator limits a message's lifetime to 1 hour, so the total retry window must be noticeably shorter.
- The working branch is `feat/retry-and-poison-messages` and integration into `develop` is done by pull request with CI green.
