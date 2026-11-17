using TechChallenge.Execution.Application;
using TechChallenge.Execution.Domain;

namespace TechChallenge.Execution.Tests;

public class ExecucaoServiceTests
{
    private class FakeRepo : IExecucaoRepository
    {
        public List<Execucao> Itens { get; } = [];
        public Task<Execucao?> ObterPorOrdemServicoAsync(Guid codigo, CancellationToken ct) =>
            Task.FromResult(Itens.FirstOrDefault(e => e.OrdemServicoUniqueCode == codigo));
        public Task<bool> ExistePorOrdemServicoAsync(Guid codigo, CancellationToken ct) =>
            Task.FromResult(Itens.Any(e => e.OrdemServicoUniqueCode == codigo));
        public Task InserirAsync(Execucao execucao, CancellationToken ct) { Itens.Add(execucao); return Task.CompletedTask; }
        public Task AtualizarAsync(Execucao execucao, CancellationToken ct) => Task.CompletedTask;
    }

    private class NoPublisher : IEventPublisher
    {
        public Task PublicarAsync<T>(T evento, CancellationToken ct) where T : class => Task.CompletedTask;
    }

    private static ExecucaoService Novo() =>
        new(new FakeRepo(), new NoPublisher(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ExecucaoService>.Instance);

    [Fact]
    public async Task FluxoPorOrdemServico_Funciona()
    {
        var service = Novo();
        var os = Guid.NewGuid();
        await service.RegistrarAsync(os, default);
        await service.IniciarDiagnosticoAsync(os, default);
        await service.IniciarReparoAsync(os, default);
        var r = await service.FinalizarAsync(os, default);
        Assert.Equal("Finalizada", r!.Status);
        Assert.Equal(os, (await service.ConsultarAsync(os, default))!.OrdemServicoUniqueCode);
    }

    [Fact]
    public async Task OrdemServicoInexistente_RetornaNull() =>
        Assert.Null(await Novo().ConsultarAsync(Guid.NewGuid(), default));

    [Fact]
    public async Task Registrar_Duplicado_Falha()
    {
        var service = Novo();
        var os = Guid.NewGuid();
        await service.RegistrarAsync(os, default);
        await Assert.ThrowsAsync<DuplicidadeException>(() => service.RegistrarAsync(os, default));
    }

    [Fact]
    public async Task TransicaoInvalida_Falha()
    {
        var service = Novo();
        var os = Guid.NewGuid();
        await service.RegistrarAsync(os, default);
        await Assert.ThrowsAsync<DomainException>(() => service.FinalizarAsync(os, default));
    }
}

