# Research: Resilient message consumption

Sources: reading the code on the `feat/retry-and-poison-messages` branch, `infra/local/Config.json` and a **disposable proof of
concept** against the local emulator (console program outside the repository). Labels: **[TEST]** checked by running,
**[CODE]** read in the repository, **[DOC]** knowledge of the SDK/service not verified in this session, **[VERIFY]** to be
confirmed when implementing.

No `NEEDS CLARIFICATION` markers were left in the spec; the following decisions resolve the points it delegated to the plan.

---

## R1. How to wait between retries

**Decision**: on a transient failure, the consumer **schedules a copy of the message** on the same topic with
`ScheduleMessageAsync` (growing future time) and **then completes the original**. The retry number travels in the application property
`RetryAttempt`.

**Evidence**:
- **[TEST]** A message was scheduled 8 s in the future: it reached the subscription at **8.7 s**, with `Subject` and `RetryAttempt`
  intact and `DeliveryCount = 1`. The emulator supports scheduled messages.
- **[CODE]** `Config.json`: each subscription has a correlation rule by `Label` = event name, and there is one subscription
  per event. The copy carries the same `Subject`, so it only reaches **the subscription that originated it**.
- **[CODE]** Today `Process` only calls `CompleteMessageAsync` after the `Publish`; if the exception goes up, the SDK abandons the message and
  the redelivery is immediate. That is why the 10 deliveries were used up in ~15 s (finding 1).

**Order of operations**: schedule the copy **first**, complete the original **afterwards**.
- If scheduling fails (the bus does not respond): the original is abandoned and the broker redelivers it (normal); nothing is lost.
- If completion fails after scheduling: the original plus the copy remain ⇒ a duplicate, which idempotent consumers
  absorb. A duplicate is preferred to a loss.

**Rejected alternatives**:
| Alternative | Why not |
|---|---|
| In-process wait (`Task.Delay`/Polly) with the message locked | Holds the lock and the concurrency; the retry does not survive a service restart (fails FR-006); adds a dependency |
| EF Core `EnableRetryOnFailure` | Only covers SQL, in milliseconds or a few seconds, and not the bus; [VERIFY] its interaction with `ExecuteUpdateAsync` |
| Raising `MaxDeliveryCount` or `LockDuration` | Adds no wait; it only postpones the problem (finding 1 expressly rules it out) |
| The client's `ServiceBusRetryOptions` | Applies to client operations with the broker, not to the business handler |
| Circuit breaker (pause the processor) | Out of scope (spec, Assumptions); complementary, not a substitute |
| Abandon with delay | Service Bus does not offer "abandon with delay" |

---

## R2. Retry policy

**Decision**: **5 retries** (6 executions in total). Delay of retry *n* (1 to 5):

`delay(n) = min(5 s × 3^(n−1), 300 s) × u`, with `u` uniform in [0.8 ; 1.2]

| Retry | Nominal delay | Nominal accumulated |
|:-:|--:|--:|
| 1 | 5 s | 5 s |
| 2 | 15 s | 20 s |
| 3 | 45 s | 65 s |
| 4 | 135 s | 200 s |
| 5 | 300 s (cap) | 500 s ≈ 8.3 min |

**Reasoning**:
- It tolerates an interruption of **more than 3 minutes** (the 4th retry happens at ~3 min 20 s) ⇒ it meets SC-002 (2 min) with room to spare; the
  total window (~8 min) meets SC-003 and is far below the emulator's 1 h TTL.
- Factor 3 and a 5 min cap: it grows fast at first (brief failures) without leaving a message waiting indefinitely.
- The ±20 % random variation prevents messages that failed at the same time from retrying at the same time (FR-005).
- The policy is a **pure function** (`RetryPolicy.NextDelay(attempt, random)`), so it is tested without a broker or clock.

**[VERIFY]** The values will be exposed as constants (`RetryConstants`) and adjusted if the manual verification (quickstart)
shows it is too long or too short.

---

## R3. How a failure is classified

**Decision**: three classes, evaluated in this order.

| Class | How it is detected | What is done |
|---|---|---|
| **Poison** | Validation before MediatR: empty or blank body, unreadable JSON, JSON `null` or non-object, `CorrelationId` missing/blank, or longer than 64 characters. **Also** that the body does not deserialize to the service's entity (e.g. a field with the wrong type, `"CustomerId":"abc"`; `CustomerId` is an integer in the four entities), and, as a second line of defense, a `Newtonsoft.Json.JsonException` thrown while processing | DLQ on the first delivery (`PoisonMessage`) + `*-failed` if the `CorrelationId` is readable (R4) |
| **Permanent data error** | Inside `ProcessService`, the existing whitelist: SQL 515, 547, 8115, 8152 (`DatabaseErrors.IsPermanent`) | **Unchanged**: the service emits its `*-failed` and the message is completed (FR-010) |
| **Transient** | Everything else that comes out as an exception from `ProcessService`, except `JsonException` (timeout, deadlock, dropped connection, bus error). Uniqueness violations are already treated as "already processed" inside the service | Retry with delay (R1, R2) |

**Evidence and nuances**:
- **[CODE]** The whitelist already exists and the rest "propagates" by design. Classification by exclusion is what the code uses.
- **[CODE]** Clarification with respect to the draft in the conversation: **8152 is text truncation, not corrupt JSON**, and **547
  is a foreign-key or CHECK violation, not a missing `CorrelationId`**. Corrupt JSON does not reach SQL: it is detected in the prior validation.
- **A SQL timeout does not guarantee "nothing was saved"**: the write may be committed and the response fail. That is why
  retries require idempotency (Transfer and Transaction already have it; see R9).

**Why the validation includes deserializing to the service's entity**: **[CODE]** `ParseTransaction` and `ParseTransfer`
catch `JsonException` and treat the message as "ignored" (it is completed). If the classifier only looked at the shape of the JSON, a
field with the wrong type would still be silently completed in Transfer and Transaction, instead of going to the DLQ. That is why the
`MessageClassifier` (one per service) attempts the same deserialization that `ProcessService` does and, if it fails, the message is poison.

**Why the validation goes before MediatR and not inside `ProcessService`**: **[CODE]** today `ParseTransaction` (Transaction) and
`ParseTransfer` (Transfer) already treat an invalid message as "ignored": they log it and **complete** it; the 103 existing tests
pin that behavior. Moving it to an exception would break those tests (SC-007). With the prior validation, a poison message
never reaches `ProcessService`, and the internal protection remains as a second line of defense. In Balance and Notification
(`JsonConvert.DeserializeObject` unchecked; today they throw `NullReferenceException` and use up the 10 deliveries) the prior validation
is the only control.

---

## R4. Compensation (poison and exhausted retries)

**Decision** (clarification of 2026-11-07, option B): if the `CorrelationId` is readable, the **first step's** `*-failed` is emitted and
the message is also set aside; if it is not readable, it is only set aside.

**Safety rule (clarification of 2026-11-07, option B, FR-016)**: before emitting, the compensator queries **the service's own
database**; if a record of that `CorrelationId` already exists, the step had effect and **it is not emitted** (the message is set aside
anyway). If there is no record, or the database cannot be queried, **it is emitted**.

**Why it is necessary** (verified in the code, **[CODE]**):

| Failure event | Consumed by | On receiving it | Effect of a spurious `*-failed` |
|---|---|---|---|
| `balance-failed` | Transaction | `Pending` ⇒ emits `transaction-failed` and moves to `Canceled`; final state ⇒ ignores it | Cancels a saga **in flight** (the real `transfer-confirmed` that arrives afterwards is ignored: money moved and transaction canceled) |
| `transfer-failed` | Transaction | Same, and also emits `transfer-failed-balance` | Same |
| `transaction-failed` | Notification | Sends the "Your transaction has failed" email and stores it; it **does not check state** | A false failure email for a completed transfer |

Transaction only protects the *final* states; a `Pending` saga in flight is not protected. The previous version of this plan,
with a compensator without access to the database, did not tell these cases apart.

**What is queried** (only for the first-step subscription; for any other, `false` is returned without querying):

| Service | Record queried | If it exists |
|---|---|---|
| Transaction | `Transaction` row with that `CorrelationId` (any state) | `transaction-failed` is not emitted |
| Balance | `Balance` row with that `CorrelationId` | `balance-failed` is not emitted |
| Transfer | `TransferRequest` row with that `CorrelationId` (it exists both for confirmed and for rejected ones) | `transfer-failed` is not emitted: there is already a result and the redelivery recovers its emission |

**Probability versus severity**: a damaged body in a duplicate of a good message is almost impossible (Service Bus delivers
identical duplicates). The most plausible case is a duplicate that exhausts its retries while another copy already had effect. It is rare, but
the consequence (canceling an already executed transfer) is serious in a banking system, and the check is cheap.

**Accepted race**: milliseconds pass between the query and the emission; a record created right in that interval would not be seen. It is the
same kind of window already documented in Transaction (emit before confirming) and it is accepted.

**Database down**: it is the main case of exhausted retries. If the query fails, it is emitted: with no readable record, the most
likely thing is that the step did not happen, and it is exactly the situation the compensation wants to close. A `Warning` is logged.

One `FailureCompensator` class per service (with `IFailureCompensator.CompensateAsync(body, messageType, ct)`) that depends on
`IServiceBusSenderService`, the logger and the service's database **through `IServiceScopeFactory`** (the compensator is
*singleton*; `IDatabaseService` is *scoped*):

| Service | Input subscription | Failure event | Payload (same as the existing one) |
|---|---|---|---|
| Transaction | `transaction-initiated` | `transaction-failed` | `{ CorrelationId, CustomerId }` |
| Balance | `balance-initiated` | `balance-failed` | `{ CorrelationId, CustomerId }` |
| Transfer | `transfer-initiated` | `transfer-failed` | `{ CorrelationId, CustomerId }` |
| Notification | any | — (no failure event) | — |
| Remaining subscriptions (they only update state) | — | — (justified deviation, finding 6) | — |

**[CODE]** The payloads match those the permanent paths already emit in each `ProcessService`.
`CustomerId` is read tolerantly: if it is missing or not an integer, it is omitted from the payload (an explicit `null` would be rejected by the consumers, which read it as `int`).

**Why a separate class and not a method of `IProcessService`**: `ProcessService` stays intact (SC-007) and the compensation has
a single responsibility. Price of the safety rule: **the compensator's tests need a real SQL Server**, also in
Balance, whose test project starts using Testcontainers (Docker) like those of Transfer and Transaction. Notification has no
compensator or relational database and still needs no Docker. The other components (processor, policy, classifier) keep being
tested without a database.

**If the `*-failed` itself cannot be sent** (FR-009): the exception is **not swallowed**. The message is set aside with reason
`CompensationFailed` if the broker allows it; if the broker does not respond, the completion also fails, the lock expires and the broker
redelivers (nothing is lost silently).

**Safety against late processing**: if a message set aside is reprocessed later (manual DLQ reprocessing) after having
emitted its `*-failed`, Transaction already protects its final states (`Canceled` does not change), so there are no contradictory states.
This covers manual reprocessing; the case of a concurrent duplicate is covered by the safety rule above.

---

## R5. What `ProcessError` does

**Decision**: it only **logs** (`Error` level): `ErrorSource`, `EntityPath`, `FullyQualifiedNamespace` and the exception. It does not set aside,
complete or compensate (owner's clarification, FR-012).

**Evidence**:
- **[DOC]** `ProcessErrorEventArgs` does not contain the message; it only describes the processor's failure (connection, lost lock, or an
  exception thrown by the handler, with `ErrorSource = ProcessMessageCallback`). There is nothing to set aside.
- **[DOC]** If the message handler throws, the SDK abandons the message on its own and notifies `ProcessError`. That is why the
  decision about the message is made **inside** the handler (`ResilientMessageProcessor`), which catches the exceptions and never
  lets them propagate except cancellation.
- A graceful shutdown (FR-014): if `args.CancellationToken` is canceled, no retry is scheduled and it is not counted as a failure; the
  message is abandoned and redelivered after the restart. In `ProcessError`, an `OperationCanceledException` due to shutdown is
  logged at a low level, not as an error.

---

## R6. Logging with `CorrelationId`

**Decision**: structured parameters in the templates (not just scopes), with fixed names: `CorrelationId`, `MessageId`,
`MessageType`, `Attempt`, `Classification` (`Poison` | `Permanent` | `Transient`) and `Reason`.

**Reason**: **[CODE]** the tests' `CapturingLogger` already collects structured properties from the template (as `IgnoreMessage` does with
`Reason`), and `BeginScope` returns `null`: a scope would not be observable in the tests. If the `CorrelationId` is missing, it is logged with
`MessageId` and an explicit `CorrelationId = null` (FR-013).

---

## R7. Dead-letter with reason and detail

**Decision**: `DeadLetterMessageAsync(message, propertiesToModify, reason, detail)` with:
- Fixed reasons: `PoisonMessage`, `RetriesExhausted`, `CompensationFailed`.
- Readable detail, **truncated to 1,024 characters** (the broker limits the size; a failure when setting aside is avoided).
- `CorrelationId` and `Attempts` properties to be able to query the DLQ without opening the body.

**Evidence**: **[TEST]** `DeadLetterMessageAsync(msg, "PoisonMessage", "detalle de prueba")` in the emulator: the DLQ returned
`DeadLetterReason = PoisonMessage` and `DeadLetterErrorDescription = detalle de prueba`. **[VERIFY]** that the emulator also keeps the
modified properties; if not, the `CorrelationId` will go only in the detail and the log.

---

## R8. How it is tested without a broker

**Decision**: separate decision and execution.
- `ResilientMessageProcessor` receives the message, the subscription, the delivery number/`RetryAttempt` and an `IMessageActions`
  (`Complete`, `DeadLetter(reason, detail, properties)`, `ScheduleRetry(attempt, delay)`). It does not know the SDK.
- `ServiceBusMessageActions` (in `External`) implements `IMessageActions` over `ProcessMessageEventArgs`.
- The tests use a `FakeMessageActions` that records what was requested, an `IProcessService`/delegate that throws whatever is wanted, a
  `Random` with a fixed seed and `FakeServiceBusSender` (it already exists in Transfer and Transaction).

**Coverage** (all fail before the change, Principle V):
| Spec scenario | Test |
|---|---|
| Story 1 | Empty body, `null`, broken JSON, no `CorrelationId`, long `CorrelationId` ⇒ `DeadLetter("PoisonMessage")` on the 1st delivery, no `Publish`; with a readable `CorrelationId` **and no previous record**, the `*-failed` is emitted (with a record: not emitted, FR-016) |
| Story 2 | Transient exception ⇒ `ScheduleRetry` with attempt +1 and a delay within the range; growing delays between retries; the original is completed; with cancellation it is not rescheduled |
| Story 3 | Attempt 5 and exception ⇒ `*-failed` + `DeadLetter("RetriesExhausted")`; if sending the `*-failed` fails, it is not swallowed and it is set aside as `CompensationFailed` |
| Story 4 | The log contains `CorrelationId`, `MessageType`, `Attempt`, `Classification`; `ProcessError` logs origin and entity |
| FR-010/FR-011 | A permanent data error and a uniqueness violation do **not** go through the retry path (the existing tests cover it with real SQL) |
| Policy | `RetryPolicy` is increasing, bounded by the cap and respects the variation range |

Balance and Notification: new projects. **Notification without a database** (it has no compensator). **Balance with SQL Server in a
container** (Testcontainers) only for the compensator tests (FR-016); the rest of its tests do not use the database. New
FR-016 tests in Transaction, Balance and Transfer: with an existing record in each state (`Pending`, `Completed`, `Canceled`
for Transaction) it is not emitted; without a record it is emitted; with the database unreachable it is emitted.

---

## R9. Idempotency and the risk of retries

- **[CODE]** Transfer (`TransferRequest` table, unique index) and Transaction (conditional transitions from `Pending`)
  converge on duplicates: a retry after an ambiguous timeout is safe.
- **[CODE]** Balance: `ProcessDataBase` looks up by `CorrelationId` and updates; it is idempotent in sequence, not under
  concurrency (no unique index). The retries are sequential, so they do not worsen the situation.
- **[CODE]** Notification: it sends the email and then stores; a failure after sending duplicates the email on the retry. **Today it already
  happens up to 10 times in seconds**; with a delay it would be at most 6, more spread out. It is not fixed here (finding 5).
- Conclusion: the change **does not worsen** any case and it is pointed out as a risk in the spec and in the plan.

---

## R10. Receiver and lifecycle

- **[CODE]** The four receivers are almost identical (`ServiceBusClient` + one processor per subscription + MediatR). That
  shape is kept; only `Process` (delegating to the resilience processor) and `ProcessError` change.
- `AutoCompleteMessages = false`: each path of the processor must settle the message explicitly (complete, set aside or
  reschedule+complete). If a path forgot to settle it, it would be detected in tests instead of being hidden by the SDK.
- The retry sender is a **single** `ServiceBusSender`, created from the receiver's own `ServiceBusClient` and reused
  (not one per message). The per-message sender leak of `ServiceBusSenderService` is known debt and is **pointed out, not fixed**.
- **[CODE]** `Process` today publishes through MediatR and completes; `IMediator.Publish(ProcessEvents)` is kept as the single entry point
  to the service, passed to the processor as a delegate.

---

## R11. Environment and CI

- **`Config.json`**: no changes. `MaxDeliveryCount = 10`, `LockDuration = PT1M` and TTL `PT1H` are kept as a safety net
  (for example, against a process crash in the middle of the handler, where the broker's delivery does count).
- **CI**: the test matrix of `ci.yml` goes from `[Transfer, Transaction]` to `[Transfer, Transaction, Balance, Notification]`. The new
  checks are called `Tests (Balance)` and `Tests (Notification)`.
- The `Tests (Balance)` job starts using Docker (Testcontainers) on the runner, like those of Transfer and Transaction; `Tests (Notification)` does not.
- **Two manual actions** (they are not repository files): adjust the branch protection in GitHub to require the two new
  checks and amend the constitution (1.2.0), which names the three current checks. Until that is done, the new jobs run but do not block.
