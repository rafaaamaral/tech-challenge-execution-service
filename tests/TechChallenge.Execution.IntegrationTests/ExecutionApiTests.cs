using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using TechChallenge.Execution.Application;
using TechChallenge.Execution.Domain;
using TechChallenge.Execution.Infrastructure.Messaging;

namespace TechChallenge.Execution.IntegrationTests;

public sealed class ExecutionApiTests(ExecutionFixture fixture) : IClassFixture<ExecutionFixture>
{
    private const string Execucoes = "/api/v1/execucoes";
    private readonly HttpClient _client = fixture.Client;

    [Fact]
    public async Task HealthRetornaHealthy()
    {
        using var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RegistroValidaCodigoEImpedeDuplicidade()
    {
        using var invalida = await _client.PostAsJsonAsync(Execucoes, new RegistrarExecucaoRequest(Guid.Empty));
        Assert.Equal(HttpStatusCode.BadRequest, invalida.StatusCode);

        var ordem = Guid.NewGuid();
        var criada = await RegistrarAsync(ordem);
        using var duplicada = await _client.PostAsJsonAsync(Execucoes, new RegistrarExecucaoRequest(ordem));
        Assert.Equal(HttpStatusCode.Conflict, duplicada.StatusCode);
        var persistida = await _client.GetFromJsonAsync<ExecucaoResponse>($"{Execucoes}/os/{ordem}");
        Assert.NotNull(persistida);
        Assert.Equal(criada.Id, persistida.Id);
        Assert.Equal("Pendente", persistida.Status);

        var repository = fixture.Services.GetRequiredService<IExecucaoRepository>();
        await Assert.ThrowsAsync<DuplicidadeException>(() => repository.InserirAsync(Execucao.Criar(ordem), default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/diagnostico")]
    [InlineData("/reparo")]
    [InlineData("/finalizacao")]
    [InlineData("/falha")]
    public async Task OrdemInexistenteRetornaNotFound(string sufixo)
    {
        var uri = $"{Execucoes}/os/{Guid.NewGuid()}{sufixo}";
        using var response = sufixo.Length == 0 ? await _client.GetAsync(uri) : await _client.PatchAsync(uri, null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FinalizacaoPersisteTransicoesEPublicaEvento()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var channel = await fixture.Broker.CreateChannelAsync(cancellationToken: ct);
        var fila = await ObservarEventoAsync(channel, RabbitMqOptions.RoutingKeyFinalizada, ct);
        var ordem = Guid.NewGuid();
        var criada = await RegistrarAsync(ordem);

        using var prematura = await _client.PatchAsync($"{Execucoes}/os/{ordem}/finalizacao", null, ct);
        Assert.Equal(HttpStatusCode.Conflict, prematura.StatusCode);
        Assert.Equal("EmDiagnostico", (await AtualizarAsync(ordem, "diagnostico")).Status);
        Assert.Equal("EmReparo", (await AtualizarAsync(ordem, "reparo")).Status);

        var finalizada = await AtualizarAsync(ordem, "finalizacao");
        Assert.Equal("Finalizada", finalizada.Status);
        Assert.Equal(criada.Id, finalizada.Id);
        Assert.NotNull(finalizada.FinalizadaEm);
        var persistida = await _client.GetFromJsonAsync<ExecucaoResponse>($"{Execucoes}/os/{ordem}", ct);
        Assert.NotNull(persistida);
        Assert.Equal(finalizada.Id, persistida.Id);
        Assert.Equal("Finalizada", persistida.Status);
        Assert.NotNull(persistida.FinalizadaEm);

        var mensagem = await LerMensagemAsync(channel, fila, ct);
        Assert.Equal(RabbitMqOptions.RoutingKeyFinalizada, mensagem.RoutingKey);
        Assert.Equal("application/json", mensagem.BasicProperties.ContentType);
        var evento = JsonSerializer.Deserialize<ExecucaoFinalizada>(mensagem.Body.Span, JsonSerializerOptions.Web);
        Assert.NotNull(evento);
        Assert.Equal(ordem, evento.OrdemServicoUniqueCode);
        Assert.NotEqual(Guid.Empty, evento.EventId);
    }

    [Fact]
    public async Task FalhaPersisteEPublicaSomenteUmaVez()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var channel = await fixture.Broker.CreateChannelAsync(cancellationToken: ct);
        var fila = await ObservarEventoAsync(channel, RabbitMqOptions.RoutingKeyFalhou, ct);
        var ordem = Guid.NewGuid();
        await RegistrarAsync(ordem);

        var falhou = await AtualizarAsync(ordem, "falha");
        Assert.Equal("Falhou", falhou.Status);
        Assert.Equal(falhou, await AtualizarAsync(ordem, "falha"));

        var mensagem = await LerMensagemAsync(channel, fila, ct);
        Assert.Equal(RabbitMqOptions.RoutingKeyFalhou, mensagem.RoutingKey);
        var evento = JsonSerializer.Deserialize<ExecucaoFalhou>(mensagem.Body.Span, JsonSerializerOptions.Web);
        Assert.NotNull(evento);
        Assert.Equal(ordem, evento.OrdemServicoUniqueCode);
        Assert.NotEqual(Guid.Empty, evento.EventId);
        Assert.Null(await channel.BasicGetAsync(fila, autoAck: true, cancellationToken: ct));
    }

    [Fact]
    public async Task OrcamentoAprovadoCriaExecucaoAcessivelPelaApi()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var channel = await fixture.Broker.CreateChannelAsync(cancellationToken: ct);
        var ordem = Guid.NewGuid();
        var evento = new OrcamentoAprovado(Guid.NewGuid(), ordem, DateTime.UtcNow);
        var corpo = JsonSerializer.SerializeToUtf8Bytes(evento, JsonSerializerOptions.Web);
        await PublicarOrcamentoAsync(channel, corpo, ct);

        while (true)
        {
            using var response = await _client.GetAsync($"{Execucoes}/os/{ordem}", ct);
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var execucao = await response.Content.ReadFromJsonAsync<ExecucaoResponse>(ct);
                Assert.NotNull(execucao);
                Assert.Equal(ordem, execucao.OrdemServicoUniqueCode);
                Assert.Equal("Pendente", execucao.Status);
                return;
            }

            await Task.Delay(50, ct);
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task OrcamentoInvalidoVaiParaFilaDeErros(string json)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var channel = await fixture.Broker.CreateChannelAsync(cancellationToken: ct);
        await PublicarOrcamentoAsync(channel, Encoding.UTF8.GetBytes(json), ct);

        var mensagem = await LerMensagemAsync(channel, RabbitMqOptions.ErrorQueue, ct);
        Assert.Equal(Encoding.UTF8.GetBytes(json), mensagem.Body.ToArray());
    }

    [Fact]
    public Task PublicadorRejeitaEventosSemRoutingKey() =>
        Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Services.GetRequiredService<IEventPublisher>()
            .PublicarAsync(new ExecucaoSolicitada(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow), default));

    private async Task<ExecucaoResponse> RegistrarAsync(Guid ordem)
    {
        using var response = await _client.PostAsJsonAsync(Execucoes, new RegistrarExecucaoRequest(ordem));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"{Execucoes}/os/{ordem}", response.Headers.Location?.OriginalString);
        var execucao = await response.Content.ReadFromJsonAsync<ExecucaoResponse>();
        Assert.NotNull(execucao);
        Assert.Equal(ordem, execucao.OrdemServicoUniqueCode);
        return execucao;
    }

    private async Task<ExecucaoResponse> AtualizarAsync(Guid ordem, string etapa)
    {
        using var response = await _client.PatchAsync($"{Execucoes}/os/{ordem}/{etapa}", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var execucao = await response.Content.ReadFromJsonAsync<ExecucaoResponse>();
        Assert.NotNull(execucao);
        return execucao;
    }

    private static async Task<string> ObservarEventoAsync(IChannel channel, string routingKey, CancellationToken ct)
    {
        var queue = await channel.QueueDeclareAsync(cancellationToken: ct);
        await channel.QueueBindAsync(queue.QueueName, RabbitMqOptions.Exchange, routingKey, cancellationToken: ct);
        return queue.QueueName;
    }

    private static ValueTask PublicarOrcamentoAsync(IChannel channel, byte[] corpo, CancellationToken ct) =>
        channel.BasicPublishAsync(RabbitMqOptions.Exchange, RabbitMqOptions.RoutingKeyAprovado, corpo, ct);

    private static async Task<BasicGetResult> LerMensagemAsync(IChannel channel, string fila, CancellationToken ct)
    {
        while (true)
        {
            var mensagem = await channel.BasicGetAsync(fila, autoAck: true, cancellationToken: ct);
            if (mensagem is not null) return mensagem;
            await Task.Delay(50, ct);
        }
    }
}
