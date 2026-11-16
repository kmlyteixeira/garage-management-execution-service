using MongoDB.Driver;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);
ConfigureObservability(builder);
ConfigureAuthentication(builder);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<IExecutionStore, MongoExecutionStore>();
builder.Services.AddSingleton<IEventBus, RabbitMqEventBus>();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health").AllowAnonymous();

static void ConfigureObservability(WebApplicationBuilder builder)
{
    var endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
    if (string.IsNullOrWhiteSpace(endpoint)) return;
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("garage-management-execution-service"))
        .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter(options => options.Endpoint = new Uri(endpoint)))
        .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddOtlpExporter(options => options.Endpoint = new Uri(endpoint)));
}

static void ConfigureAuthentication(WebApplicationBuilder builder)
{
    var enabled = builder.Configuration.GetValue<bool>("ExternalJwt:Enabled");
    var authentication = builder.Services.AddAuthentication();
    builder.Services.AddAuthorization(options =>
    {
        if (enabled)
            options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().Build();
    });
    if (!enabled) return;
    var publicKey = builder.Configuration["ExternalJwt:PublicKeyPem"]
        ?? throw new InvalidOperationException("ExternalJwt:PublicKeyPem não foi configurada.");
    using var rsa = RSA.Create();
    rsa.ImportFromPem(publicKey);
    authentication.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = builder.Configuration["ExternalJwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = builder.Configuration["ExternalJwt:Audience"],
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new RsaSecurityKey(rsa.ExportParameters(false)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };
        });
}
var allowedStatuses = new HashSet<string>(StringComparer.Ordinal)
{
    "Queued", "InDiagnosis", "InRepair", "Completed", "Canceled"
};

app.MapPost("/executions", async (
    StartExecutionRequest request,
    IExecutionStore store,
    CancellationToken cancellationToken) =>
{
    if (request.ServiceOrderId == Guid.Empty)
        return Results.BadRequest(new { error = "serviceOrderId é obrigatório." });
    var execution = new ExecutionResponse(Guid.NewGuid(), request.ServiceOrderId, request.CorrelationId, "Queued");
    await store.SaveAsync(execution, cancellationToken);
    return Results.Created($"/executions/{execution.ExecutionId}", execution);
}).WithOpenApi();

app.MapPut("/executions/{id:guid}/status", async (
    Guid id,
    UpdateExecutionStatusRequest request,
    IExecutionStore store,
    IEventBus bus,
    CancellationToken cancellationToken) =>
{
    if (!allowedStatuses.Contains(request.Status))
        return Results.BadRequest(new { error = "Status inválido." });
    var updated = await store.UpdateStatusAsync(id, request.Status, cancellationToken);
    if (updated && request.Status == "Completed")
    {
        var execution = await store.GetAsync(id, cancellationToken);
        if (execution is not null)
            await bus.PublishAsync(new ExecutionEvent("ExecutionCompleted", Guid.NewGuid(), execution.CorrelationId, execution.ServiceOrderId), cancellationToken);
    }
    return updated ? Results.NoContent() : Results.NotFound();
}).WithOpenApi();

app.Run();

public sealed record StartExecutionRequest(Guid ServiceOrderId, Guid CorrelationId);
public sealed record UpdateExecutionStatusRequest(string Status);
public sealed record ExecutionResponse(Guid ExecutionId, Guid ServiceOrderId, Guid CorrelationId, string Status);
public sealed record ExecutionEvent(string EventType, Guid EventId, Guid CorrelationId, Guid ServiceOrderId);

public interface IExecutionStore
{
    Task SaveAsync(ExecutionResponse execution, CancellationToken cancellationToken);
    Task<bool> UpdateStatusAsync(Guid id, string status, CancellationToken cancellationToken);
    Task<ExecutionResponse?> GetAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class MongoExecutionStore(IConfiguration configuration) : IExecutionStore
{
    private readonly IMongoCollection<ExecutionDocument> _collection = CreateCollection(configuration);

    public Task SaveAsync(ExecutionResponse execution, CancellationToken cancellationToken) =>
        _collection.InsertOneAsync(new ExecutionDocument
        {
            Id = execution.ExecutionId,
            ServiceOrderId = execution.ServiceOrderId,
            CorrelationId = execution.CorrelationId,
            Status = execution.Status,
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken: cancellationToken);

    public async Task<bool> UpdateStatusAsync(Guid id, string status, CancellationToken cancellationToken)
    {
        var result = await _collection.UpdateOneAsync(
            document => document.Id == id,
            Builders<ExecutionDocument>.Update
                .Set(document => document.Status, status)
                .Set(document => document.UpdatedAt, DateTimeOffset.UtcNow),
            cancellationToken: cancellationToken);
        return result.ModifiedCount == 1;
    }

    public async Task<ExecutionResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var document = await _collection.Find(item => item.Id == id).FirstOrDefaultAsync(cancellationToken);
        return document is null ? null : new ExecutionResponse(document.Id, document.ServiceOrderId, document.CorrelationId, document.Status);
    }

    private static IMongoCollection<ExecutionDocument> CreateCollection(IConfiguration configuration)
    {
        var connectionString = configuration["Mongo:ConnectionString"]
            ?? throw new InvalidOperationException("Mongo:ConnectionString não foi configurada.");
        var client = new MongoClient(connectionString);
        return client.GetDatabase(configuration["Mongo:Database"] ?? "execution_service")
            .GetCollection<ExecutionDocument>("executions");
    }
}

public sealed class ExecutionDocument
{
    public Guid Id { get; set; }
    public Guid ServiceOrderId { get; set; }
    public Guid CorrelationId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

public interface IEventBus
{
    Task PublishAsync(ExecutionEvent message, CancellationToken cancellationToken);
}

public sealed class RabbitMqEventBus : IEventBus, IDisposable
{
    private readonly IConnection connection;
    private readonly IModel channel;

    public RabbitMqEventBus(IConfiguration configuration)
    {
        var factory = new ConnectionFactory { Uri = new Uri(configuration["RabbitMq:Uri"] ?? "amqp://guest:guest@localhost:5672/") };
        connection = factory.CreateConnection();
        channel = connection.CreateModel();
        channel.ExchangeDeclare("garage.events", ExchangeType.Topic, durable: true);
    }

    public Task PublishAsync(ExecutionEvent message, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        channel.BasicPublish("garage.events", message.EventType, null, body);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        channel.Dispose();
        connection.Dispose();
    }
}
