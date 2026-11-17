using Microsoft.Extensions.Logging.Abstractions;
using TechChallenge.Execution.Application;
using TechChallenge.Execution.Domain;

namespace TechChallenge.Execution.Tests;

public class OrcamentoAprovadoHandlerTests
{
    private class Repo : IExecucaoRepository
    {
        public List<Execucao> Itens { get; } = [];
        public Exception? Falha { get; set; }
        public Task<Execucao?> ObterPorOrdemServicoAsync(Guid c, CancellationToken ct) =>
            Task.FromResult(Itens.FirstOrDefault(e => e.OrdemServicoUniqueCode == c));
        public Task<bool> ExistePorOrdemServicoAsync(Guid c, CancellationToken ct) =>
            Falha is null ? Task.FromResult(Itens.Any(e => e.OrdemServicoUniqueCode == c)) : throw Falha;
        public Task InserirAsync(Execucao e, CancellationToken ct) { Itens.Add(e); return Task.CompletedTask; }
        public Task AtualizarAsync(Execucao e, CancellationToken ct) => Task.CompletedTask;
    }

    private class Publisher : IEventPublisher
    {
        public List<object> Eventos { get; } = [];
        public Task PublicarAsync<T>(T evento, CancellationToken ct) where T : class { Eventos.Add(evento); return Task.CompletedTask; }
    }

    private static (OrcamentoAprovadoHandler h, ExecucaoService s, Repo r, Publisher p) Criar()
    {
        var r = new Repo();
        var p = new Publisher();
        var s = new ExecucaoService(r, p, NullLogger<ExecucaoService>.Instance);
        return (new OrcamentoAprovadoHandler(s, p, NullLogger<OrcamentoAprovadoHandler>.Instance), s, r, p);
    }

    private static OrcamentoAprovado Evento(Guid os) => new(Guid.NewGuid(), os, DateTime.UtcNow);

    [Fact]
    public async Task CriaExecucaoPendente()
    {
        var (h, _, r, p) = Criar();
        var os = Guid.NewGuid();
        await h.ConsumirAsync(Evento(os), default);
        Assert.Equal(StatusExecucao.Pendente, Assert.Single(r.Itens).Status);
        Assert.Empty(p.Eventos);
    }

    [Fact]
    public async Task EventoRepetido_EhIdempotente()
    {
        var (h, _, r, p) = Criar();
        var e = Evento(Guid.NewGuid());
        await h.ConsumirAsync(e, default);
        await h.ConsumirAsync(e, default);
        Assert.Single(r.Itens);
        Assert.Empty(p.Eventos);
    }

    [Fact]
    public async Task FalhaDeNegocio_PublicaExecucaoFalhou()
    {
        var (h, _, r, p) = Criar();
        await h.ConsumirAsync(Evento(Guid.Empty), default);
        var falha = Assert.IsType<ExecucaoFalhou>(Assert.Single(p.Eventos));
        Assert.False(string.IsNullOrWhiteSpace(falha.Motivo));
        Assert.Empty(r.Itens);
    }

    [Fact]
    public async Task FalhaTransitoria_PropagaSemPublicarFalha()
    {
        var (h, _, r, p) = Criar();
        r.Falha = new TimeoutException();
        await Assert.ThrowsAsync<TimeoutException>(() => h.ConsumirAsync(Evento(Guid.NewGuid()), default));
        Assert.Empty(p.Eventos);
    }

    [Fact]
    public async Task Finalizar_PublicaExecucaoFinalizada()
    {
        var (h, s, _, p) = Criar();
        var os = Guid.NewGuid();
        await h.ConsumirAsync(Evento(os), default);
        await s.IniciarDiagnosticoAsync(os, default);
        await s.IniciarReparoAsync(os, default);
        await s.FinalizarAsync(os, default);
        var ev = Assert.IsType<ExecucaoFinalizada>(Assert.Single(p.Eventos));
        Assert.Equal(os, ev.OrdemServicoUniqueCode);
    }
}
