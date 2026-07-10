using System.ComponentModel.DataAnnotations;

namespace BankWithdrawal.Api.Contracts;

/// <summary>
/// Bound from the JSON request body instead of query-string @RequestParam-style
/// binding. Money and account identifiers in a URL/query string are easy to tamper
/// with, get logged in access logs and proxies by default, and don't support
/// declarative validation - a body-bound DTO with DataAnnotations fixes all three.
/// </summary>
public sealed class WithdrawalRequest
{
    [Range(1, long.MaxValue)]
    public long AccountId { get; init; }

    [Range(typeof(decimal), "0.01", "79228162514264337593543950335")]
    public decimal Amount { get; init; }
}

public sealed class WithdrawalResponse
{
    public required string Status { get; init; }
    public decimal? RemainingBalance { get; init; }
    public required string CorrelationId { get; init; }
}
