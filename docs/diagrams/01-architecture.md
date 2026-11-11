# 1. Architecture

.NET 10 microservices with a **choreographed saga** on **Azure Service Bus** (a single topic `bank-transfer`, one subscription per event filtered by `Subject`). No service calls another over HTTP: they only communicate through events.

## 1.1 Components and infrastructure

```mermaid
flowchart LR
    Customer(["Customer"])

    subgraph Services[".NET 10 services - http profile"]
        GW["Gateway<br/>:5204<br/>POST /api-gateway"]
        TX["Transaction<br/>:5110<br/>saga orchestrator"]
        BL["Balance<br/>:5287"]
        TF["Transfer<br/>:5197"]
        NT["Notification<br/>:5267"]
    end

    subgraph Messaging["Azure Service Bus - local emulator :5672"]
        TOPIC{{"Topic bank-transfer<br/>11 subscriptions<br/>CorrelationFilter by Label = Subject"}}
    end

    subgraph Data["Persistence"]
        DBT[("SQL Server<br/>BankTransaction")]
        DBB[("SQL Server<br/>BankBalance")]
        DBF[("SQL Server<br/>BankTransfer")]
        COS[("Cosmos DB<br/>notifications")]
    end

    SG["SendGrid<br/>email"]

    Customer -->|HTTP| GW
    GW -->|publishes| TOPIC
    TX <-->|publishes and consumes| TOPIC
    BL <-->|publishes and consumes| TOPIC
    TF <-->|publishes and consumes| TOPIC
    TOPIC -->|consumes| NT

    TX --- DBT
    BL --- DBB
    TF --- DBF
    NT --- COS
    NT -->|HTTP API| SG
```

## 1.2 Event map (who publishes and who consumes)

Each arrow is an event that travels through the topic. The event name goes in `ServiceBusMessage.Subject` and matches the subscription name.

```mermaid
flowchart LR
    GW["Gateway"]
    TX["Transaction"]
    BL["Balance"]
    TF["Transfer"]
    NT["Notification"]

    GW -->|transaction-initiated| TX

    TX -->|balance-initiated| BL
    BL -->|balance-confirmed| TX
    BL -->|balance-failed| TX

    TX -->|transfer-initiated| TF
    TF -->|transfer-confirmed| TX
    TF -->|transfer-failed| TX

    TX -->|transaction-completed| NT
    TX -->|transaction-failed| NT

    TX -->|transfer-confirmed-balance| BL
    TX -->|transfer-failed-balance| BL
```

| Event (subscription) | Published by | Consumed by |
|---|---|---|
| `transaction-initiated` | Gateway | Transaction |
| `balance-initiated` | Transaction | Balance |
| `balance-confirmed` | Balance | Transaction |
| `balance-failed` | Balance | Transaction |
| `transfer-initiated` | Transaction | Transfer |
| `transfer-confirmed` | Transfer | Transaction |
| `transfer-failed` | Transfer | Transaction |
| `transaction-completed` | Transaction | Notification |
| `transaction-failed` | Transaction (and its own compensation) | Notification |
| `transfer-confirmed-balance` | Transaction | Balance |
| `transfer-failed-balance` | Transaction | Balance |

## 1.3 Internal structure of each service (layers)

It is the same structure in the five services. The code in `Application/Resilience/` is deliberately duplicated in four of them (the project's constitution forbids a shared project).

```mermaid
flowchart TB
    SB{{"Service Bus<br/>event subscription"}}

    subgraph External["External - adapters"]
        RX["ServiceBusReciveService<br/>BackgroundService<br/>AutoCompleteMessages = false"]
        ACT["ServiceBusMessageActions<br/>Complete, Schedule, DeadLetter"]
        TXS["ServiceBusSenderService<br/>singleton"]
    end

    subgraph App["Application"]
        RMP["ResilientMessageProcessor<br/>decides to complete, reschedule or set aside"]
        CLS["MessageClassifier<br/>detects poison messages"]
        RP["RetryPolicy<br/>5 s x 3^n-1, cap 300 s, +-20 %"]
        FC["FailureCompensator<br/>emits the first step's failure event"]
        MED["MediatR<br/>Publish ProcessEvents"]
        PH["ProcessHandler<br/>creates scope"]
        PS["ProcessService<br/>business logic, scoped"]
    end

    subgraph Per["Persistence"]
        DB["DatabaseService<br/>EF Core DbContext or CosmosClient"]
    end

    BD[("Database")]

    SB --> RX --> RMP
    RMP --> CLS
    RMP --> RP
    RMP --> FC
    RMP --> ACT
    RMP -->|valid body| MED --> PH --> PS
    PS --> DB --> BD
    PS --> TXS
    FC --> TXS
    FC --> DB
    TXS --> SB
    ACT --> SB
```

## 1.4 Observations to review (found while reading the code)

- **Balance has no idempotency of its own**: `ProcessDataBase` looks up by `CorrelationId` and, if it does not exist, inserts; it does not catch uniqueness violations nor protect final states. Transaction and Transfer do.
- **Notification does not compensate** (`NullFailureCompensator`) and is not idempotent: a duplicate `transaction-completed` would send the email twice. In addition, the email recipient is fixed in the code.
- **Double-emission window**: without the Outbox pattern, an event can be emitted more than once if the process crashes right between sending and confirming (documented in `specs/001`). That is why all consumers must be idempotent.
- `ResilientMessageProcessorTests` in the four test projects only covers the happy case; the file's comment describes it as "andamiaje" (scaffolding).
