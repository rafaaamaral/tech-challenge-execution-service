using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TechChallenge.Execution.Application;

namespace TechChallenge.Execution.Infrastructure.Messaging;

public class RabbitMqOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string User { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VHost { get; set; } = "/";

    public const string Exchange = "techchallenge.events";
    public const string Queue = "execution-service.events";
    public const string DeadLetterExchange = "techchallenge.events.dlx";
    public const string ErrorQueue = "execution-service.events.error";
    public const string RoutingKeyAprovado = "billing.aprovado";
    public const string RoutingKeyFinalizada = "execution.finalizada";
    public const string RoutingKeyFalhou = "execution.falhou";

    public static RabbitMqOptions FromConfiguration(Func<string, string?> get) => new()
    {
        Enabled = bool.TryParse(get("RABBITMQ_ENABLED"), out var e) && e,
        Host = get("RABBITMQ_HOST") ?? "localhost",
        Port = int.TryParse(get("RABBITMQ_PORT"), out var p) ? p : 5672,
        User = get("RABBITMQ_USER") ?? "guest",
        Password = get("RABBITMQ_PASSWORD") ?? "guest",
        VHost = get("RABBITMQ_VHOST") ?? "/"
    };

    public ConnectionFactory CreateFactory() => new()
    {
        HostName = Host,
        Port = Port,
        UserName = User,
        Password = Password,
        VirtualHost = VHost,
        AutomaticRecoveryEnabled = true
    };
}

public class NoOpEventPublisher(ILogger<NoOpEventPublisher> logger) : IEventPublisher
{
    public Task PublicarAsync<TEvent>(TEvent evento, CancellationToken ct) where TEvent : class
    {
        logger.LogWarning("RabbitMQ desabilitado: evento {Evento} não publicado.", typeof(TEvent).Name);
        return Task.CompletedTask;
    }
}

public sealed class RabbitMqEventPublisher(RabbitMqOptions options) : IEventPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    public async Task PublicarAsync<TEvent>(TEvent evento, CancellationToken ct) where TEvent : class
    {
        var routingKey = evento switch
        {
            ExecucaoFinalizada => RabbitMqOptions.RoutingKeyFinalizada,
            ExecucaoFalhou => RabbitMqOptions.RoutingKeyFalhou,
            _ => throw new InvalidOperationException($"Evento sem routing key: {typeof(TEvent).Name}")
        };

        var connection = await ObterConexaoAsync(ct);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            ct);
        await channel.ExchangeDeclareAsync(RabbitMqOptions.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);

        var body = JsonSerializer.SerializeToUtf8Bytes(evento, JsonSerializerOptions.Web);
        var props = new BasicProperties { ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent };
        await channel.BasicPublishAsync(RabbitMqOptions.Exchange, routingKey, mandatory: false, props, body, ct);
    }

    private async Task<IConnection> ObterConexaoAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true }) return _connection;
        await _lock.WaitAsync(ct);
        try
        {
            if (_connection is not { IsOpen: true })
                _connection = await options.CreateFactory().CreateConnectionAsync(ct);
            return _connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
    }
}

public class RabbitMqConsumerService(
    RabbitMqOptions options,
    IServiceScopeFactory scopeFactory,
    ILogger<RabbitMqConsumerService> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IConnection? connection = null;
        IChannel? channel = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                connection = await options.CreateFactory().CreateConnectionAsync(stoppingToken);
                channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await ConfigurarAsync(channel, stoppingToken);
                logger.LogInformation("Consumindo a fila {Queue}.", RabbitMqOptions.Queue);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "RabbitMQ indisponível; nova tentativa em {Delay}s.", RetryDelay.TotalSeconds);
                try { await Task.Delay(RetryDelay, stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (channel is not null) await channel.DisposeAsync();
            if (connection is not null) await connection.DisposeAsync();
        }
    }

    private async Task ConfigurarAsync(IChannel channel, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(RabbitMqOptions.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(RabbitMqOptions.DeadLetterExchange, ExchangeType.Fanout, durable: true, cancellationToken: ct);
        await channel.QueueDeclareAsync(RabbitMqOptions.ErrorQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(RabbitMqOptions.ErrorQueue, RabbitMqOptions.DeadLetterExchange, "", cancellationToken: ct);
        await channel.QueueDeclareAsync(RabbitMqOptions.Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = RabbitMqOptions.DeadLetterExchange },
            cancellationToken: ct);
        await channel.QueueBindAsync(RabbitMqOptions.Queue, RabbitMqOptions.Exchange, RabbitMqOptions.RoutingKeyAprovado, cancellationToken: ct);
        await channel.BasicQosAsync(0, 1, false, ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, ea) => ProcessarAsync(channel, ea, ct);
        await channel.BasicConsumeAsync(RabbitMqOptions.Queue, autoAck: false, consumer, ct);
    }

    private async Task ProcessarAsync(IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        OrcamentoAprovado? evento;
        try
        {
            evento = JsonSerializer.Deserialize<OrcamentoAprovado>(Encoding.UTF8.GetString(ea.Body.Span), JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            evento = null;
        }

        if (evento is null || evento.OrdemServicoUniqueCode == Guid.Empty)
        {
            logger.LogWarning("Mensagem inválida enviada para a fila de erro {ErrorQueue}.", RabbitMqOptions.ErrorQueue);
            await channel.BasicRejectAsync(ea.DeliveryTag, requeue: false, ct);
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<IEventConsumer<OrcamentoAprovado>>();
            await handler.ConsumirAsync(evento, ct);
            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falha transitória ao processar o evento {EventId}; reenfileirando.", evento.EventId);
            try { await Task.Delay(RetryDelay, ct); } catch (OperationCanceledException) { }
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
        }
    }
}
