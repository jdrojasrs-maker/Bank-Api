# Quickstart: validate resilient consumption

Validation guide. The design details are in [research.md](research.md), [data-model.md](data-model.md) and
[contracts/message-handling.md](contracts/message-handling.md).

## Prerequisites

- Docker running (SQL Server + Service Bus emulator): `cd infra/local; docker compose up -d; .\apply-migrations.ps1`.
- Emulator ready: `curl.exe http://localhost:5300/health` returns 200.
- User-secrets of the five services configured (see `CLAUDE.md`).
- Services on the host with the `http` profile: `dotnet run --project src/Bank.<Service>/Bank.<Service>.Api --launch-profile http`.

## A. Automated tests (no services running)

```powershell
dotnet test tests/Bank.Transfer.Api.Tests
dotnet test tests/Bank.Transaction.Api.Tests
dotnet test tests/Bank.Balance.Api.Tests
dotnet test tests/Bank.Notification.Api.Tests
```

**Expected**: all green. The 103 existing tests (31 + 72) keep passing **without modifying them** (SC-007).
The Transfer, Transaction and Balance tests require Docker (Testcontainers; in Balance, because of the compensator ones); the Notification ones do not.

Working order (Principle V): each new test is first run in **red** and it is verified that it fails for the right reason.

## B. Poison message (story 1, SC-001)

With the five services running, send invalid messages to the topic with the `Subject` of each service (use `infra/local/duplicate-test`
in `--raw` mode, which sends an arbitrary body, or the emulator console):

| Body | Subscription | Expected |
|---|---|---|
| (empty) | `transfer-initiated` | DLQ on the 1st delivery, reason `PoisonMessage` |
| `null` | `balance-initiated` | DLQ on the 1st delivery, reason `PoisonMessage` |
| `{ no es json` | `transaction-initiated` | DLQ on the 1st delivery, reason `PoisonMessage` |
| `{"Amount": 10}` (no `CorrelationId`) | `transfer-initiated` | DLQ; no `*-failed` |
| `{"CorrelationId":"<id>","CustomerId":"abc"}` (field with the wrong type) | `balance-initiated` | `balance-failed` emitted **and** DLQ on the 1st delivery (deserialization error ⇒ poison, no retries) |

**Check the safety rule (FR-016, SC-008)**: after completing a normal transfer, republish to `balance-initiated` a
message with the same `CorrelationId` but `"CustomerId":"abc"`. **Expected:** the message goes to the DLQ (`PoisonMessage`) and
`balance-failed` is **not** emitted; the transaction stays `Completed` and no failure email arrives.

**Check**: the message's `DeliveryCount` in the DLQ = 1; the service log shows the reason and the `CorrelationId`; the
transfer with a readable `CorrelationId` ends up `Canceled` in Transaction.

## C. Transient failure that recovers (story 2, SC-002, SC-003)

1. Take `BankTransfer` offline:
   `ALTER DATABASE BankTransfer SET OFFLINE WITH ROLLBACK IMMEDIATE;`
2. Send a transfer through the Gateway (`POST http://localhost:5204/api-gateway`).
3. Watch the Transfer log: retries 1, 2, 3… with **growing** delays (~5 s, ~15 s, ~45 s).
4. **Restart during the wait (FR-006)**: after the 2nd retry, stop Transfer with Ctrl+C and start it again with the `http` profile. The scheduled retry is **not lost**: it arrives at its time at the same subscription and the new service processes it.
5. After ~90 s, `ALTER DATABASE BankTransfer SET ONLINE;`.

**Expected**: the transfer ends `Completed` without intervention; the DLQ of `transfer-initiated` stays **empty**; the log
shows the sequence of attempts with `CorrelationId`. (Today the same scenario sends the message to the DLQ in ~15 s.)

## D. Retries exhausted (story 3, SC-004, SC-005)

1. `ALTER DATABASE BankTransfer SET OFFLINE WITH ROLLBACK IMMEDIATE;` and send a transfer.
2. Wait more than ~9 minutes (nominal window ≈ 8.3 min).

**Expected**: Transfer emits `transfer-failed`; Transaction ends `Canceled`; the original message is in the DLQ of
`transfer-initiated` with reason `RetriesExhausted`, `Attempts = 6` and the last error in the detail. Put the database back online.

> This test is long; it is recommended to run it once at the end. The logic is covered by the automated tests of section A.
> Shortcut: send a valid `transfer-initiated` for a `Pending` transaction with `infra/local/duplicate-test --raw transfer-initiated <body> 5` (`RetryAttempt = 5`) while the database is offline.

## E. `ProcessError` and graceful shutdown (story 4, FR-014)

- With a service running, restart the emulator container (`docker compose restart emulator`): the service must **log**
  the processor error (origin, entity) instead of losing it.
- Stop a service with Ctrl+C while it is processing: it must not appear as a failure of the message nor use a retry.

## F. Continuous integration

Push the branch and check in the *Actions* tab that `Build`, `Tests (Transfer)`, `Tests (Transaction)`,
`Tests (Balance)` and `Tests (Notification)` run. The last two become required after adjusting the branch protection and amending
the constitution (manual actions; see [plan.md](plan.md) → *Complexity Tracking*).

## Exit criteria

- Sections A, B and C verified; D and E verified at least once.
- `dotnet list package --vulnerable --include-transitive` stays clean.
- The study documents (`GUIA-ESTUDIO-Hallazgos-Bank.md`, `HALLAZGOS-PENDIENTES-Bank.md`, kept outside the repository) updated with what was resolved.
