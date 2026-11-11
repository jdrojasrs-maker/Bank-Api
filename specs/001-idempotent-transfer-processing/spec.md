# Feature Specification: Idempotent transfer processing

**Feature Branch**: `001-idempotent-transfer-processing` (branch not created; the work continues on `wb-feat-s301001`)

**Created**: 2026-11-03

**Status**: Draft

**Input**: User description: "When a transfer-initiated message arrives more than once with the same CorrelationId, the system must record a single transfer and emit the result only once. A transaction in a final state (Completed or Canceled) must never go back to Pending because of a duplicate or late message."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A repeated start message does not duplicate the transfer (Priority: P1)

A customer starts a transfer and the system, because of normal messaging redeliveries, receives the same transfer start message (`transfer-initiated`) more than once with the same `CorrelationId`, even simultaneously. The customer expects their money to move only once: there must be a single transfer and a single result (confirmed or rejected) for that operation.

**Why this priority**: it is the most serious business risk. A duplicated transfer amounts to charging the customer twice. It is also the behavior that is not guaranteed today.

**Independent Test**: it can be fully tested by delivering the same start message several times (serially and in parallel) and checking that there is a single transfer and that a single result was emitted. It delivers value on its own: it eliminates the double charge.

**Acceptance Scenarios**:

1. **Given** a transfer already recorded for a `CorrelationId`, **When** the same start message arrives again, **Then** there is still a single transfer for that `CorrelationId` and no second result is emitted.
2. **Given** no previous record for a `CorrelationId`, **When** two or more copies of the same start message arrive at the same time, **Then** a single transfer is recorded and a single result is emitted.
3. **Given** a start message whose first processing was rejected and emitted the failure result, **When** a copy of the same message arrives, **Then** no second failure result is emitted and no transfer is recorded.
4. **Given** a transfer already recorded and confirmed, **When** a copy of the start message arrives, **Then** the state and data of the original transfer do not change.

---

### User Story 2 - A finished transaction never goes back to Pending (Priority: P1)

A transaction that has already finished (`Completed` or `Canceled`) must not go back to `Pending` or change state because a duplicate or late message arrives (for example, a repeated transaction start or a balance confirmation that arrives after the operation has already closed). The customer and the operations teams must be able to trust that a final state is definitive.

**Why this priority**: without this guarantee a finished operation can be reopened and trigger downstream steps again (a new balance check, new transfers), with the risk of moving money twice and of contradictory states.

**Independent Test**: it is tested by taking a transaction to `Completed` and to `Canceled`, then delivering each type of message that used to leave it in `Pending` (duplicate and late) and checking that the state does not change and no new events are emitted.

**Acceptance Scenarios**:

1. **Given** a transaction in `Completed`, **When** a duplicate transaction start message arrives, **Then** the transaction stays in `Completed` and no new event is emitted towards other services.
2. **Given** a transaction in `Canceled`, **When** a late balance confirmation arrives, **Then** the transaction stays in `Canceled` and no transfer is started.
3. **Given** a transaction in `Completed`, **When** a late failure message that would cancel it arrives, **Then** the transaction stays in `Completed` and the message is recorded as ignored.
4. **Given** a transaction in `Pending`, **When** the message that completes or cancels it arrives, **Then** it moves to the corresponding final state (valid transitions keep working).

---

### User Story 3 - Recovery after an interrupted processing (Priority: P2)

If the first processing of a transfer is interrupted after recording it but before emitting its result, the redelivery of the same message must complete the pending work: emit the result without creating a second transfer. That way the operation is neither left hanging nor duplicated.

**Why this priority**: interruptions are common in distributed environments; without this recovery, the solution of stories 1 and 2 could leave operations recorded but without a result.

**Independent Test**: an interruption is simulated after the record and before the result is emitted, the message is redelivered, and it is checked that there is a single transfer and that the result arrived.

**Acceptance Scenarios**:

1. **Given** a recorded transfer whose result was never emitted, **When** the start message is redelivered, **Then** the result is emitted and there is still a single transfer.
2. **Given** a recorded transfer whose result was already emitted, **When** the start message is redelivered, **Then** it is not emitted again.

---

### Edge Cases

- **Simultaneous deliveries of the same message**: two copies processed at the same time cannot record two transfers; one wins and the other is treated as a duplicate.
- **Same `CorrelationId` with different content** (for example, another amount): the first recorded content prevails; the later one is ignored and a record is left.
- **Late message that contradicts the final state** (for example, a failure after `Completed`): it is ignored, never changes the final state and is recorded.
- **Out-of-order message** (a confirmation arrives earlier or later than expected): only valid transitions from the current state are applied.
- **Message without `CorrelationId` or unreadable**: it cannot be associated with any operation; it must not create records and its rejection must be recorded.
- **Redelivery of a message whose first processing was rejected**: the transfer is still not created and the failure event is not repeated.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST keep at most one transfer per `CorrelationId`, no matter how many times, or how simultaneously, the transfer start message arrives.
- **FR-002**: When the start message arrives more than once without interruptions, the system MUST emit the transfer result (confirmation or failure) only once.
- **FR-003**: A duplicate start message for an already processed transfer MUST NOT modify the existing record nor emit a second result; the original result prevails.
- **FR-004**: If a transfer was recorded but its result was never emitted, the redelivery of the message MUST complete the emission of the result without creating a second transfer.
- **FR-005**: A transaction in a final state (`Completed` or `Canceled`) MUST NOT change state, in particular it MUST NOT go back to `Pending`, because of a duplicate or late message.
- **FR-006**: A duplicate or late message addressed to a transaction in a final state MUST NOT cause new events towards other services (for example, restarting the balance check or a transfer).
- **FR-007**: The system MUST leave a record of each duplicate or late message that is ignored, with the `CorrelationId`, the message type and the reason, so that it can be audited.
- **FR-008**: If a message arrives with an already recorded `CorrelationId` but with different content, the system MUST keep the original content, ignore the new one and leave a record.
- **FR-009**: The normal flow without duplicates MUST remain unchanged: a single message produces one transfer and its result, and the valid transitions from `Pending` to `Completed` or `Canceled` keep working.
- **FR-010**: A message without `CorrelationId` or unreadable MUST NOT create or modify records and MUST leave a record of its rejection.

### Key Entities *(include if feature involves data)*

- **Transfer**: movement of money requested by a customer. It is uniquely identified by its `CorrelationId`; it includes amount, source and destination accounts and customer. Only one can exist per `CorrelationId`.
- **Transaction**: end-to-end tracking of an operation. Its state can be `Pending`, `Completed` or `Canceled`; `Completed` and `Canceled` are final and definitive states.
- **Transfer start message (`transfer-initiated`)**: order to record a transfer. It can arrive several times for the same operation.
- **Transfer result**: notice that the transfer was confirmed or rejected. It must be observed only once per operation.
- **Ignored-message record**: auditable record of a duplicate, late or invalid message that had no effect (with `CorrelationId`, message type and reason).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In 100 % of the tests with between 2 and 10 deliveries of the same start message (serial and parallel), there is exactly one transfer for the `CorrelationId`.
- **SC-002**: In 100 % of the tests with repeated deliveries without interruption, consumers observe at most one result (confirmation or failure) per operation; zero duplicate results.
- **SC-003**: In 100 % of the tested combinations of final state (`Completed`, `Canceled`) by type of duplicate or late message, the transaction keeps its state and no new event is emitted; zero regressions to `Pending`.
- **SC-004**: In 100 % of the interruption-after-record tests, the redelivery leaves exactly one transfer and the result arrives at least once.
- **SC-005**: The path without duplicates keeps ending with the same final result (transaction and balance completed, transfer complete) in 100 % of the runs.
- **SC-006**: 100 % of the ignored duplicate, late or invalid messages can be located by their `CorrelationId` with their reason.
- **SC-007**: Each acceptance scenario in this document is covered by a repeatable automated test (per Principle V of the constitution).

## Assumptions

- **Scope**: the feature covers (a) recording transfers on repeated start messages and (b) protecting the final state of transactions. The idempotency of the balance service and of notifications is out of this scope.
- **Single-emission guarantee**: single emission of the result is guaranteed for repeated deliveries without interruption. In the window in which the system cannot know whether the result was emitted, priority is given to the result being delivered at least once; consumers must already be idempotent by `CorrelationId` (Principle II). Guaranteeing atomicity between record and emission is out of scope (it is a separate topic).
- **Different content with the same identifier**: the first recorded content is assumed to be the valid one; detecting or resolving fraud or sender errors is out of scope.
- **Immutable final states**: neither `Completed` nor `Canceled` is assumed to admit any later transition, not even to the other final state; a message that tries it is ignored and recorded.
- **"Late message"** means a message that arrives after the transaction reached a final state or that arrives outside the expected order of the flow.
- **Messaging retries**: at-least-once delivery is assumed, so duplicates are normal behavior and not an anomaly.
- **Minimum record**: the record of ignored messages is limited to minimal data (`CorrelationId`, message type, reason, date); no retention period is defined here.
- **Verification**: the local environment with a messaging emulator and local databases is enough to validate the scenarios; there is no automated test project yet and creating it is part of this feature (Principle V).
