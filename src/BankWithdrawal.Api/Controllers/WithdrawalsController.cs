using BankWithdrawal.Api.Contracts;
using BankWithdrawal.Application;
using BankWithdrawal.Domain;
using Microsoft.AspNetCore.Mvc;

namespace BankWithdrawal.Api.Controllers;

[ApiController]
[Route("api/v1/bank")]
public sealed class WithdrawalsController : ControllerBase
{
    private readonly IWithdrawalService _withdrawalService;
    private readonly ILogger<WithdrawalsController> _logger;

    public WithdrawalsController(IWithdrawalService withdrawalService, ILogger<WithdrawalsController> logger)
    {
        _withdrawalService = withdrawalService;
        _logger = logger;
    }

    /// <summary>
    /// Controller is intentionally thin: parse/validate the HTTP concerns, delegate
    /// the actual withdrawal to the application service, translate the outcome to
    /// an HTTP status. No SQL, no AWS SDK, no business rule lives here - that keeps
    /// this class trivial to read and means the business logic is testable without
    /// spinning up ASP.NET Core at all.
    /// </summary>
    [HttpPost("withdraw")]
    [ProducesResponseType(typeof(WithdrawalResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(WithdrawalResponse), StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WithdrawalResponse>> Withdraw(
        [FromBody] WithdrawalRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        // An idempotency key is required, not optional-with-a-default: a client
        // that doesn't send one has no safe way to retry a withdrawal after a
        // dropped connection, which was exactly the ambiguity in the original code
        // (a client timeout gives no indication of whether the debit happened).
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Problem(
                title: "Missing Idempotency-Key header",
                detail: "A unique Idempotency-Key header is required so retried requests are not double-processed.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var correlationId = HttpContext.TraceIdentifier;

        using var _ = _logger.BeginScope(new Dictionary<string, object>
        {
            ["AccountId"] = request.AccountId,
            ["CorrelationId"] = correlationId
        });

        var result = await _withdrawalService.WithdrawAsync(
            request.AccountId, request.Amount, idempotencyKey, correlationId, ct);

        return result.Outcome switch
        {
            WithdrawalOutcome.Success or WithdrawalOutcome.DuplicateRequest => Ok(new WithdrawalResponse
            {
                Status = "SUCCESSFUL",
                RemainingBalance = result.RemainingBalance,
                CorrelationId = correlationId
            }),

            WithdrawalOutcome.InsufficientFunds => Problem(
                title: "Insufficient funds",
                statusCode: StatusCodes.Status402PaymentRequired,
                extensions: new Dictionary<string, object?> { ["correlationId"] = correlationId }),

            WithdrawalOutcome.AccountNotFound => Problem(
                title: "Account not found",
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?> { ["correlationId"] = correlationId }),

            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }
}
