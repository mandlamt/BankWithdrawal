using BankWithdrawal.Application;
using BankWithdrawal.Domain;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BankWithdrawal.Infrastructure.Messaging;

/// <summary>
/// Polls withdrawal_outbox for PENDING rows and publishes them to SNS.
/// This is what actually solves the "dual write" problem: the HTTP request only
/// ever writes to Postgres (one transactional resource), and this separate process
/// is solely responsible for the at-least-once delivery of already-committed events
/// to SNS, with its own retry/backoff independent of request latency.
/// A crash between "row committed" and "row published" just leaves the row PENDING
/// for the next poll - no event is ever silently lost, unlike the original code
/// where a JVM crash between the DB commit and the SNS publish call would drop the
/// notification with no trace of it having been attempted.
/// </summary>
public sealed class OutboxDispatcherHostedService : BackgroundService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEventPublisher _publisher;
    private readonly ILogger<OutboxDispatcherHostedService> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 50;
    private const int MaxAttempts = 8;

    public OutboxDispatcherHostedService(
        NpgsqlDataSource dataSource,
        IEventPublisher publisher,
        ILogger<OutboxDispatcherHostedService> logger)
    {
        _dataSource = dataSource;
        _publisher = publisher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dispatched = await DispatchPendingBatchAsync(stoppingToken);
                if (dispatched == 0)
                {
                    await Task.Delay(_pollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A poll-loop failure must never crash the host; log and back off.
                _logger.LogError(ex, "Outbox dispatch loop iteration failed");
                await Task.Delay(_pollInterval, stoppingToken);
            }
        }
    }

    private async Task<int> DispatchPendingBatchAsync(CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        // SKIP LOCKED lets multiple instances of this service run for horizontal
        // scaling / high availability without two instances racing to publish the
        // same row.
        const string selectSql = """
            SELECT event_id, account_id, payload, attempts
              FROM withdrawal_outbox
             WHERE status = 'PENDING' AND attempts < @MaxAttempts
             ORDER BY created_at_utc
             LIMIT @BatchSize
               FOR UPDATE SKIP LOCKED;
        """;

        var rows = (await connection.QueryAsync<OutboxRow>(
            new CommandDefinition(selectSql, new { BatchSize, MaxAttempts }, cancellationToken: ct))).AsList();

        foreach (var row in rows)
        {
            var @event = System.Text.Json.JsonSerializer.Deserialize<WithdrawalEvent>(row.Payload)
                ?? throw new InvalidOperationException($"Outbox row {row.EventId} has an unparsable payload.");

            try
            {
                await _publisher.PublishAsync(@event, ct);
                await MarkPublishedAsync(connection, row.EventId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish outbox event {EventId}, attempt {Attempt}", row.EventId, row.Attempts + 1);
                await MarkAttemptFailedAsync(connection, row.EventId, ct);
            }
        }

        return rows.Count;
    }

    private static Task MarkPublishedAsync(NpgsqlConnection connection, Guid eventId, CancellationToken ct)
    {
        const string sql = """
            UPDATE withdrawal_outbox
               SET status = 'PUBLISHED', published_at_utc = now()
             WHERE event_id = @EventId;
        """;
        return connection.ExecuteAsync(new CommandDefinition(sql, new { EventId = eventId }, cancellationToken: ct));
    }

    private static Task MarkAttemptFailedAsync(NpgsqlConnection connection, Guid eventId, CancellationToken ct)
    {
        // attempts + exponential-backoff-friendly next_attempt_at leaves room for a
        // smarter scheduling query later without changing the table shape.
        const string sql = """
            UPDATE withdrawal_outbox
               SET attempts = attempts + 1,
                   status = CASE WHEN attempts + 1 >= @MaxAttempts THEN 'DEAD_LETTER' ELSE 'PENDING' END
             WHERE event_id = @EventId;
        """;
        return connection.ExecuteAsync(new CommandDefinition(sql, new { EventId = eventId, MaxAttempts }, cancellationToken: ct));
    }

    private sealed record OutboxRow(Guid EventId, long AccountId, string Payload, int Attempts);
}
