using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Inativacao;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Tests.Comum;
using Microsoft.Extensions.DependencyInjection;

namespace InativacaoUsuarioInter.Tests.Unit.Ciclo;

/// <summary>
/// T062 — classificação na retomada com <c>estado_anterior</c> gravado (análise H1) e recuperação de log
/// exatamente uma vez (R-08). O WAccess é substituído por um repositório falso que conta as chamadas;
/// o comportamento real do banco é coberto pelos testes de contrato.
/// </summary>
public sealed class RecuperacaoTests : IDisposable
{
    private readonly AmbienteTeste _ambiente = AmbienteTeste.Criar("Server=nao-usado");

    private sealed class RepositorioFalso(EstadoAnterior estado) : IRepositorioWAccess
    {
        public int Leituras { get; private set; }

        public int Inativacoes { get; private set; }

        public Exception? FalharLeitura { get; set; }

        public Exception? FalharInativacao { get; set; }

        public ResultadoInativacao Resultado { get; set; } = new(1, 1);

        public Task<EstadoAnterior> LerEstadoAsync(string cpf, OrcamentoCiclo? orcamento, CancellationToken cancelamento)
        {
            Leituras++;
            return FalharLeitura is { } erro ? Task.FromException<EstadoAnterior>(erro) : Task.FromResult(estado);
        }

        public Task<ResultadoInativacao> InativarAsync(string cpf, OrcamentoCiclo? orcamento, CancellationToken cancelamento)
        {
            Inativacoes++;
            return FalharInativacao is { } erro ? Task.FromException<ResultadoInativacao>(erro) : Task.FromResult(Resultado);
        }
    }

    private async Task CicloAsync(RepositorioFalso repositorio, IPontoFalha? ponto = null)
    {
        await using var servicos = _ambiente.CriarServicos(s =>
        {
            s.AddSingleton<IRepositorioWAccess>(repositorio);
            s.AddSingleton(ponto ?? new PontoFalhaNulo());
        });
        await servicos.GetRequiredService<IOrquestradorCiclo>().ExecutarCicloAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EmExecucao_ativo_com_zero_linhas_no_passo_B_eh_INATIVADO_sem_nova_pre_leitura()
    {
        _ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        var repositorio = new RepositorioFalso(EstadoAnterior.Ativo) { FalharInativacao = new TimeoutException() };
        await CicloAsync(repositorio);
        Assert.Empty(_ambiente.LinhasLog("Auditoria"));

        repositorio.FalharInativacao = null;
        repositorio.Resultado = new ResultadoInativacao(0, 1);
        await CicloAsync(repositorio);

        Assert.Equal(1, repositorio.Leituras);
        Assert.Equal(2, repositorio.Inativacoes);
        Assert.EndsWith("status=INATIVADO", Assert.Single(_ambiente.LinhasLog("Auditoria")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmExecucao_inativo_eh_JA_INATIVO_sem_chamar_InativarAsync()
    {
        _ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        var repositorio = new RepositorioFalso(EstadoAnterior.Inativo);
        await Assert.ThrowsAsync<InterrupcaoSimuladaException>(() => CicloAsync(repositorio, new PontoFalhaEmProcesso(PontoFalha.AposIntencao)));

        await CicloAsync(repositorio);

        Assert.Equal(1, repositorio.Leituras);
        Assert.Equal(0, repositorio.Inativacoes);
        Assert.EndsWith("status=JA_INATIVO", Assert.Single(_ambiente.LinhasLog("Auditoria")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmExecucao_inexistente_eh_CPF_NAO_ENCONTRADO()
    {
        _ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        var repositorio = new RepositorioFalso(EstadoAnterior.Inexistente);
        await Assert.ThrowsAsync<InterrupcaoSimuladaException>(() => CicloAsync(repositorio, new PontoFalhaEmProcesso(PontoFalha.AposIntencao)));

        await CicloAsync(repositorio);

        Assert.Equal(0, repositorio.Inativacoes);
        Assert.EndsWith("causa=CPF_NAO_ENCONTRADO", Assert.Single(_ambiente.LinhasLog("Falhas")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pendente_antes_da_intencao_recomeca_pela_pre_leitura()
    {
        _ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        var repositorio = new RepositorioFalso(EstadoAnterior.Ativo) { FalharLeitura = new TimeoutException() };
        await CicloAsync(repositorio);

        repositorio.FalharLeitura = null;
        await CicloAsync(repositorio);

        Assert.Equal(2, repositorio.Leituras);
        Assert.Equal(1, repositorio.Inativacoes);
        Assert.Single(_ambiente.LinhasLog("Auditoria"));
    }

    [Fact]
    public async Task Linha_ja_presente_no_log_apenas_eh_marcada()
    {
        _ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        var repositorio = new RepositorioFalso(EstadoAnterior.Ativo);
        await Assert.ThrowsAsync<InterrupcaoSimuladaException>(() => CicloAsync(repositorio, new PontoFalhaEmProcesso(PontoFalha.AposAppendLog)));
        Assert.Single(_ambiente.LinhasLog("Auditoria"));

        await CicloAsync(repositorio);

        Assert.Single(_ambiente.LinhasLog("Auditoria"));
        Assert.Equal(1, repositorio.Inativacoes);
    }

    [Fact]
    public async Task Linha_ausente_no_log_eh_anexada_uma_vez()
    {
        _ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        var repositorio = new RepositorioFalso(EstadoAnterior.Ativo);
        await Assert.ThrowsAsync<InterrupcaoSimuladaException>(() => CicloAsync(repositorio, new PontoFalhaEmProcesso(PontoFalha.AposRegistrarResultado)));
        Assert.Empty(_ambiente.LinhasLog("Auditoria"));

        await CicloAsync(repositorio);
        await CicloAsync(repositorio);

        Assert.Single(_ambiente.LinhasLog("Auditoria"));
        Assert.Equal(1, repositorio.Inativacoes);
    }

    [Theory]
    [InlineData(EstadoAnterior.Ativo, 1, 1, ResultadoRegistro.Inativado)]
    [InlineData(EstadoAnterior.Ativo, 0, 1, ResultadoRegistro.Inativado)]
    [InlineData(EstadoAnterior.Ativo, 0, 0, ResultadoRegistro.CpfNaoEncontrado)]
    public void Classificacao_com_passo_B(EstadoAnterior anterior, int inativados, int existentes, ResultadoRegistro esperado) =>
        Assert.Equal(esperado, ClassificacaoWAccess.Classificar(anterior, new ResultadoInativacao(inativados, existentes)));

    [Fact]
    public void Classificacao_sem_passo_B()
    {
        Assert.Equal(ResultadoRegistro.JaInativo, ClassificacaoWAccess.Classificar(EstadoAnterior.Inativo, null));
        Assert.Equal(ResultadoRegistro.CpfNaoEncontrado, ClassificacaoWAccess.Classificar(EstadoAnterior.Inexistente, null));
        Assert.Throws<InvalidOperationException>(() => ClassificacaoWAccess.Classificar(EstadoAnterior.Ativo, null));
    }

    public void Dispose() => _ambiente.Dispose();
}
