using BankWithdrawal.Domain;
using Microsoft.Extensions.Logging;

namespace BankWithdrawal.Application;

public interface IWithdrawalService
{
    Task<WithdrawalResult> WithdrawAsync(long accountId, decimal amount, string idempotencyKey, string correlationId, CancellationToken ct);
}

/// <summary>
/// Application service holding the withdrawal use case. Pure orchestration - no SQL,
/// no AWS SDK - which is what makes it unit-testable with in-memory fakes for
/// IUnitOfWorkFactory. Business rule (balance >= amount) is expressed once here for
/// documentation/readability, and enforced for real by the atomic UPDATE in the
/// repository so it holds under concurrency.
/// </summary>
public sealed class WithdrawalService : IWithdrawalService
{
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly ILogger<WithdrawalService> _logger;

    public WithdrawalService(IUnitOfWorkFactory unitOfWorkFactory, ILogger<WithdrawalService> logger)
    {
        _unitOfWorkFactory = unitOfWorkFactory;
        _logger = logger;
    }

    public async Task<WithdrawalResult> WithdrawAsync(
        long accountId,
        decimal amount,
        string idempotencyKey,
        string correlationId,
        CancellationToken ct)
    {
        if (amount <= 0)
        {
            // Defensive: DTO-level validation should already have caught this,
            // but a domain-level guard protects any other future caller of this service.
            throw new ArgumentOutOfRangeException(nameof(amount), "Withdrawal amount must be positive.");
        }

        await using var uow = await _unitOfWorkFactory.BeginAsync(ct);

        if (await uow.Outbox.IdempotencyKeyExistsAsync(accountId, idempotencyKey, ct))
        {
            _logger.LogInformation(
                "Duplicate withdrawal request suppressed. AccountId={AccountId} IdempotencyKey={IdempotencyKey} CorrelationId={CorrelationId}",
                accountId, idempotencyKey, correlationId);
            return new WithdrawalResult(WithdrawalOutcome.DuplicateRequest);
        }

        var newBalance = await uow.Accounts.TryDebitAsync(accountId, amount, ct);

        if (newBalance is null)
        {
            // Either the account doesn't exist, or the balance check failed.
            // One extra read only on the unhappy path keeps the common (success) path
            // to a single round trip, and gives callers/observability an accurate reason.
            var exists = await uow.Accounts.ExistsAsync(accountId, ct);
            var outcome = exists ? WithdrawalOutcome.InsufficientFunds : WithdrawalOutcome.AccountNotFound;

            _logger.LogWarning(
                "Withdrawal rejected. AccountId={AccountId} Amount={Amount} Outcome={Outcome} CorrelationId={CorrelationId}",
                accountId, amount, outcome, correlationId);

            return new WithdrawalResult(outcome);
        }

        var domainEvent = new WithdrawalEvent(
            EventId: Guid.NewGuid(),
            AccountId: accountId,
            Amount: amount,
            Currency: "USD",
            Status: WithdrawalStatus.Successful,
            CorrelationId: correlationId,
            OccurredAtUtc: DateTimeOffset.UtcNow);

        // Same transaction as the debit: either both the balance change and the
        // outbox record land, or neither does. This is what removes the original
        // bug class where a real balance mutation could occur with no corresponding
        // (or a lost) notification.
        await uow.Outbox.InsertAsync(domainEvent, idempotencyKey, ct);
        await uow.CommitAsync(ct);

        _logger.LogInformation(
            "Withdrawal succeeded. AccountId={AccountId} Amount={Amount} RemainingBalance={RemainingBalance} CorrelationId={CorrelationId}",
            accountId, amount, newBalance, correlationId);

        return new WithdrawalResult(WithdrawalOutcome.Success, newBalance);
    }
}
