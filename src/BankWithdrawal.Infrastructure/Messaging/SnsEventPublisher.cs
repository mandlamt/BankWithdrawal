using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using BankWithdrawal.Application;
using BankWithdrawal.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BankWithdrawal.Infrastructure.Messaging;

public sealed class SnsOptions
{
    public string TopicArn { get; set; } = string.Empty;
}

/// <summary>
/// NOTE on library choice: this uses AWSSDK.SimpleNotificationService, the official
/// .NET SNS client (the C# equivalent of the Java SnsClient in the original snippet).
/// It is registered once as a singleton via the DI container instead of being
/// "new"-ed up inside a controller constructor, so it is reused across requests
/// (SDK clients are thread-safe and expensive to construct - pooled HTTP connections
/// etc) and so it can be swapped for a mock in tests.
/// </summary>
public sealed class SnsEventPublisher : IEventPublisher
{
    private readonly IAmazonSimpleNotificationService _sns;
    private readonly SnsOptions _options;
    private readonly ILogger<SnsEventPublisher> _logger;

    public SnsEventPublisher(IAmazonSimpleNotificationService sns, IOptions<SnsOptions> options, ILogger<SnsEventPublisher> logger)
    {
        _sns = sns;
        _options = options.Value;
        _logger = logger;
    }

    public async Task PublishAsync(WithdrawalEvent @event, CancellationToken ct)
    {
        var request = new PublishRequest
        {
            TopicArn = _options.TopicArn,
            Message = System.Text.Json.JsonSerializer.Serialize(@event),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["eventType"] = new() { DataType = "String", StringValue = nameof(WithdrawalEvent) },
                ["schemaVersion"] = new() { DataType = "Number", StringValue = WithdrawalEvent.SchemaVersion.ToString() },
                ["correlationId"] = new() { DataType = "String", StringValue = @event.CorrelationId }
            },
            // De-duplicates redelivery from the outbox poller if a FIFO topic is used;
            // harmless no-op on a standard topic.
            MessageDeduplicationId = @event.EventId.ToString(),
            MessageGroupId = @event.AccountId.ToString()
        };

        // The AWS SDK already retries transient errors internally (configurable
        // RetryMode); we let that stand and simply let exceptions propagate to the
        // outbox poller, which will retry the whole publish on its own schedule
        // rather than duplicating retry logic here.
        await _sns.PublishAsync(request, ct);

        _logger.LogInformation("Published withdrawal event {EventId} to SNS for account {AccountId}",
            @event.EventId, @event.AccountId);
    }
}
