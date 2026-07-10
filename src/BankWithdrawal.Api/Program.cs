using Amazon.SimpleNotificationService;
using BankWithdrawal.Application;
using BankWithdrawal.Infrastructure.Messaging;
using BankWithdrawal.Infrastructure.Persistence;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration -----------------------------------------------------
// Region, topic ARN and connection string come from configuration
// (appsettings / environment variables / AWS Parameter Store, per environment)
// instead of being hard-coded literals in source, which was the biggest portability
// and cost-efficiency (no rebuild-to-repoint) problem in the original snippet.
builder.Services.Configure<SnsOptions>(builder.Configuration.GetSection("Sns"));

builder.Services.AddSingleton(_ =>
{
    var connectionString = builder.Configuration.GetConnectionString("Bank")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:Bank configuration.");
    return NpgsqlDataSource.Create(connectionString);
});

builder.Services.AddDefaultAWSOptions(builder.Configuration.GetAWSOptions());
builder.Services.AddAWSService<IAmazonSimpleNotificationService>();

// --- Application services -----------------------------------------------
builder.Services.AddScoped<IUnitOfWorkFactory, SqlUnitOfWorkFactory>();
builder.Services.AddScoped<IWithdrawalService, WithdrawalService>();
builder.Services.AddSingleton<IEventPublisher, SnsEventPublisher>();
builder.Services.AddHostedService<OutboxDispatcherHostedService>();

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();

// Structured logging: JSON to stdout, which is what lets CorrelationId/AccountId
// scopes (added in the controller) show up as queryable fields in CloudWatch/ELK
// instead of being buried in a formatted string - the original code had zero logging.
builder.Logging.AddJsonConsole();

// Health checks so the load balancer / orchestrator can detect a broken DB
// connection instead of routing traffic to an instance that will 500 on every call.
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("Bank") ?? string.Empty);

var app = builder.Build();

app.UseExceptionHandler();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
