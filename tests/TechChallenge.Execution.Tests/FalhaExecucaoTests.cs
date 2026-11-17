using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TechChallenge.Execution.Application;
using TechChallenge.Execution.Domain;

namespace TechChallenge.Execution.Tests;

public class FalhaExecucaoTests
{
    private class Repo : IExecucaoRepository
    {
        public List<Execucao> Itens { get; } = [];
        public Task<Execucao?> ObterPorOrdemServicoAsync(Guid c, CancellationToken ct) =>
            Task.FromResult(Itens.FirstOrDefault(e => e.OrdemServicoUniqueCode == c));
        public Task<bool> ExistePorOrdemServicoAsync(Guid c, CancellationToken ct) =>
            Task.FromResult(Itens.Any(e => e.OrdemServicoUniqueCode == c));
        public Task InserirAsync(Execucao e, CancellationToken ct) { Itens.Add(e); return Task.CompletedTask; }
        public Task AtualizarAsync(Execucao e, CancellationToken ct) => Task.CompletedTask;
    }

    private class Publisher : IEventPublisher
    {
        public List<object> Eventos { get; } = [];
        public bool Falhar { get; set; }
        public Task PublicarAsync<T>(T evento, CancellationToken ct) where T : class
        {
            if (Falhar) throw new InvalidOperationException("broker fora");
            Eventos.Add(evento);
            return Task.CompletedTask;
        }
    }

    private static (ExecucaoService s, Publisher p, Guid os) Criar()
    {
        var p = new Publisher();
        var s = new ExecucaoService(new Repo(), p, NullLogger<ExecucaoService>.Instance);
        return (s, p, Guid.NewGuid());
    }

    [Fact]
    public async Task Pendente_PodeFalhar_EPublicaUmEvento()
    {
        var (s, p, os) = Criar();
        await s.RegistrarAsync(os, default);
        var r = await s.FalharAsync(os, default);
        Assert.Equal("Falhou", r!.Status);
        var ev = Assert.IsType<ExecucaoFalhou>(Assert.Single(p.Eventos));
        Assert.Equal(os, ev.OrdemServicoUniqueCode);
        Assert.NotEqual(Guid.Empty, ev.EventId);
    }

    [Fact]
    public async Task ChamadaRepetida_NaoPublicaDuplicado()
    {
        var (s, p, os) = Criar();
        await s.RegistrarAsync(os, default);
        await s.FalharAsync(os, default);
        await s.FalharAsync(os, default);
        Assert.Single(p.Eventos);
    }

    [Fact]
    public async Task Finalizada_NaoPodeFalhar()
    {
        var (s, p, os) = Criar();
        await s.RegistrarAsync(os, default);
        await s.IniciarDiagnosticoAsync(os, default);
        await s.IniciarReparoAsync(os, default);
        await s.FinalizarAsync(os, default);
        p.Eventos.Clear();
        await Assert.ThrowsAsync<DomainException>(() => s.FalharAsync(os, default));
        Assert.Empty(p.Eventos);
    }

    [Fact]
    public async Task Falhou_NaoAceitaOutrasTransicoes()
    {
        var (s, _, os) = Criar();
        await s.RegistrarAsync(os, default);
        await s.FalharAsync(os, default);
        await Assert.ThrowsAsync<DomainException>(() => s.IniciarDiagnosticoAsync(os, default));
        await Assert.ThrowsAsync<DomainException>(() => s.FinalizarAsync(os, default));
    }

    [Fact]
    public async Task FalhaDePublicacao_NaoEhSilenciosa_EReenviaComMesmoEventId()
    {
        var (s, p, os) = Criar();
        await s.RegistrarAsync(os, default);
        p.Falhar = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.FalharAsync(os, default));
        Assert.Empty(p.Eventos);

        p.Falhar = false;
        await s.FalharAsync(os, default);
        var ev = Assert.IsType<ExecucaoFalhou>(Assert.Single(p.Eventos));
        await s.FalharAsync(os, default);
        Assert.Single(p.Eventos);
        Assert.NotEqual(Guid.Empty, ev.EventId);
    }

    [Fact]
    public async Task OsInexistente_RetornaNull()
    {
        var (s, _, _) = Criar();
        Assert.Null(await s.FalharAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public void ContratoJson_TemApenasCamposComuns()
    {
        var json = JsonSerializer.Serialize(new ExecucaoFalhou(Guid.NewGuid(), Guid.NewGuid(), null, DateTime.UtcNow), JsonSerializerOptions.Web);
        using var doc = JsonDocument.Parse(json);
        var nomes = doc.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray();
        Assert.Equal(["eventId", "occurredAt", "ordemServicoUniqueCode"], nomes);
    }
}
