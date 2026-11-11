# Quickstart (Phase 1): validate idempotent processing

Guide to check that the feature meets the spec. It contains no implementation code.
Data details: [data-model.md](data-model.md). Event contract: [contracts/events.md](contracts/events.md).

## Prerequisites
- Docker running (the automated tests start their own SQL Server in a container; the image
  `mcr.microsoft.com/mssql/server:2022-latest` is already downloaded).
- The `infra/local/` local environment up only for manual validation: `docker compose up -d`, `.\apply-migrations.ps1`,
  `curl.exe http://localhost:5300/health` ⇒ 200.
- .NET 10 SDK.

## 1. Automated validation (mandatory; Principle V)

From the repository root:

```powershell
dotnet test tests/Bank.Transfer.Api.Tests
dotnet test tests/Bank.Transaction.Api.Tests
```

Expected result: all tests green. Each test starts a clean database by applying the real migrations.

| Spec scenario | Test that covers it | Expected result |
|---|---|---|
| Story 1, sc. 1 and 4 | Same `transfer-initiated` delivered 2 to 10 times serially | 1 row in `Transfer`, 1 result emitted, original record intact |
| Story 1, sc. 2 | Same message delivered in parallel | 1 row, 1 result |
| Story 1, sc. 3 | Rejected message (provoked `CHECK` violation) and then repeated | 1 `transfer-failed`, 0 rows in `Transfer`, no second failure |
| Story 2, sc. 1 to 3 | For `Completed` and `Canceled`: repeat `transaction-initiated`, `balance-confirmed`, `balance-failed`, `transfer-failed`, `transfer-confirmed` | State unchanged and 0 events emitted |
| Story 2, sc. 4 | Valid transitions from `Pending` | Moves to `Completed` / `Canceled` with their events |
| Story 3 | Sender failure after recording; redelivery | 1 row and the result arrives |
| Edge cases | Message without `CorrelationId`; same id with a different amount | No data changes; `InvalidMessage` / `ContentMismatch` record |

## 2. Manual end-to-end validation (recommended)

1. Apply the new migrations: `.\infra\local\apply-migrations.ps1`.
   - If it fails while creating a unique index because of previous duplicates, check first (see section 4).
2. Start the services with the `http` profile (Gateway, Transaction, Balance, Transfer), as in the README.
3. **Happy path (SC-005)**: `POST http://localhost:5204/api-gateway` with a transfer; expected: `Completed` / `Completed` / `Complete`.
4. **Duplicate**: publish again to the topic the same `transfer-initiated` (same `CorrelationId`) with a console tool
   (see the support task in `tasks.md`; it is not part of the service).
5. Check with SQL (connection to each database, as in the VS Code guide):

```sql
-- BankTransfer: must return 0 rows (none duplicated)
SELECT CorrelationId, COUNT(*) AS n FROM Transfer GROUP BY CorrelationId HAVING COUNT(*) > 1;

-- BankTransfer: one row per request, with EmittedAt filled in
SELECT CorrelationId, Outcome, ClaimedAt, EmittedAt FROM TransferRequest ORDER BY Id DESC;

-- BankTransaction: after a late duplicate, the state must still be final
SELECT CorrelationId, CurrentState FROM [Transaction] ORDER BY Id DESC;
```

6. In the Transfer/Transaction logs, look for the records `Reason = DuplicateAlreadyEmitted` / `FinalStateProtected` with the `CorrelationId`.
7. Check with `peek` (helper project) that there is no second `transfer-confirmed` in the subscription.

## 3. Acceptance criteria

| Criterion | How it is checked |
|---|---|
| SC-001 | Tests of repeated deliveries serially and in parallel: always 1 row |
| SC-002 | Same: 1 result emitted |
| SC-003 | Final state × message type combinations: 0 changes and 0 events |
| SC-004 | Interruption test: 1 row and the result arrives at least once |
| SC-005 | Manual happy path and automated regression test |
| SC-006 | Records with `CorrelationId` and `Reason` |
| SC-007 | `dotnet test` green in both projects |

## 4. If something fails
- **Error while creating the unique index**: there are repeated `CorrelationId`s. Detect them with
  `SELECT CorrelationId, COUNT(*) FROM Transfer GROUP BY CorrelationId HAVING COUNT(*) > 1;` (and the same with `[Transaction]`).
  Locally, the simplest is `docker compose down` and running `apply-migrations.ps1` again (the databases are disposable).
- **The tests do not start**: check that Docker responds (`docker version`).
- **The emulator lost its entities after recreating SQL**: `docker compose restart emulator`.
