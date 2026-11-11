<!--
SYNC IMPACT REPORT (review draft; remove before versioning the file)
Version change: 1.2.0 → 1.2.1 (PATCH: the whole document is translated from Spanish to English; no rule changes)
Modified principles: none (the five principles are unchanged; only their wording language changes)
Modified sections: all, translation only. Normative keywords map as: DEBE → MUST, NO DEBE → MUST NOT,
  DEBERÍA → SHOULD, PUEDE → MAY.
Added sections: none
Removed sections: none
Templates requiring updates: none modified here (the templates read the constitution at run time).
Follow-up TODOs:
  - In GitHub, branch protection on main and develop must also require "Tests (Balance)" and "Tests (Notification)"
    (web UI setting; they can only be chosen after they have run once). Confirmed done by the repository owner.
  - Known gaps against the principles (recorded debt, not fixed here):
    * III: closed for the first steps (Balance, Transfer and Transaction also compensate when retries are exhausted
      and on poison messages with a readable CorrelationId). Still open: the handlers that only update state, which
      are logged and set aside to the DLQ without compensation (no failure event is defined).
    * II: Balance and Notification are not idempotent yet (Transfer and Transaction are).
-->
# Bank Constitution

## Core Principles

### I. Services decoupled by events only
The services (Gateway, Transaction, Balance, Transfer, Notification) MUST communicate exclusively
through events published on Azure Service Bus. There MUST NOT be any project reference, HTTP call or
shared access between services.

Rationale: decoupling lets a service that is down, slow or being redeployed leave the others
unaffected; messages wait in their subscription until the consumer returns.

### II. Idempotent handlers by CorrelationId
Every handler MUST be idempotent using the `CorrelationId` created in the Gateway, which MUST travel
in all the events of a transfer. Processing the same message twice MUST produce the same final state
and MUST NOT duplicate records or external effects (events, emails). A prior check by `CorrelationId`
is not enough under concurrency: uniqueness SHOULD also be guaranteed by a unique constraint in the
service's database.

Rationale: Service Bus delivers at least once; duplicates are normal behavior, not an exception.

### III. Every error path emits its failure event
Every error path MUST end in its failure or compensation event (`*-failed`) instead of getting lost.
A permanent error (retrying does not solve it) MUST compensate immediately. A transient error MAY be
retried first, but if the retries are exhausted it MUST still emit the failure event. There MUST NOT
be a path in which the saga stays in `Pending` with no way out, and the absence of an exception or an
`Id != 0` MUST NOT be used as the only success criterion.

Rationale: without explicit compensation, sagas get stuck and the customer never learns that the
transfer was not completed.

### IV. Each service owns its database
Each service MUST have its own database and be the only one that reads and writes it. No table,
connection string or schema MUST be shared between services. Another service's data is obtained only
through its events.

Rationale: exclusive ownership of the data is what makes the decoupling of Principle I possible, and
lets each schema evolve independently.

### V. Every fix comes with an automated test (NON-NEGOTIABLE)
Every defect fix and every behavior change MUST include an automated test that fails before the change
and passes after it. The case that originated the defect MUST remain permanently covered. A fix whose
only verification is manual MUST NOT be accepted.

Rationale: a fix without a test can regress without anyone noticing; the manual fault-injection tests
already demonstrated the defects and must now be automated.

## Technical constraints

- Platform: .NET 10 (`net10.0`), nullable and implicit usings enabled. Solution `Bank.slnx`.
- Messaging: a single topic (`SERVICEBUSTOPIC`). The event name MUST go in
  `ServiceBusMessage.Subject` and match the subscription name, in kebab-case. The names MUST live in
  `Domain/Constants` of each service and match across services.
- Event contract: JSON payload. Senders use `System.Text.Json` and receivers `Newtonsoft.Json`
  (case-insensitive); field compatibility MUST be maintained.
- Structure per service: `Domain/`, `Application/`, `External/`, `Persistence/` and a `Program.cs`
  that only registers dependencies. The `ProcessService` classes are *scoped*; the senders, *singleton*.
- Secrets: sensitive configuration MUST arrive through user-secrets or environment variables. No
  secret MUST be written in `appsettings*.json`, in commits or in `.env.example`.
- Persistence: the schema MUST evolve with EF Core migrations applied by command (never when the
  service starts).

## Development workflow

- A change that adds or renames an event MUST update, in the same change, the sender, the receiver,
  `infra/local/Config.json` (subscription and rule) and the README events table.
- A model change MUST include its migration and be checked with `infra/local/apply-migrations.ps1`.
- The services run on the host with the `http` profile; the flow is verified end to end against the
  local Service Bus emulator and SQL Server (`infra/local/`).
- Areas of known technical debt are pointed out when a change touches them, but MUST NOT be fixed
  without an express request.
- Commits and pushes are made only when the repository owner explicitly says so.
- Every change MUST be integrated into `main` or `develop` through a pull request with continuous
  integration (`.github/workflows/ci.yml`: build of `Bank.slnx` and tests of Transfer, Transaction,
  Balance and Notification) green. The checks `Build`, `Tests (Transfer)`, `Tests (Transaction)`,
  `Tests (Balance)` and `Tests (Notification)` MUST be required in the repository's branch protection.
- A continuous integration check MUST NOT be disabled, skipped or relaxed to make a change pass; if a
  check fails, the cause is fixed.

## Governance

This constitution prevails over any other practice in the repository. `CLAUDE.md` is the ongoing
development guide and MUST NOT contradict it.

- Amendments: they are proposed in writing with their justification, recorded in the impact report
  and require the approval of the repository owner. An amendment that affects a principle MUST
  include the adaptation plan for the existing code or record the gap as technical debt.
- Versioning (semantic): MAJOR for removing or redefining a principle in an incompatible way;
  MINOR for adding a principle or section, or materially expanding guidance; PATCH for
  clarifications and wording corrections.
- Compliance review: every review of changes MUST verify the five principles and that continuous
  integration is green. Any exception MUST be justified in writing in the change itself and recorded
  as technical debt.

**Version**: 1.2.1 | **Ratified**: 2026-11-03 | **Last Amended**: 2026-11-10
