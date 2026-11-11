using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Entrada.Modelos;
using InativacaoUsuarioInter.Service.Inativacao;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Tests.Comum;

namespace InativacaoUsuarioInter.Tests.Contract.WAccess;

/// <summary>T029 — contrato WAccess (contracts/waccess-inativacao.md, invariantes 1–8), contra SQL Server real.</summary>
public sealed class InativacaoWAccessTests(SqlServerFixture sql)
{
    private static RepositorioWAccessSql Repositorio(BancoTeste banco)
    {
        var configuracao = new ConfiguracaoServico
        {
            Instancia = Instancia.Inter,
            IntervaloSegundos = 10,
            RaizInter = Path.GetTempPath(),
            RaizCondominio = Path.GetTempPath(),
            ConexaoWAccess = banco.StringConexao,
            DiretorioEstado = Path.GetTempPath(),
            Processamento = new ProcessamentoOptions(),
            Resiliencia = new ResilienciaOptions(),
        };
        return new RepositorioWAccessSql(configuracao, new ProvedorPipelines(configuracao));
    }

    private static async Task<ResultadoRegistro> ExecutarAsync(RepositorioWAccessSql repositorio, string cpf)
    {
        var anterior = await repositorio.LerEstadoAsync(cpf, null, TestContext.Current.CancellationToken);
        var passoB = anterior == EstadoAnterior.Ativo
            ? await repositorio.InativarAsync(cpf, null, TestContext.Current.CancellationToken)
            : (ResultadoInativacao?)null;
        return ClassificacaoWAccess.Classificar(anterior, passoB);
    }

    [Fact]
    public async Task Inv1_e_2_so_CHState_do_cpf_alvo_muda()
    {
        var ct = TestContext.Current.CancellationToken;
        var banco = await sql.CriarBancoAsync(ct);
        var alvo = await banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        await banco.InserirUsuarioAsync("33333333333", 0, cancelamento: ct);
        await banco.InserirUsuarioAsync("44444444444", 2, cancelamento: ct);
        var antes = await banco.ObterSnapshotAsync(ct);

        Assert.Equal(ResultadoRegistro.Inativado, await ExecutarAsync(Repositorio(banco), "22222222222"));

        var depois = await banco.ObterSnapshotAsync(ct);
        foreach (var (chid, linha) in antes)
        {
            var esperado = chid == alvo ? linha.Replace("|0|", "|1|", StringComparison.Ordinal) : linha;
            Assert.Equal(esperado, depois[chid]);
        }
    }

    [Fact]
    public async Task Inv3_zeros_a_esquerda_sao_preservados()
    {
        var ct = TestContext.Current.CancellationToken;
        var banco = await sql.CriarBancoAsync(ct);
        await banco.InserirUsuarioAsync("1234567890", 0, cancelamento: ct);

        Assert.Equal(ResultadoRegistro.CpfNaoEncontrado, await ExecutarAsync(Repositorio(banco), "01234567890"));
        Assert.Equal([(short)0], await banco.ObterEstadosAsync("1234567890", ct));
    }

    [Fact]
    public async Task Inv4_entrada_maliciosa_vai_como_parametro_sem_efeito()
    {
        var ct = TestContext.Current.CancellationToken;
        var banco = await sql.CriarBancoAsync(ct);
        await banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);

        Assert.Equal(ResultadoRegistro.CpfNaoEncontrado, await ExecutarAsync(Repositorio(banco), "1' OR '1'='1"));
        Assert.Equal(ResultadoRegistro.CpfNaoEncontrado, await ExecutarAsync(Repositorio(banco), "1'; DROP TABLE CHMain;--"));
        Assert.Equal([(short)0], await banco.ObterEstadosAsync("22222222222", ct));
    }

    [Fact]
    public async Task Inv5_contagem_correta_com_trigger_sem_nocount()
    {
        var ct = TestContext.Current.CancellationToken;
        var banco = await sql.CriarBancoAsync(ct);
        await banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);

        var resultado = await Repositorio(banco).InativarAsync("22222222222", null, ct);

        Assert.Equal(new ResultadoInativacao(1, 1), resultado);
        Assert.Equal(1, await banco.ContarHistoricoAsync(ct));
    }

    [Fact]
    public async Task Inv6_execucao_dupla_inativado_e_depois_ja_inativo()
    {
        var ct = TestContext.Current.CancellationToken;
        var banco = await sql.CriarBancoAsync(ct);
        await banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        var repositorio = Repositorio(banco);

        Assert.Equal(ResultadoRegistro.Inativado, await ExecutarAsync(repositorio, "22222222222"));
        Assert.Equal(ResultadoRegistro.JaInativo, await ExecutarAsync(repositorio, "22222222222"));
        Assert.Equal(1, await banco.ContarHistoricoAsync(ct));
    }

    [Fact]
    public async Task Inv7_passo_B_repetido_com_estado_anterior_ativo_continua_inativado()
    {
        var ct = TestContext.Current.CancellationToken;
        var banco = await sql.CriarBancoAsync(ct);
        await banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        var repositorio = Repositorio(banco);
        var anterior = await repositorio.LerEstadoAsync("22222222222", null, ct);
        await repositorio.InativarAsync("22222222222", null, ct);

        var repetido = await repositorio.InativarAsync("22222222222", null, ct);

        Assert.Equal(EstadoAnterior.Ativo, anterior);
        Assert.Equal(new ResultadoInativacao(0, 1), repetido);
        Assert.Equal(ResultadoRegistro.Inativado, ClassificacaoWAccess.Classificar(anterior, repetido));
    }

    [Fact]
    public async Task Inv8_usuario_ja_inativo_nunca_recebe_update()
    {
        var ct = TestContext.Current.CancellationToken;
        var banco = await sql.CriarBancoAsync(ct);
        await banco.InserirUsuarioAsync("22222222222", 1, cancelamento: ct);

        Assert.Equal(ResultadoRegistro.JaInativo, await ExecutarAsync(Repositorio(banco), "22222222222"));
        Assert.Equal(0, await banco.ContarHistoricoAsync(ct));
    }

    [Fact]
    public async Task Cpf_com_dois_cadastros_inativa_os_dois_r02()
    {
        var ct = TestContext.Current.CancellationToken;
        var banco = await sql.CriarBancoAsync(ct);
        await banco.InserirUsuarioAsync("22222222222", 0, 1, ct);
        await banco.InserirUsuarioAsync("22222222222", 2, 2, ct);

        var repositorio = Repositorio(banco);
        var resultado = await repositorio.InativarAsync("22222222222", null, ct);

        Assert.Equal(new ResultadoInativacao(2, 2), resultado);
        Assert.Equal([(short)1, (short)1], await banco.ObterEstadosAsync("22222222222", ct));
    }

    [Fact]
    public void Comandos_usam_o_texto_exato_do_contrato()
    {
        Assert.Contains("WHERE IdNumber = @Cpf AND CHState <> 1", RepositorioWAccessSql.SqlInativacao, StringComparison.Ordinal);
        Assert.Contains("OUTPUT inserted.CHID INTO @afetados", RepositorioWAccessSql.SqlInativacao, StringComparison.Ordinal);
        Assert.Contains("SUM(CASE WHEN CHState <> 1 THEN 1 ELSE 0 END) AS Ativos", RepositorioWAccessSql.SqlPreLeitura, StringComparison.Ordinal);
    }
}
