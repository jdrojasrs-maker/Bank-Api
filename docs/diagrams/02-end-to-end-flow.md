# 2. End-to-end flow (happy path)

A transfer from the moment it enters through the Gateway until the customer is notified. The `CorrelationId` is generated in the Gateway (`Guid.NewGuid()`) and travels in all events: it is the idempotency key in each database.

## 2.1 Sequence

```mermaid
sequenceDiagram
    autonumber
    actor C as Customer
    participant GW as Gateway
    participant SB as Service Bus
    participant TX as Transaction
    participant BL as Balance
    participant TF as Transfer
    participant NT as Notification
    participant SG as SendGrid

    C->>GW: POST /api-gateway customerId, amount, source, destination
    GW->>GW: CorrelationId = Guid.NewGuid()
    GW->>SB: transaction-initiated
    GW-->>C: 200 OK echo of the request

    Note over C,GW: The 200 only confirms that the event was published.<br/>The real result arrives later, by email.

    SB->>TX: transaction-initiated
    TX->>TX: INSERT Transaction state Pending
    TX->>SB: balance-initiated

    SB->>BL: balance-initiated
    BL->>BL: INSERT Balance state Pending
    BL->>SB: balance-confirmed

    SB->>TX: balance-confirmed
    TX->>TX: reads the Pending transaction
    TX->>SB: transfer-initiated with amount and accounts

    SB->>TF: transfer-initiated
    TF->>TF: INSERT Transfer Complete + TransferRequest Confirmed in a single operation
    TF->>SB: transfer-confirmed
    TF->>TF: marks EmittedAt

    SB->>TX: transfer-confirmed
    TX->>SB: transaction-completed
    TX->>SB: transfer-confirmed-balance
    TX->>TX: conditional UPDATE Pending to Completed

    par Notification
        SB->>NT: transaction-completed
        NT->>SG: email Transaction Completed
        NT->>NT: Cosmos stores the notification
    and Balance closing
        SB->>BL: transfer-confirmed-balance
        BL->>BL: UPDATE Balance state Completed
    end
```

## 2.2 States per service during the flow

```mermaid
stateDiagram-v2
    direction LR

    state "Transaction" as T {
        [*] --> T_Pending: transaction-initiated
        T_Pending --> T_Completed: transfer-confirmed
        T_Pending --> T_Canceled: balance-failed or transfer-failed
        T_Completed --> [*]
        T_Canceled --> [*]
        state "Pending" as T_Pending
        state "Completed - final" as T_Completed
        state "Canceled - final" as T_Canceled
    }

    state "Balance" as B {
        [*] --> B_Pending: balance-initiated
        B_Pending --> B_Completed: transfer-confirmed-balance
        B_Pending --> B_Canceled: transfer-failed-balance
        state "Pending" as B_Pending
        state "Completed" as B_Completed
        state "Canceled" as B_Canceled
    }

    state "Transfer" as F {
        [*] --> F_Complete: transfer-initiated
        state "Complete" as F_Complete
    }
```

> `Completed` and `Canceled` in Transaction are **final**: no duplicate or late message changes them (the transition is an `UPDATE ... WHERE CurrentState = 'Pending'`).

## 2.3 Data timeline

| Step | Service | Writes | Emits |
|---|---|---|---|
| 1 | Gateway | nothing (no database) | `transaction-initiated` |
| 2 | Transaction | `Transaction` = Pending | `balance-initiated` |
| 3 | Balance | `Balance` = Pending | `balance-confirmed` |
| 4 | Transaction | nothing (only reads) | `transfer-initiated` |
| 5 | Transfer | `Transfer` = Complete and `TransferRequest` = Confirmed | `transfer-confirmed` |
| 6 | Transaction | `Transaction` = Completed (after emitting) | `transaction-completed` and `transfer-confirmed-balance` |
| 7a | Notification | Cosmos: email-type notification | email through SendGrid |
| 7b | Balance | `Balance` = Completed | nothing |
