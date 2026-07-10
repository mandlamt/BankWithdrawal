using BankWithdrawal.Domain;

namespace BankWithdrawal.Application;

/// <summary>
/// Represents a single atomic unit of work spanning the balance mutation and the
/// outbox insert. Kept abstract from any specific ADO/EF technology so the
/// application layer stays portable and trivially testable with an in-memory fake.
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    IAccountRepository Accounts { get; }
    IOutboxRepository Outbox { get; }
    Task CommitAsync(CancellationToken ct);
}

public interface IUnitOfWorkFactory
{
    Task<IUnitOfWork> BeginAsync(CancellationToken ct);
}

public interface IAccountRepository
{
    /// <summary>
    /// Atomically debits the account in a single conditional statement
    /// (UPDATE ... WHERE id = @id AND balance >= @amount) so there is no
    /// read-then-write race between concurrent withdrawals on the same account.
    /// Returns the resulting balance, or null if the row didn't exist or the
    /// balance check failed (caller distinguishes the two via ExistsAsync only
    /// when it needs to - see WithdrawalService).
    /// </summary>
    Task<decimal?> TryDebitAsync(long accountId, decimal amount, CancellationToken ct);

    Task<bool> ExistsAsync(long accountId, CancellationToken ct);
}

public interface IOutboxRepository
{
    /// <summary>
    /// Persists the event in the same DB transaction as the balance change
    /// (transactional outbox pattern) and records the idempotency key so a
    /// retried request can be recognised before it touches the balance again.
    /// </summary>
    Task InsertAsync(WithdrawalEvent @event, string idempotencyKey, CancellationToken ct);

    Task<bool> IdempotencyKeyExistsAsync(long accountId, string idempotencyKey, CancellationToken ct);
}

/// <summary>
/// Publishes already-committed events to the outside world (SNS, etc). Implementations
/// live in Infrastructure and typically run from a background poller reading the
/// outbox table, decoupling "did we record the withdrawal" from "did the notification
/// go out" - a dual write across a DB and SNS in one HTTP request is not atomic and
/// would otherwise risk losing or duplicating events on partial failure.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(WithdrawalEvent @event, CancellationToken ct);
}
