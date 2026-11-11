# 4. Test projects

One test project per service (xUnit). None references more than one service. Transfer, Transaction and Balance use a **real SQL Server in a container (Testcontainers)** with the real migrations, so they require Docker to be running. Notification does not use a database.

Common test doubles: `FakeMessageActions` (records Complete, Schedule and DeadLetter), `FakeFailureCompensator`, `CapturingLogger` and `PoisonCases` (sample poison bodies).

## 4.1 Bank.Transfer.Api.Tests

The most complete one: it covers resilience **and** the idempotency of `transfer-initiated`.

```mermaid
flowchart LR
    subgraph Infra["Infrastructure"]
        FX["SqlServerFixture<br/>Testcontainers + real migrations"]
        HN["TransferHarness"]
        FS["FakeServiceBusSender"]
        FA["FakeMessageActions"]
        FF["FakeFailureCompensator"]
        CL["CapturingLogger"]
        PC["PoisonCases"]
    end

    subgraph SUT["Bank.Transfer.Api - code under test"]
        PS["ProcessService"]
        RMP["ResilientMessageProcessor"]
        CLS["MessageClassifier"]
        RP["RetryPolicy"]
        FC["FailureCompensator"]
        PEH["ProcessorErrorHandler"]
    end

    subgraph TI["TransferInitiated - idempotency"]
        T1["DuplicateDeliveryTests<br/>same message several times:<br/>one transfer, one result"]
        T2["ConcurrentDeliveryTests<br/>copies in parallel: only one wins"]
        T3["RejectedTransferTests<br/>CHECK 547 rejection: a single transfer-failed"]
        T4["EmissionRecoveryTests<br/>send fails: releases the claim and the redelivery emits"]
        T5["LeaseTests<br/>expired lease recovers, active one ignores"]
        T6["InvalidAndMismatchedMessageTests<br/>invalid creates nothing, another fingerprint keeps the first"]
        T7["SchemaTests<br/>unique index and nvarchar 64"]
    end

    subgraph RS["Resilience"]
        R1["MessageClassifierTests"]
        R2["PoisonMessageTests"]
        R3["TransientRetryTests"]
        R4["ExhaustedRetriesTests"]
        R5["RetryPolicyTests"]
        R6["FailureCompensatorTests<br/>rule FR-016 with real SQL"]
        R7["PermanentDataErrorTests<br/>547 emits failure and completes"]
        R8["ProcessorErrorHandlerTests<br/>ProcessorLoggingTests<br/>ResilientMessageProcessorTests"]
    end

    FX --> HN
    HN --> PS
    FS -.-> PS
    FA -.-> RMP
    FF -.-> RMP
    CL -.-> RMP

    TI --> HN
    T1 & T2 & T3 & T4 & T5 & T6 & T7 --> PS

    R1 --> CLS
    R2 & R3 & R4 --> RMP
    R5 --> RP
    R6 --> FC
    R7 --> PS
    R8 --> PEH
    R8 --> RMP
```

## 4.2 Bank.Transaction.Api.Tests

It covers resilience and the saga's **state protection** (`StateProtection`).

```mermaid
flowchart LR
    subgraph Infra["Infrastructure"]
        FX["SqlServerFixture<br/>Testcontainers + real migrations"]
        HN["TransactionHarness"]
        FS["FakeServiceBusSender"]
        FA["FakeMessageActions"]
        FF["FakeFailureCompensator"]
        CL["CapturingLogger"]
        PC["PoisonCases"]
    end

    subgraph SUT["Bank.Transaction.Api - code under test"]
        PS["ProcessService<br/>saga state machine"]
        RMP["ResilientMessageProcessor"]
        CLS["MessageClassifier"]
        RP["RetryPolicy"]
        FC["FailureCompensator"]
        PEH["ProcessorErrorHandler"]
    end

    subgraph SP["StateProtection"]
        S1["ValidTransitionTests<br/>starts Pending, balance-confirmed,<br/>balance-failed, transfer-failed,<br/>transfer-confirmed"]
        S2["FinalStateProtectionTests<br/>Completed and Canceled never change<br/>or emit, a trace of the ignored one is left"]
        S3["OrphanAndInvalidMessageTests<br/>no transaction or invalid: creates nothing"]
        S4["ConcurrencyTests<br/>copies in parallel: one row,<br/>a single final state"]
        S5["EmissionFailureTests<br/>send fails: stays Pending<br/>and the redelivery completes"]
        S6["SchemaTests<br/>unique index and nvarchar 64"]
    end

    subgraph RS["Resilience"]
        R1["MessageClassifierTests"]
        R2["PoisonMessageTests"]
        R3["TransientRetryTests"]
        R4["ExhaustedRetriesTests"]
        R5["RetryPolicyTests"]
        R6["FailureCompensatorTests<br/>with a record in Pending, Completed<br/>or Canceled it does not emit"]
        R7["ProcessorErrorHandlerTests<br/>ProcessorLoggingTests<br/>ResilientMessageProcessorTests"]
    end

    FX --> HN
    HN --> PS
    FS -.-> PS
    FA -.-> RMP
    FF -.-> RMP
    CL -.-> RMP

    S1 & S2 & S3 & S4 & S5 & S6 --> HN
    S1 & S2 & S3 & S4 & S5 & S6 --> PS

    R1 --> CLS
    R2 & R3 & R4 --> RMP
    R5 --> RP
    R6 --> FC
    R7 --> PEH
    R7 --> RMP
```

## 4.3 Bank.Balance.Api.Tests

It only covers resilience. **There are no idempotency or state-protection tests**, because Balance does not implement that behavior yet.

```mermaid
flowchart LR
    subgraph Infra["Infrastructure"]
        FX["SqlServerFixture<br/>Testcontainers + real migrations"]
        HN["BalanceHarness"]
        FS["FakeServiceBusSender"]
        FA["FakeMessageActions"]
        FF["FakeFailureCompensator"]
        CL["CapturingLogger"]
        PC["PoisonCases"]
    end

    subgraph SUT["Bank.Balance.Api - code under test"]
        PS["ProcessService"]
        RMP["ResilientMessageProcessor"]
        CLS["MessageClassifier"]
        RP["RetryPolicy"]
        FC["FailureCompensator"]
        PEH["ProcessorErrorHandler"]
    end

    subgraph RS["Resilience"]
        R1["MessageClassifierTests"]
        R2["PoisonMessageTests"]
        R3["TransientRetryTests"]
        R4["ExhaustedRetriesTests"]
        R5["RetryPolicyTests"]
        R6["FailureCompensatorTests<br/>only emits balance-failed if there is no record"]
        R7["PermanentDataErrorTests<br/>CHECK 547 emits balance-failed and completes<br/>without retry or DLQ"]
        R8["ProcessorErrorHandlerTests<br/>ProcessorLoggingTests<br/>ResilientMessageProcessorTests"]
    end

    FX --> HN
    HN --> PS
    FS -.-> PS
    FA -.-> RMP
    FF -.-> RMP
    CL -.-> RMP

    R1 --> CLS
    R2 & R3 & R4 --> RMP
    R5 --> RP
    R6 --> FC
    R7 --> HN
    R7 --> PS
    R8 --> PEH
    R8 --> RMP
```

## 4.4 Bank.Notification.Api.Tests

No database, no Docker and no `FakeServiceBusSender`: Notification emits no events and has no real compensator.

```mermaid
flowchart LR
    subgraph Infra["Infrastructure - no Docker"]
        FA["FakeMessageActions"]
        FF["FakeFailureCompensator"]
        CL["CapturingLogger"]
        PC["PoisonCases"]
    end

    subgraph SUT["Bank.Notification.Api - code under test"]
        RMP["ResilientMessageProcessor"]
        CLS["MessageClassifier"]
        RP["RetryPolicy"]
        NFC["NullFailureCompensator<br/>always returns false"]
        PEH["ProcessorErrorHandler"]
    end

    subgraph RS["Resilience"]
        R1["MessageClassifierTests"]
        R2["PoisonMessageTests"]
        R3["TransientRetryTests"]
        R4["ExhaustedRetriesTests"]
        R5["RetryPolicyTests"]
        R6["NullFailureCompensatorTests<br/>does nothing"]
        R7["ProcessorErrorHandlerTests<br/>ProcessorLoggingTests<br/>ResilientMessageProcessorTests"]
    end

    FA -.-> RMP
    FF -.-> RMP
    CL -.-> RMP

    R1 --> CLS
    R2 & R3 & R4 --> RMP
    R5 --> RP
    R6 --> NFC
    R7 --> PEH
    R7 --> RMP
```

## 4.5 What each project covers (summary)

| Area | Transfer | Transaction | Balance | Notification |
|---|:---:|:---:|:---:|:---:|
| Poison message classification | yes | yes | yes | yes |
| Retries with delay and jitter | yes | yes | yes | yes |
| Exhausted retries and DLQ | yes | yes | yes | yes |
| Structured logging and processor errors | yes | yes | yes | yes |
| Compensation (`FailureCompensator`) | yes | yes | yes | not applicable |
| Permanent data error | yes | no | yes | not applicable |
| Duplicate idempotency and lease | yes | no | no | no |
| Final-state protection | not applicable | yes | no | no |
| Concurrency | yes | yes | no | no |
| Schema (unique index, nvarchar 64) | yes | yes | no | no |
| Requires Docker | yes | yes | yes | no |
