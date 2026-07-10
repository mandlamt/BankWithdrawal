using System.Data;
using BankWithdrawal.Application;
using BankWithdrawal.Domain;
using Dapper;
using Npgsql;

namespace BankWithdrawal.Infrastructure.Persistence;

/// <summary>
/// NOTE on library choice: Dapper (a thin micro-ORM over ADO.NET, see remarks in the
/// README under "unclear library usage") is used instead of EF Core here. A withdrawal
/// is a narrow, hot, latency-sensitive write path with one hand-tuned statement - Dapper
/// keeps that SQL visible and avoids EF's change-tracking overhead for a case that doesn't
/// need it. A larger app would likely still use EF Core for its general CRUD surface and
/// Dapper only for hot paths like this one.
/// </summary>
public sealed class SqlUnitOfWorkFactory : IUnitOfWorkFactory
{
    private readonly NpgsqlDataSource _dataSource;

    public SqlUnitOfWorkFactory(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IUnitOfWork> BeginAsync(CancellationToken ct)
    {
        var connection = await _dataSource.OpenConnectionAsync(ct);
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        return new SqlUnitOfWork(connection, transaction);
    }
}

public sealed class SqlUnitOfWork : IUnitOfWork
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private bool _committed;

    public SqlUnitOfWork(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
        Accounts = new SqlAccountRepository(connection, transaction);
        Outbox = new SqlOutboxRepository(connection, transaction);
    }

    public IAccountRepository Accounts { get; }
    public IOutboxRepository Outbox { get; }

    public async Task CommitAsync(CancellationToken ct)
    {
        await _transaction.CommitAsync(ct);
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_committed)
        {
            await _transaction.RollbackAsync();
        }
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

public sealed class SqlAccountRepository : IAccountRepository
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    public SqlAccountRepository(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async Task<decimal?> TryDebitAsync(long accountId, decimal amount, CancellationToken ct)
    {
        // Single atomic statement: the WHERE clause re-checks the balance at write
        // time under the row lock the UPDATE takes, so two concurrent withdrawals on
        // the same account cannot both succeed and overdraw it (fixes the
        // check-then-act race in the original SELECT-then-UPDATE code).
        const string sql = """
            UPDATE accounts
               SET balance = balance - @Amount
             WHERE id = @AccountId
               AND balance >= @Amount
         RETURNING balance;
        """;

        var command = new CommandDefinition(
            sql,
            new { AccountId = accountId, Amount = amount },
            _transaction,
            cancellationToken: ct);

        return await _connection.ExecuteScalarAsync<decimal?>(command);
    }

    public async Task<bool> ExistsAsync(long accountId, CancellationToken ct)
    {
        const string sql = "SELECT EXISTS(SELECT 1 FROM accounts WHERE id = @AccountId);";
        var command = new CommandDefinition(sql, new { AccountId = accountId }, _transaction, cancellationToken: ct);
        return await _connection.ExecuteScalarAsync<bool>(command);
    }
}

public sealed class SqlOutboxRepository : IOutboxRepository
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    public SqlOutboxRepository(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async Task InsertAsync(WithdrawalEvent @event, string idempotencyKey, CancellationToken ct)
    {
        // payload stored as jsonb: queryable/auditable at rest, and gives us a
        // permanent, append-only record of every withdrawal attempt outcome -
        // this table doubles as the audit trail the original code had none of.
        const string sql = """
            INSERT INTO withdrawal_outbox
                (event_id, account_id, idempotency_key, payload, status, created_at_utc)
            VALUES
                (@EventId, @AccountId, @IdempotencyKey, @Payload::jsonb, 'PENDING', @OccurredAtUtc);
        """;

        var payload = System.Text.Json.JsonSerializer.Serialize(@event);

        var command = new CommandDefinition(
            sql,
            new
            {
                @event.EventId,
                @event.AccountId,
                IdempotencyKey = idempotencyKey,
                Payload = payload,
                @event.OccurredAtUtc
            },
            _transaction,
            cancellationToken: ct);

        await _connection.ExecuteAsync(command);
    }

    public async Task<bool> IdempotencyKeyExistsAsync(long accountId, string idempotencyKey, CancellationToken ct)
    {
        const string sql = """
            SELECT EXISTS(
                SELECT 1 FROM withdrawal_outbox
                 WHERE account_id = @AccountId AND idempotency_key = @IdempotencyKey
            );
        """;

        var command = new CommandDefinition(
            sql,
            new { AccountId = accountId, IdempotencyKey = idempotencyKey },
            _transaction,
            cancellationToken: ct);

        return await _connection.ExecuteScalarAsync<bool>(command);
    }
}
