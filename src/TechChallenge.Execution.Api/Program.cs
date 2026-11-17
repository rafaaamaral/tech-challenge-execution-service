using MongoDB.Driver;
using TechChallenge.Execution.Application;
using TechChallenge.Execution.Domain;
using TechChallenge.Execution.Infrastructure;
using TechChallenge.Execution.Infrastructure.Messaging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Bson.Serialization;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration["MONGODB_CONNECTION_STRING"] ?? "mongodb://localhost:27017";
var databaseName = builder.Configuration["MONGODB_DATABASE"] ?? "execution";

BsonSerializer.RegisterSerializer(
    new GuidSerializer(GuidRepresentation.Standard)
);
builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));
builder.Services.AddSingleton<IExecucaoRepository, MongoExecucaoRepository>();
builder.Services.AddScoped<ExecucaoService>();
builder.Services.AddScoped<IEventConsumer<OrcamentoAprovado>, OrcamentoAprovadoHandler>();

var rabbit = RabbitMqOptions.FromConfiguration(k => builder.Configuration[k]);
builder.Services.AddSingleton(rabbit);
if (rabbit.Enabled)
{
    builder.Services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
    builder.Services.AddHostedService<RabbitMqConsumerService>();
}
else
{
    builder.Services.AddSingleton<IEventPublisher, NoOpEventPublisher>();
}
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex) when (ex is DomainException or DuplicidadeException)
    {
        ctx.Response.StatusCode = StatusCodes.Status409Conflict;
        await ctx.Response.WriteAsJsonAsync(new { erro = ex.Message });
    }
});

app.MapHealthChecks("/health");

var g = app.MapGroup("/api/v1/execucoes");

g.MapPost("/", async (RegistrarExecucaoRequest req, ExecucaoService s, CancellationToken ct) =>
{
    if (req.OrdemServicoUniqueCode == Guid.Empty)
        return Results.BadRequest(new { erro = "OrdemServicoUniqueCode inválido." });
    var r = await s.RegistrarAsync(req.OrdemServicoUniqueCode, ct);
    return Results.Created($"/api/v1/execucoes/os/{r.OrdemServicoUniqueCode}", r);
});

g.MapGet("/os/{uniqueCode:guid}", async (Guid uniqueCode, ExecucaoService s, CancellationToken ct) =>
    await s.ConsultarAsync(uniqueCode, ct) is { } r ? Results.Ok(r) : Results.NotFound());

g.MapPatch("/os/{uniqueCode:guid}/diagnostico", async (Guid uniqueCode, ExecucaoService s, CancellationToken ct) =>
    await s.IniciarDiagnosticoAsync(uniqueCode, ct) is { } r ? Results.Ok(r) : Results.NotFound());

g.MapPatch("/os/{uniqueCode:guid}/reparo", async (Guid uniqueCode, ExecucaoService s, CancellationToken ct) =>
    await s.IniciarReparoAsync(uniqueCode, ct) is { } r ? Results.Ok(r) : Results.NotFound());

g.MapPatch("/os/{uniqueCode:guid}/finalizacao", async (Guid uniqueCode, ExecucaoService s, CancellationToken ct) =>
    await s.FinalizarAsync(uniqueCode, ct) is { } r ? Results.Ok(r) : Results.NotFound());

app.Run();

public record RegistrarExecucaoRequest(Guid OrdemServicoUniqueCode);
