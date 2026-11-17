using Microsoft.Extensions.Logging;
using TechChallenge.Execution.Domain;

namespace TechChallenge.Execution.Application;

// Consumido (routing key billing.aprovado)
public record OrcamentoAprovado(Guid EventId, Guid OrdemServicoUniqueCode, DateTime OccurredAt);

// Publicados (routing keys execution.finalizada / execution.falhou)
public record ExecucaoSolicitada(Guid EventId, Guid OrdemServicoUniqueCode, DateTime OccurredAt);
public record ExecucaoFinalizada(Guid EventId, Guid OrdemServicoUniqueCode, DateTime OccurredAt);
public record ExecucaoFalhou(Guid EventId, Guid OrdemServicoUniqueCode, string Motivo, DateTime OccurredAt);

public interface IEventPublisher
{
    Task PublicarAsync<TEvent>(TEvent evento, CancellationToken ct) where TEvent : class;
}

public interface IEventConsumer<TEvent> where TEvent : class
{
    Task ConsumirAsync(TEvent evento, CancellationToken ct);
}

public class OrcamentoAprovadoHandler(
    ExecucaoService service,
    IEventPublisher publisher,
    ILogger<OrcamentoAprovadoHandler> logger) : IEventConsumer<OrcamentoAprovado>
{
    /// <summary>
    /// Duplicidade é tratada como sucesso (idempotência). DomainException é falha de negócio definitiva
    /// e gera ExecucaoFalhou. Qualquer outra exceção (infraestrutura) propaga para o consumidor reenfileirar.
    /// </summary>
    public async Task ConsumirAsync(OrcamentoAprovado evento, CancellationToken ct)
    {
        try
        {
            await service.RegistrarAsync(evento.OrdemServicoUniqueCode, ct);
        }
        catch (DuplicidadeException)
        {
            logger.LogInformation("Evento {EventId} ignorado: execução já existe para a OS {OrdemServico}.",
                evento.EventId, evento.OrdemServicoUniqueCode);
        }
        catch (DomainException ex)
        {
            logger.LogWarning(ex, "Falha de negócio ao criar execução da OS {OrdemServico}.", evento.OrdemServicoUniqueCode);
            await publisher.PublicarAsync(
                new ExecucaoFalhou(Guid.NewGuid(), evento.OrdemServicoUniqueCode, ex.Message, DateTime.UtcNow), ct);
        }
    }
}
