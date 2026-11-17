namespace TechChallenge.Execution.Domain;

public enum StatusExecucao
{
    Pendente,
    EmDiagnostico,
    EmReparo,
    Finalizada
}

public class DomainException(string message) : Exception(message);

public class Execucao
{
    private Execucao() { }

    public Guid Id { get; private set; }
    public Guid OrdemServicoUniqueCode { get; private set; }
    public StatusExecucao Status { get; private set; }
    public DateTime CriadaEm { get; private set; }
    public DateTime? DiagnosticoIniciadoEm { get; private set; }
    public DateTime? ReparoIniciadoEm { get; private set; }
    public DateTime? FinalizadaEm { get; private set; }

    public static Execucao Criar(Guid ordemServicoUniqueCode)
    {
        if (ordemServicoUniqueCode == Guid.Empty)
            throw new DomainException("OrdemServicoUniqueCode inválido.");

        return new Execucao
        {
            Id = Guid.NewGuid(),
            OrdemServicoUniqueCode = ordemServicoUniqueCode,
            Status = StatusExecucao.Pendente,
            CriadaEm = DateTime.UtcNow
        };
    }

    public void IniciarDiagnostico()
    {
        Garantir(StatusExecucao.Pendente, StatusExecucao.EmDiagnostico);
        Status = StatusExecucao.EmDiagnostico;
        DiagnosticoIniciadoEm = DateTime.UtcNow;
    }

    public void IniciarReparo()
    {
        Garantir(StatusExecucao.EmDiagnostico, StatusExecucao.EmReparo);
        Status = StatusExecucao.EmReparo;
        ReparoIniciadoEm = DateTime.UtcNow;
    }

    public void Finalizar()
    {
        Garantir(StatusExecucao.EmReparo, StatusExecucao.Finalizada);
        Status = StatusExecucao.Finalizada;
        FinalizadaEm = DateTime.UtcNow;
    }

    private void Garantir(StatusExecucao esperado, StatusExecucao destino)
    {
        if (Status != esperado)
            throw new DomainException($"Transição inválida de {Status} para {destino}.");
    }
}
