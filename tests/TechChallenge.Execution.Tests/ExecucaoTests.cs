using TechChallenge.Execution.Domain;

namespace TechChallenge.Execution.Tests;

public class ExecucaoTests
{
    private static Execucao Nova() => Execucao.Criar(Guid.NewGuid());

    [Fact]
    public void Criar_DeveIniciarPendente() =>
        Assert.Equal(StatusExecucao.Pendente, Nova().Status);

    [Fact]
    public void Criar_ComGuidVazio_Falha() =>
        Assert.Throws<DomainException>(() => Execucao.Criar(Guid.Empty));

    [Fact]
    public void FluxoCompleto_Funciona()
    {
        var e = Nova();
        e.IniciarDiagnostico();
        Assert.Equal(StatusExecucao.EmDiagnostico, e.Status);
        e.IniciarReparo();
        Assert.Equal(StatusExecucao.EmReparo, e.Status);
        e.Finalizar();
        Assert.Equal(StatusExecucao.Finalizada, e.Status);
        Assert.NotNull(e.FinalizadaEm);
    }

    [Fact]
    public void IniciarReparo_SemDiagnostico_Falha() =>
        Assert.Throws<DomainException>(() => Nova().IniciarReparo());

    [Fact]
    public void Finalizar_Pendente_Falha() =>
        Assert.Throws<DomainException>(() => Nova().Finalizar());

    [Fact]
    public void Finalizar_EmDiagnostico_Falha()
    {
        var e = Nova();
        e.IniciarDiagnostico();
        Assert.Throws<DomainException>(() => e.Finalizar());
    }

    [Fact]
    public void IniciarDiagnostico_Duas_Vezes_Falha()
    {
        var e = Nova();
        e.IniciarDiagnostico();
        Assert.Throws<DomainException>(() => e.IniciarDiagnostico());
    }

    [Fact]
    public void Finalizada_NaoAceitaNovasTransicoes()
    {
        var e = Nova();
        e.IniciarDiagnostico(); e.IniciarReparo(); e.Finalizar();
        Assert.Throws<DomainException>(() => e.Finalizar());
        Assert.Throws<DomainException>(() => e.IniciarReparo());
    }
}
