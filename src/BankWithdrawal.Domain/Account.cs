namespace BankWithdrawal.Domain;

/// <summary>
/// Core domain entity. Deliberately anemic here - withdrawal rules live in the
/// application service so they can be unit tested without a database, but the
/// invariant ("balance can never go negative") is enforced again at the SQL layer
/// via an atomic conditional UPDATE, since the DB is the real source of truth
/// under concurrent access.
/// </summary>
public sealed class Account
{
    public long Id { get; init; }
    public decimal Balance { get; init; }
    public string Currency { get; init; } = "USD";
}
