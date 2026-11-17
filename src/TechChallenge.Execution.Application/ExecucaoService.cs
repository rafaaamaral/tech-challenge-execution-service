using Microsoft.Extensions.Logging;
using TechChallenge.Execution.Domain;

namespace TechChallenge.Execution.Application;

public class DuplicidadeException(string message) : Exception(message);

public interface IExecucaoRepository
{
    Task<Execucao?> ObterPorOrdemServicoAsync(Guid ordemServicoUniqueCode, CancellationToken ct);
    Task<bool> ExistePorOrdemServicoAsync(Guid ordemServicoUniqueCode, CancellationToken ct);
    /// <summary>Lança <see cref="DuplicidadeException"/> se a OS já possuir execução.</summary>
    Task InserirAsync(Execucao execucao, CancellationToken ct);
    Task AtualizarAsync(Execucao execucao, CancellationToken ct);
}

public record ExecucaoResponse(
    Guid Id,
    Guid OrdemServicoUniqueCode,
    string Status,
    DateTime CriadaEm,
    DateTime? DiagnosticoIniciadoEm,
    DateTime? ReparoIniciadoEm,
    DateTime? FinalizadaEm)
{
    public static ExecucaoResponse De(Execucao e) => new(
        e.Id, e.OrdemServicoUniqueCode, e.Status.ToString(),
        e.CriadaEm, e.DiagnosticoIniciadoEm, e.ReparoIniciadoEm, e.FinalizadaEm);
}

public class ExecucaoService(IExecucaoRepository repository, IEventPublisher publisher, ILogger<ExecucaoService> logger)
{
    public async Task<ExecucaoResponse> RegistrarAsync(Guid ordemServicoUniqueCode, CancellationToken ct)
    {
        var execucao = Execucao.Criar(ordemServicoUniqueCode);
        if (await repository.ExistePorOrdemServicoAsync(ordemServicoUniqueCode, ct))
            throw new DuplicidadeException("Já existe execução para esta ordem de serviço.");
        await repository.InserirAsync(execucao, ct);
        return ExecucaoResponse.De(execucao);
    }

    public Task<ExecucaoResponse?> ConsultarAsync(Guid ordemServicoUniqueCode, CancellationToken ct) =>
        AplicarAsync(ordemServicoUniqueCode, _ => { }, ct);

    public Task<ExecucaoResponse?> IniciarDiagnosticoAsync(Guid ordemServicoUniqueCode, CancellationToken ct) =>
        AplicarAsync(ordemServicoUniqueCode, e => e.IniciarDiagnostico(), ct, persistir: true);

    public Task<ExecucaoResponse?> IniciarReparoAsync(Guid ordemServicoUniqueCode, CancellationToken ct) =>
        AplicarAsync(ordemServicoUniqueCode, e => e.IniciarReparo(), ct, persistir: true);

    public async Task<ExecucaoResponse?> FinalizarAsync(Guid ordemServicoUniqueCode, CancellationToken ct)
    {
        var response = await AplicarAsync(ordemServicoUniqueCode, e => e.Finalizar(), ct, persistir: true);
        if (response is null) return null;

        try
        {
            await publisher.PublicarAsync(
                new ExecucaoFinalizada(Guid.NewGuid(), ordemServicoUniqueCode, DateTime.UtcNow), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Execução {OrdemServico} finalizada, mas a publicação de ExecucaoFinalizada falhou.", ordemServicoUniqueCode);
        }
        return response;
    }

    private async Task<ExecucaoResponse?> AplicarAsync(Guid ordemServicoUniqueCode, Action<Execucao> acao, CancellationToken ct, bool persistir = false)
    {
        var execucao = await repository.ObterPorOrdemServicoAsync(ordemServicoUniqueCode, ct);
        if (execucao is null) return null;
        acao(execucao);
        if (persistir) await repository.AtualizarAsync(execucao, ct);
        return ExecucaoResponse.De(execucao);
    }
}
