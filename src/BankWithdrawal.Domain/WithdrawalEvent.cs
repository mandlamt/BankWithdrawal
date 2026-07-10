namespace BankWithdrawal.Domain;

/// <summary>
/// Integration event published after a withdrawal is durably recorded.
/// Immutable record - once created it represents a fact that happened.
/// A schema version is included so downstream consumers can evolve
/// independently of the producer (interoperability / consistency).
/// </summary>
public sealed record WithdrawalEvent(
    Guid EventId,
    long AccountId,
    decimal Amount,
    string Currency,
    WithdrawalStatus Status,
    string CorrelationId,
    DateTimeOffset OccurredAtUtc)
{
    public const int SchemaVersion = 1;
}

public enum WithdrawalStatus
{
    Successful,
    Failed
}

/// <summary>
/// Outcome of a withdrawal attempt, returned by the application service to the API layer.
/// Using a discriminated result instead of a bare string lets the controller map each
/// case to the correct HTTP status code, and lets callers pattern-match exhaustively.
/// </summary>
public enum WithdrawalOutcome
{
    Success,
    InsufficientFunds,
    AccountNotFound,
    DuplicateRequest
}

public sealed record WithdrawalResult(WithdrawalOutcome Outcome, decimal? RemainingBalance = null)
{
    public bool IsSuccess => Outcome == WithdrawalOutcome.Success || Outcome == WithdrawalOutcome.DuplicateRequest;
}
