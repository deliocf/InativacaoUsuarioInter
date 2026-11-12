using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Entrada.Modelos;
using InativacaoUsuarioInter.Service.Inativacao;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Tests.Comum;

namespace InativacaoUsuarioInter.Tests.Contract.WAccess;

/// <summary>
/// T029 + feature 002 T031 — contrato WAccess v2 (specs/002-ajustes-pastas-fluxo/contracts/waccess-inativacao.md),
/// contra SQL Server real: o passo B grava <c>CHState = 1</c> e <c>CHDownloadRequired = 1</c>.
/// </summary>
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
        var passoB = anterior is EstadoAnterior.Ativo or EstadoAnterior.Inativo
            ? await repositorio.InativarAsync(cpf, null, TestContext.Current.CancellationToken)
            : (ResultadoInativacao?)null;
        return ClassificacaoWAccess.Classificar(anterior, passoB);
    }

    [Fact]
    public async Task Inv1_e_2_so_CHState_e_CHDownloadRequired_do_cpf_alvo_mudam()
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
            var esperado = chid == alvo ? linha.Replace("|0|<null>|", "|1|True|", StringComparison.Ordinal) : linha;
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

        Assert.Equal(new ResultadoInativacao(1, 0, 1), resultado);
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
        Assert.Equal(new ResultadoInativacao(0, 0, 1), repetido);
        Assert.Equal(ResultadoRegistro.Inativado, ClassificacaoWAccess.Classificar(anterior, repetido));
    }

    [Fact]
    public async Task Inv8_usuario_ja_inativo_e_ja_marcado_nunca_recebe_update()
    {
        var ct = TestContext.Current.CancellationToken;
        var banco = await sql.CriarBancoAsync(ct);
        await banco.InserirUsuarioAsync("22222222222", 1, downloadRequired: true, cancelamento: ct);

        Assert.Equal(ResultadoRegistro.JaInativo, await ExecutarAsync(Repositorio(banco), "22222222222"));
        Assert.Equal(0, await banco.ContarHistoricoAsync(ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Inv9_ja_inativo_sem_marca_recebe_so_a_marca_e_continua_JA_INATIVO(bool? marcaAnterior)
    {
        // Feature 002, FR-015 / US4 cenário 3: cadastro inativado antes desta mudança.
        var ct = TestContext.Current.CancellationToken;
        var banco = await sql.CriarBancoAsync(ct);
        var alvo = await banco.InserirUsuarioAsync("22222222222", 1, downloadRequired: marcaAnterior, cancelamento: ct);
        var antes = await banco.ObterSnapshotAsync(ct);
        var repositorio = Repositorio(banco);

        Assert.Equal(ResultadoRegistro.JaInativo, await ExecutarAsync(repositorio, "22222222222"));
        var resultado = await repositorio.InativarAsync("22222222222", null, ct);

        Assert.Equal(new ResultadoInativacao(0, 0, 1), resultado);
        Assert.Equal([((short)1, (bool?)true)], await banco.ObterEstadosDownloadAsync("22222222222", ct));
        var anteriorTexto = marcaAnterior is null ? "<null>" : "False";
        Assert.Equal(antes[alvo].Replace($"|1|{anteriorTexto}|", "|1|True|", StringComparison.Ordinal), (await banco.ObterSnapshotAsync(ct))[alvo]);
    }

    [Fact]
    public async Task Inv10_somente_linhas_do_predicado_mudam_em_todas_as_combinacoes()
    {
        var ct = TestContext.Current.CancellationToken;
        var banco = await sql.CriarBancoAsync(ct);
        foreach (short estado in new short[] { 0, 1, 2, 8 })
        {
            foreach (var marca in new bool?[] { null, false, true })
            {
                await banco.InserirUsuarioAsync("22222222222", estado, downloadRequired: marca, cancelamento: ct);
            }
        }

        await banco.InserirUsuarioAsync("33333333333", 0, cancelamento: ct);
        var antes = await banco.ObterSnapshotAsync(ct);

        var resultado = await Repositorio(banco).InativarAsync("22222222222", null, ct);

        // 12 linhas do CPF: 9 com CHState <> 1 (inativadas) + 2 já inativas sem marca (só a marca); 1 intocada.
        Assert.Equal(new ResultadoInativacao(9, 2, 12), resultado);
        Assert.All(await banco.ObterEstadosDownloadAsync("22222222222", ct), l => Assert.Equal(((short)1, (bool?)true), l));
        var depois = await banco.ObterSnapshotAsync(ct);
        Assert.Equal(11, await banco.ContarHistoricoAsync(ct));
        foreach (var (chid, linha) in antes.Where(l => !l.Value.Contains("|22222222222|", StringComparison.Ordinal)))
        {
            Assert.Equal(linha, depois[chid]);
        }
    }

    [Fact]
    public async Task Cpf_com_dois_cadastros_inativa_os_dois_r02()
    {
        var ct = TestContext.Current.CancellationToken;
        var banco = await sql.CriarBancoAsync(ct);
        await banco.InserirUsuarioAsync("22222222222", 0, 1, cancelamento: ct);
        await banco.InserirUsuarioAsync("22222222222", 2, 2, cancelamento: ct);

        var repositorio = Repositorio(banco);
        var resultado = await repositorio.InativarAsync("22222222222", null, ct);

        Assert.Equal(new ResultadoInativacao(2, 0, 2), resultado);
        Assert.Equal([(short)1, (short)1], await banco.ObterEstadosAsync("22222222222", ct));
    }

    [Fact]
    public void Comandos_usam_o_texto_exato_do_contrato()
    {
        var sql = RepositorioWAccessSql.SqlInativacao;
        Assert.Contains("UPDATE dbo.CHMain SET CHState = 1, CHDownloadRequired = 1", sql, StringComparison.Ordinal);
        Assert.Contains("OUTPUT inserted.CHID, deleted.CHState INTO @afetados", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE IdNumber = @Cpf", sql, StringComparison.Ordinal);
        Assert.Contains("AND (CHState <> 1 OR CHDownloadRequired IS NULL OR CHDownloadRequired = 0)", sql, StringComparison.Ordinal);
        Assert.Contains("AS Inativados", sql, StringComparison.Ordinal);
        Assert.Contains("AS SomenteDownload", sql, StringComparison.Ordinal);
        Assert.Contains("AS Existentes", sql, StringComparison.Ordinal);
        Assert.Contains("SUM(CASE WHEN CHState <> 1 THEN 1 ELSE 0 END) AS Ativos", RepositorioWAccessSql.SqlPreLeitura, StringComparison.Ordinal);
    }
}
