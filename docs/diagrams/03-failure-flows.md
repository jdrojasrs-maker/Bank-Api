# 3. Failure flows

There are two families of failure and they are handled differently:

- **Permanent business or data failures** (for example a CHECK constraint): the service resolves them inside `ProcessService` by emitting a `*-failed` event. There is no retry or DLQ.
- **Infrastructure or message failures** (unreadable message, database down, Service Bus down): `ResilientMessageProcessor` resolves them with retries, compensation and the dead-letter queue.

## 3.1 Saga compensation map

```mermaid
flowchart LR
    TXI["transaction-initiated"]
    BI["balance-initiated"]
    TI["transfer-initiated"]

    TXI -.->|permanent error or retries exhausted| TXF["transaction-failed"]
    BI -.->|permanent error or retries exhausted| BF["balance-failed"]
    TI -.->|permanent error or retries exhausted| TRF["transfer-failed"]

    BF -->|Transaction moves to Canceled| TXF
    TRF -->|Transaction moves to Canceled| TXF
    TRF -->|releases the balance| TFB["transfer-failed-balance"]

    TXF --> NT["Notification<br/>Transaction Failed email"]
    TFB --> BC["Balance moves to Canceled"]
```

Dotted lines: the service that receives the first step emits its own failure event. Solid lines: Transaction reacts and propagates.

## 3.2 Failure in Balance (permanent data error)

Example: a database constraint rejects the balance (SQL 515, 547, 8115 or 8152). It is not retried: it is compensated immediately.

```mermaid
sequenceDiagram
    autonumber
    participant SB as Service Bus
    participant TX as Transaction
    participant BL as Balance
    participant NT as Notification

    SB->>BL: balance-initiated
    BL->>BL: INSERT Balance fails with a permanent error
    BL->>SB: balance-failed
    BL->>SB: Complete the message, no retry or DLQ

    SB->>TX: balance-failed
    TX->>TX: reads the transaction, still Pending
    TX->>SB: transaction-failed
    TX->>TX: conditional UPDATE Pending to Canceled

    SB->>NT: transaction-failed
    NT->>NT: Transaction Failed email and stores in Cosmos
```

## 3.3 Failure in Transfer (rejected transfer)

Example from the tests: an amount greater than or equal to 20000 violates a CHECK (SQL 547). Transfer records the rejection so that a repetition does not emit a second failure.

```mermaid
sequenceDiagram
    autonumber
    participant SB as Service Bus
    participant TX as Transaction
    participant BL as Balance
    participant TF as Transfer
    participant NT as Notification

    SB->>TF: transfer-initiated
    TF->>TF: INSERT Transfer fails with SQL 547
    TF->>TF: ClearChanges and stores TransferRequest with Outcome Failed
    TF->>SB: transfer-failed
    TF->>TF: marks EmittedAt

    SB->>TX: transfer-failed
    TX->>SB: transaction-failed
    TX->>SB: transfer-failed-balance
    TX->>TX: conditional UPDATE Pending to Canceled

    par Notification
        SB->>NT: transaction-failed
        NT->>NT: Transaction Failed email
    and Balance release
        SB->>BL: transfer-failed-balance
        BL->>BL: UPDATE Balance state Canceled
    end
```

## 3.4 The consumer's decision on a message (`ResilientMessageProcessor`)

All the services use `AutoCompleteMessages = false`; the processor decides what to do with each message.

```mermaid
flowchart TD
    START(["A message arrives"]) --> CHK{"MessageClassifier.Check<br/>valid body?"}

    CHK -->|"empty, not JSON, not an object,<br/>no CorrelationId, more than 64 characters,<br/>field with the wrong type"| POISON["Poison message"]
    CHK -->|valid| PROC["ProcessService.Execute"]

    PROC -->|OK| COMPLETE(["Complete - end"])
    PROC -->|"JsonException while processing"| POISON
    PROC -->|"other exception"| SHUT{"graceful shutdown<br/>of the service?"}

    SHUT -->|yes| REDELIVER(["The exception is rethrown:<br/>the message is redelivered<br/>without counting as a failure"])
    SHUT -->|no| MAX{"RetryAttempt >= 5?"}

    MAX -->|no| SCHED["ScheduleRetry<br/>copy with RetryAttempt + 1<br/>delay 5 s x 3^n-1, cap 300 s, +-20 %"]
    SCHED --> COMPLETE2(["Complete the original - end"])

    MAX -->|yes| EXH["Retries exhausted<br/>Attempts = 6"]

    POISON --> COMP
    EXH --> COMP{"Compensate<br/>FailureCompensator"}

    COMP -->|"compensated or not applicable"| DLQ(["DeadLetter<br/>PoisonMessage or RetriesExhausted"])
    COMP -->|"the failure event could not be emitted"| DLQ2(["DeadLetter<br/>CompensationFailed"])
```

Key rules:

- The rescheduled copy is scheduled **before** completing the original. If something fails in between, there is a possible duplicate but never a loss.
- The Service Bus processor's `ProcessError` **only logs** (`ProcessorErrorHandler`): it does not set aside, complete or compensate.
- Each message set aside or rescheduled is logged with `CorrelationId`, `MessageId`, `MessageType`, `Attempt`, `Classification` and `Reason`.

## 3.5 Retry after a transient failure

Example: Transaction cannot reach its database while processing `transaction-initiated`.

```mermaid
sequenceDiagram
    autonumber
    participant SB as Service Bus
    participant RX as Receiver
    participant PS as ProcessService
    participant DB as SQL Server

    SB->>RX: delivery 1 without RetryAttempt
    RX->>PS: Execute
    PS->>DB: INSERT
    DB--xPS: timeout, transient error
    PS--xRX: exception
    RX->>SB: ScheduleMessage copy with RetryAttempt 1, delay approx. 5 s
    RX->>SB: Complete the original

    Note over SB,RX: Wait with jitter. Retries 2 to 5 wait approx. 15, 45, 135 and 300 s (cap).

    SB->>RX: delivery of the copy with RetryAttempt 1
    RX->>PS: Execute
    PS->>DB: INSERT
    DB-->>PS: OK
    RX->>SB: Complete
```

## 3.6 Retries exhausted and compensation

```mermaid
sequenceDiagram
    autonumber
    participant SB as Service Bus
    participant RX as Receiver
    participant FC as FailureCompensator
    participant DB as Own database
    participant DLQ as Dead-letter queue

    SB->>RX: copy with RetryAttempt 5
    RX->>RX: Execute fails again
    RX->>RX: RetryAttempt >= 5, the retries are exhausted
    RX->>FC: CompensateAsync

    alt message is not the service's first step
        FC-->>RX: false, does not compensate
    else the service already has a record of the CorrelationId
        FC->>DB: AnyAsync CorrelationId
        DB-->>FC: exists
        FC-->>RX: false, the saga has already progressed
    else no record, or the database does not respond
        FC->>SB: first step's failure event
        FC-->>RX: true
    end

    RX->>DLQ: DeadLetter RetriesExhausted, Attempts 6, last error max 1024 characters
```

The failure event according to the message that exhausted the retries:

| Message | Service | Compensation event |
|---|---|---|
| `transaction-initiated` | Transaction | `transaction-failed` |
| `balance-initiated` | Balance | `balance-failed` |
| `transfer-initiated` | Transfer | `transfer-failed` |
| any other | any | none (DLQ only) |
| any | Notification | none (`NullFailureCompensator`) |

The payload is `{CorrelationId, CustomerId}`. `CustomerId` is **omitted** if it could not be read, because an explicit `null` would make the consumer reject it as poison.

## 3.7 Duplicates and late messages

### Transfer: a single transfer and a single result

```mermaid
flowchart TD
    IN(["transfer-initiated"]) --> PARSE{"valid message?"}
    PARSE -->|no| IGN1(["Ignored: InvalidMessage<br/>Complete"])
    PARSE -->|yes| FIND{"does a TransferRequest exist<br/>for the CorrelationId?"}

    FIND -->|no| INS["INSERT Transfer + TransferRequest<br/>in a single operation"]
    INS -->|OK| EMIT
    INS -->|"uniqueness 2601 or 2627"| IGN2(["Ignored: DuplicateInProgress<br/>another delivery recorded first"])
    INS -->|"permanent error"| FAILREQ["Stores TransferRequest Failed"]
    FAILREQ --> EMIT["Emit result<br/>transfer-confirmed or transfer-failed"]

    FIND -->|yes| FP{"same SHA-256 fingerprint<br/>of the content?"}
    FP -->|no| IGN3(["Ignored: ContentMismatch<br/>the first one prevails"])
    FP -->|yes| EMITTED{"does EmittedAt<br/>have a value?"}
    EMITTED -->|yes| IGN4(["Ignored: DuplicateAlreadyEmitted"])
    EMITTED -->|no| LEASE{"claim expired?<br/>ClaimedAt more than 30 s"}
    LEASE -->|no| IGN5(["Ignored: DuplicateInProgress"])
    LEASE -->|"yes, atomic UPDATE wins"| REC["Emission recovered<br/>uses the stored result"]
    REC --> EMIT

    EMIT -->|send OK| MARK(["Marks EmittedAt"])
    EMIT -->|send fails| REL(["Releases the claim and rethrows:<br/>the redelivery emits immediately"])
```

### Transaction: final states protected

```mermaid
flowchart TD
    MSG(["balance-confirmed, balance-failed,<br/>transfer-confirmed or transfer-failed"]) --> FIND{"does the<br/>transaction exist?"}
    FIND -->|no| ORPHAN(["Ignored: NoSuchTransaction<br/>creates nothing"])
    FIND -->|yes| ST{"current state"}
    ST -->|"Completed or Canceled"| FINAL(["Ignored: FinalStateProtected<br/>does not change or emit"])
    ST -->|Pending| EM["Emits the step's events"]
    EM --> UPD["UPDATE ... WHERE CurrentState = Pending"]
    UPD -->|"1 row"| DONE(["Transition applied"])
    UPD -->|"0 rows: another process won"| FINAL
    EM -->|"the send fails"| KEEP(["Stays Pending:<br/>the redelivery retries"])
```

Only `transaction-initiated` can **create** the Transaction row. If it arrives repeated while it is `Pending`, it emits `balance-initiated` again to recover a send that failed earlier.
