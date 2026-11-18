using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using TechChallenge.Execution.Infrastructure.Messaging;
using Testcontainers.MongoDb;
using Testcontainers.RabbitMq;

namespace TechChallenge.Execution.IntegrationTests;

public sealed class ExecutionFixture : IAsyncLifetime
{
    private const string RabbitUser = "execution-test";
    private const string RabbitPassword = "execution-test-local";

    private readonly MongoDbContainer _mongo = new MongoDbBuilder("mongo:7").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4.2.9-management")
        .WithUsername(RabbitUser)
        .WithPassword(RabbitPassword)
        .Build();
    private WebApplicationFactory<RegistrarExecucaoRequest>? _factory;

    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _factory!.Services;
    public IConnection Broker { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await Task.WhenAll(_mongo.StartAsync(startup.Token), _rabbit.StartAsync(startup.Token));

        _factory = new WebApplicationFactory<RegistrarExecucaoRequest>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MONGODB_CONNECTION_STRING", _mongo.GetConnectionString());
            builder.UseSetting("MONGODB_DATABASE", "execution_integration");
            builder.UseSetting("RABBITMQ_ENABLED", "true");
            builder.UseSetting("RABBITMQ_HOST", _rabbit.Hostname);
            builder.UseSetting("RABBITMQ_PORT", _rabbit.GetMappedPublicPort(5672).ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("RABBITMQ_USER", RabbitUser);
            builder.UseSetting("RABBITMQ_PASSWORD", RabbitPassword);
        });
        Client = _factory.CreateClient();

        var rabbitFactory = new ConnectionFactory { Uri = new Uri(_rabbit.GetConnectionString()) };
        Broker = await rabbitFactory.CreateConnectionAsync(startup.Token);
        using var consumerStartup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var channel = await Broker.CreateChannelAsync(cancellationToken: consumerStartup.Token);
            try
            {
                var queue = await channel.QueueDeclarePassiveAsync(RabbitMqOptions.Queue, consumerStartup.Token);
                if (queue.ConsumerCount > 0) return;
            }
            catch (OperationInterruptedException exception) when (exception.ShutdownReason?.ReplyCode == 404)
            {
                // The hosted service has not declared its queue yet.
            }

            await Task.Delay(100, consumerStartup.Token);
        }
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_factory is not null) await _factory.DisposeAsync();
        }
        finally
        {
            try
            {
                if (Broker is not null) await Broker.DisposeAsync();
            }
            finally
            {
                await Task.WhenAll(_mongo.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
            }
        }
    }
}
