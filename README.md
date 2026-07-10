# Bank Withdrawal — Redesign Notes

Ported from the original Java/Spring snippet to C# / ASP.NET Core 8, per the
exercise's "use a language you're comfortable with" allowance. Business
capability preserved exactly: given an account and an amount, debit the
balance if sufficient funds exist, and notify the outside world of the
outcome via a message broker (SNS).

## 1. What was actually wrong with the original code

Before describing the new design, worth naming the concrete defects it fixes,
because several are correctness bugs, not just style:

1. **Dead code / unreachable event publish.** The `return "Withdrawal
   successful"` inside the `if` block means the SNS publish block after the
   `if/else` can never execute. As written, no event is ever published on the
   success path that returns early. This is the most important bug: the
   snippet doesn't actually do what its own comment says it does.
2. **Check-then-act race condition.** `SELECT balance` followed later by
   `UPDATE balance = balance - ?` is two round trips with no locking between
   them. Two concurrent withdrawals on the same account can both read a
   balance that individually looks sufficient, then both succeed, overdrawing
   the account.
3. **Dual-write problem.** Even if the publish code were reachable, doing "DB
   commit" then "SNS publish" as two separate operations in one request means
   a crash, timeout, or SNS outage between the two silently drops the
   notification while the money has already moved — with no record that this
   happened.
4. **No idempotency.** A client that times out waiting for a response has no
   safe way to retry; retrying may double-debit the account.
5. **Everything in one class.** SQL, AWS SDK usage, HTTP concerns and the
   business rule are all inline in the controller — impossible to unit test
   the "sufficient funds" rule without a real database and a real SNS client.
6. **No observability or audit trail.** No logging at all; nothing records
   that a withdrawal was attempted, only the ambiguous plain-string HTTP
   response.
7. **Hard-coded configuration and manual construction of the SNS client**
   inside the controller constructor (region, topic ARN), and no dependency
   injection for it, making it untestable and non-portable across
   environments.
8. **String responses instead of proper status codes** ("Insufficient
   funds", "Withdrawal failed" are all `200 OK` bodies) — bad for API
   consumers and observability tooling that keys off HTTP status.

## 2. Approach — architecture overview

The single controller/class is split into four layers, each a separate
project so dependencies only point inward (Domain has no dependencies;
Application depends only on Domain; Infrastructure and Api depend on
Application):

```
BankWithdrawal.Api             ASP.NET Core controller, DTOs, DI wiring
BankWithdrawal.Application     Use case (WithdrawalService), pure C#, no I/O libraries
BankWithdrawal.Domain          Entities, events, result types
BankWithdrawal.Infrastructure  Dapper/Postgres persistence, SNS publisher, outbox dispatcher
```

**Core design decision: the transactional outbox pattern.** Instead of
debiting the balance and calling SNS in the same request, the request writes
*both* the balance change *and* a row describing the event into the *same*
Postgres transaction (`withdrawal_outbox` table). A separate background
service (`OutboxDispatcherHostedService`) polls that table for `PENDING` rows
and publishes them to SNS, marking each `PUBLISHED` on success or bumping a
retry counter on failure (moving to `DEAD_LETTER` after `MaxAttempts`).

This single decision is what drives most of the quality attributes called
out in the brief:

- **Correctness / consistency:** the balance mutation and "an event will be
  published" fact commit atomically. There's no window where money moved but
  no record of it exists, and no window where a publish could fire for a
  withdrawal that then gets rolled back.
- **Fault tolerance:** SNS being slow or down never blocks or fails the HTTP
  request; the withdrawal still completes, and delivery catches up when SNS
  recovers. Dispatcher failures are retried with a bounded attempt count and
  routed to a dead-letter status instead of being retried forever.
- **Auditability / data governance:** the outbox table is an append-only,
  timestamped, queryable record of every withdrawal attempt that reached the
  debit step, including delivery status — this is the audit trail the
  original code had none of.
- **Throughput:** the request path does one DB round trip (transaction with
  two statements) and returns; it never blocks on a network call to AWS.
- **Idempotency / data governance:** a required `Idempotency-Key` header is
  stored with a unique constraint on `(account_id, idempotency_key)`, so a
  client-side retry (e.g. after a dropped connection) is recognized and
  answered with the original outcome instead of double-debiting.

**Atomic balance check.** The read-then-write race is fixed by collapsing the
check and the mutation into one statement:
`UPDATE accounts SET balance = balance - @Amount WHERE id = @AccountId AND
balance >= @Amount RETURNING balance`. Under Postgres's row-level locking
this is safe under concurrent withdrawals on the same account without needing
application-level locking or a separate `SELECT ... FOR UPDATE`.

**Result type instead of a string.** `WithdrawalOutcome` (`Success`,
`InsufficientFunds`, `AccountNotFound`, `DuplicateRequest`) lets the
controller map each case to the correct HTTP status (`200`, `402`, `404`)
via an exhaustive `switch`, and lets any other caller of the service
pattern-match instead of string-comparing.

**Testability.** `WithdrawalService` depends only on `IUnitOfWorkFactory` /
`IAccountRepository` / `IOutboxRepository` — interfaces defined in the
Application project. It can be fully unit tested with in-memory fakes, no
database or AWS credentials required, even though the exercise says tests
are out of scope for this submission.

**Dependency management.** The SNS client is registered once via
`AddAWSService<IAmazonSimpleNotificationService>()` and injected, rather than
constructed inside a controller constructor — reused across requests
(SDK clients are meant to be long-lived and are thread-safe), swappable for a
mock, and configured centrally via `IOptions<SnsOptions>` instead of string
literals.

**Portability / interoperability.** `IEventPublisher` is an abstraction over
"publish this event somewhere" — swapping SNS for e.g. a Kafka or Azure
Service Bus publisher later touches only `Infrastructure`, not the use case.
The event payload is versioned (`WithdrawalEvent.SchemaVersion`) and
serialized as plain JSON with `System.Text.Json` for interoperability with
non-.NET consumers.

**Observability.** Structured JSON logging (`AddJsonConsole`) with
`AccountId`/`CorrelationId` log scopes set once in the controller, so every
log line for a request carries both fields as queryable structured fields
rather than being embedded in a formatted message. A `/health` endpoint
checks DB connectivity for load balancer / orchestrator use.

**Cost efficiency.** Fully async I/O end-to-end so a single instance can
handle many concurrent in-flight requests without a thread per request;
config-driven region/topic/connection string means no rebuild-and-redeploy
to move between environments.

## 3. Trade-offs / things intentionally left out

- **Postgres-specific SQL** (`RETURNING`, `FOR UPDATE SKIP LOCKED`,
  `jsonb`). The original snippet used generic `JdbcTemplate`; I picked one
  concrete database to keep the example concrete rather than abstracting
  over an unspecified one. Swapping to SQL Server is mechanical (`OUTPUT
  inserted.balance` instead of `RETURNING`, `NVARCHAR(MAX)` instead of
  `jsonb`) since all SQL is isolated behind the two repository classes.
- **No message batching in the dispatcher.** SNS `PublishBatch` would cut
  API call volume further; left as a follow-up since a single-topic,
  moderate-volume workload doesn't need it yet, and it adds partial-failure
  handling complexity.
- **Currency is a fixed `"USD"` string** rather than a full multi-currency
  ledger; the original snippet had no currency concept at all, so this is a
  minimal addition, not a full redesign of the money model.
- **No authentication/authorization** — explicitly out of scope per the
  brief ("security is not part of this exercise").
- **No tests included**, per the brief's explicit exclusion, though the
  layering was chosen specifically to make them cheap to add later.

## 4. Unclear / notable library usage

- **`Dapper` vs `Entity Framework Core`:** Dapper is a thin micro-ORM — it
  maps query results to objects but does not track changes or generate SQL.
  Chosen here because the withdrawal path is a single hand-written,
  performance-sensitive statement; EF's change tracking would add overhead
  this path doesn't need. A larger system would likely use EF Core for its
  general CRUD surface and Dapper selectively for hot paths like this one.
- **`NpgsqlDataSource`** (rather than `NpgsqlConnection` directly) is the
  modern Npgsql connection-pooling entry point recommended since Npgsql 7+;
  it's registered once as a singleton and handed out per-request via
  `OpenConnectionAsync`, which is what actually pools connections instead of
  opening a raw TCP connection per request.
- **`FOR UPDATE SKIP LOCKED`** in the outbox dispatcher is a Postgres
  locking clause that lets multiple dispatcher instances poll the same table
  concurrently (for horizontal scaling / HA) without two instances picking
  up and double-publishing the same row.
- **`AWSSDK.SimpleNotificationService`** is the official .NET SNS client,
  functionally equivalent to the Java `SnsClient` in the original snippet;
  registered via `AddAWSService<T>` from `AWSSDK.Extensions.NETCore.Setup`,
  which wires up region/credentials from `IConfiguration`/environment rather
  than a hard-coded `Region.YOUR_REGION`.
- **`MessageDeduplicationId` / `MessageGroupId`** on `PublishRequest` are
  only meaningful for a SNS **FIFO** topic; they're set defensively so the
  code degrades gracefully (no-ops) on a standard topic but gets
  deduplication for free if the topic is later switched to FIFO.
