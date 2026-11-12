using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Tests.Comum;
using Microsoft.Extensions.DependencyInjection;

namespace InativacaoUsuarioInter.Tests.Integration.Migracao;

/// <summary>
/// Feature 002, T010/T011 — migração automática das pastas com nomes antigos (FR-004, SC-002a,
/// research R-06, contracts/estrutura-pastas.md). Este arquivo usa os nomes antigos de propósito.
/// </summary>
public sealed class MigracaoLegadoTests(SqlServerFixture sql)
{
    private const string Traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    private Task<Cenario> CriarAsync(string instancia, Action<IServiceCollection>? servicos = null) =>
        Cenario.CriarAsync(sql, instancia, ajustarServicos: servicos, cancelamento: TestContext.Current.CancellationToken);

    /// <summary>Raiz com as quatro pastas antigas populadas, como a versão anterior deixaria.</summary>
    private static void SemearLegado(AmbienteTeste ambiente)
    {
        ambiente.CriarLegado("Imput", "a.txt", "22222222222,1,Maria\r\n");
        ambiente.CriarLegado("Imput", "a.txt.traceparent", Traceparent);
        ambiente.CriarLegado("Processados", "antigo.json", "[]");
        ambiente.CriarLegado("Falhas", "x__falhas-12345678.txt", "99,1,Ruim\r\n");
        ambiente.CriarLegado("Falhas", "log_inativacao.log", "linha-falha-antiga\r\n");
        ambiente.CriarLegado("Auditoria", "log_inativacao.log", "linha-auditoria-antiga\r\n");
    }

    [Theory]
    [InlineData("Inter")]
    [InlineData("Condominio")]
    public async Task Migra_tudo_processa_a_entrada_no_mesmo_ciclo_e_remove_as_pastas_antigas(string instancia)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync(instancia);
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        var ambiente = cenario.Ambiente;
        SemearLegado(ambiente);

        await cenario.CicloAsync(ct);
        await cenario.CicloAsync(ct);

        foreach (var antiga in new[] { "Imput", "Processados", "Falhas", "Auditoria" })
        {
            Assert.False(Directory.Exists(ambiente.Pasta(antiga)), $"{antiga} deveria ter sido removida.");
        }

        Assert.Equal([(short)1], await cenario.Banco.ObterEstadosAsync("22222222222", ct));
        Assert.Contains("a.txt", ambiente.Arquivos("Processed"));
        Assert.Contains("antigo.json", ambiente.Arquivos("Processed"));
        Assert.Contains("x__falhas-12345678.txt", ambiente.Arquivos("Fail"));
        Assert.Empty(ambiente.Arquivos("Input"));

        var auditoria = ambiente.LinhasLog("Logs");
        Assert.Equal(2, auditoria.Length);
        Assert.Equal("linha-auditoria-antiga", auditoria[0]);
        Assert.Contains("arquivo=a.txt", auditoria[1], StringComparison.Ordinal);
        Assert.Equal(["linha-falha-antiga"], ambiente.LinhasLog("Fail"));
    }

    [Fact]
    public async Task Sidecar_traceparent_acompanha_o_arquivo_migrado()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync("Condominio");
        var ambiente = cenario.Ambiente;
        ambiente.CriarLegado("Imput", "b.txt", "77777777777,1,Sem cadastro\r\n");
        ambiente.CriarLegado("Imput", "b.txt.traceparent", Traceparent);

        await cenario.CicloAsync(ct);

        // CPF inexistente: o arquivo de falhas leva o sidecar consigo (FR-012 + correlação da spec 001).
        Assert.Contains("b.txt", ambiente.Arquivos("Fail"));
        Assert.Contains("b.txt.traceparent", ambiente.Arquivos("Fail"));
        Assert.False(Directory.Exists(ambiente.Pasta("Imput")));
    }

    [Fact]
    public async Task Colisao_com_mesmo_conteudo_eh_no_op_e_com_conteudo_diferente_recebe_nome_unico()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync("Condominio");
        var ambiente = cenario.Ambiente;
        Directory.CreateDirectory(ambiente.Pasta("Processed"));
        await File.WriteAllTextAsync(ambiente.Pasta("Processed", "igual.json"), "[]", ct);
        await File.WriteAllTextAsync(ambiente.Pasta("Processed", "diferente.json"), "[1]", ct);
        ambiente.CriarLegado("Processados", "igual.json", "[]");
        ambiente.CriarLegado("Processados", "diferente.json", "[2]");

        await cenario.CicloAsync(ct);

        var processados = ambiente.Arquivos("Processed");
        Assert.Equal(3, processados.Length);
        Assert.Contains("igual.json", processados);
        Assert.Contains("diferente.json", processados);
        Assert.Single(processados, p => p.StartsWith("diferente__", StringComparison.Ordinal));
        Assert.Equal("[1]", await File.ReadAllTextAsync(ambiente.Pasta("Processed", "diferente.json"), ct));
        Assert.False(Directory.Exists(ambiente.Pasta("Processados")));
    }

    [Fact]
    public async Task Temporario_em_transito_nao_eh_tocado_e_a_pasta_antiga_permanece()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync("Condominio");
        var ambiente = cenario.Ambiente;
        ambiente.CriarLegado("Imput", "em-transito.txt.tmp", "parcial");

        await cenario.CicloAsync(ct);
        await cenario.CicloAsync(ct);

        Assert.Equal(["em-transito.txt.tmp"], ambiente.Arquivos("Imput"));
        Assert.Empty(ambiente.Arquivos("Input"));
    }

    [Fact]
    public async Task Entrega_tardia_de_um_Inter_antigo_em_Imput_eh_migrada_no_ciclo_seguinte()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync("Condominio");
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        await cenario.CicloAsync(ct);

        cenario.Ambiente.CriarLegado("Imput", "tardio.txt", "22222222222,1,Maria\r\n");
        await cenario.CicloAsync(ct);

        Assert.Equal([(short)1], await cenario.Banco.ObterEstadosAsync("22222222222", ct));
        Assert.Contains("tardio.txt", cenario.Ambiente.Arquivos("Processed"));
    }

    [Fact]
    public async Task Registro_ja_concluido_antes_da_migracao_nao_eh_reinativado_nem_reauditado()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync("Condominio");
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        await cenario.CicloAsync(ct);
        var historico = await cenario.Banco.ContarHistoricoAsync(ct);

        // O mesmo conteúdo reaparece numa pasta antiga (ex.: cópia que ficou para trás): o diário reconhece o hash.
        cenario.Ambiente.CriarLegado("Imput", "a.txt", "22222222222,1,Maria\r\n");
        await cenario.CicloAsync(ct);
        await cenario.CicloAsync(ct);

        Assert.Single(cenario.Ambiente.LinhasLog("Logs"));
        Assert.Equal(historico, await cenario.Banco.ContarHistoricoAsync(ct));
        Assert.Empty(cenario.Ambiente.Arquivos("Input"));
    }

    [Theory]
    [InlineData(PontoFalha.AposMoverLegado, 1)]
    [InlineData(PontoFalha.AposMoverLegado, 3)]
    [InlineData(PontoFalha.AposSubstituirLogLegado, 1)]
    [InlineData(PontoFalha.AposSubstituirLogLegado, 2)]
    public async Task Crash_durante_a_migracao_e_retomada_nao_duplica_nem_perde(PontoFalha ponto, int ocorrencia)
    {
        var ct = TestContext.Current.CancellationToken;
        var gatilho = new PontoFalhaEmProcesso(ponto, ocorrencia);
        await using var cenario = await CriarAsync("Inter", s => s.AddSingleton<IPontoFalha>(gatilho));
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        var ambiente = cenario.Ambiente;
        SemearLegado(ambiente);
        ambiente.CriarLegado("Processados", "outro.json", "[]");
        ambiente.CriarLegado("Falhas", "y.txt", "1,1,Y\r\n");

        await Assert.ThrowsAsync<InterrupcaoSimuladaException>(() => cenario.CicloAsync(ct));
        Assert.True(gatilho.Disparado);

        await cenario.ReiniciarAsync(s => s.AddSingleton<IPontoFalha>(new PontoFalhaNulo()));
        await cenario.CicloAsync(ct);
        await cenario.CicloAsync(ct);

        Assert.Equal(["antigo.json", "outro.json"], ambiente.Arquivos("Processed").Where(a => a.EndsWith(".json", StringComparison.Ordinal)));
        Assert.Equal(["x__falhas-12345678.txt", "y.txt"], ambiente.Arquivos("Fail").Where(a => a.EndsWith(".txt", StringComparison.Ordinal)));
        Assert.Equal(["linha-falha-antiga"], ambiente.LinhasLog("Fail"));
        var auditoria = ambiente.LinhasLog("Logs");
        Assert.Equal(2, auditoria.Length);
        Assert.Equal("linha-auditoria-antiga", auditoria[0]);
        Assert.Equal([(short)1], await cenario.Banco.ObterEstadosAsync("22222222222", ct));
        foreach (var antiga in new[] { "Imput", "Processados", "Falhas", "Auditoria" })
        {
            Assert.False(Directory.Exists(ambiente.Pasta(antiga)), $"{antiga} deveria ter sido removida.");
        }
    }

    [Fact]
    public async Task Arquivo_bloqueado_na_pasta_antiga_eh_transitorio_e_migrado_quando_liberado()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Bloqueio de compartilhamento só impede o move no Windows.
        }

        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync("Condominio");
        var ambiente = cenario.Ambiente;
        var caminho = ambiente.CriarLegado("Processados", "bloqueado.json", "[]");

        using (new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var resumo = await cenario.CicloAsync(ct);
            Assert.Equal(0, resumo.ArquivosComErro);
            Assert.True(File.Exists(caminho));
        }

        await cenario.CicloAsync(ct);

        Assert.Equal(["bloqueado.json"], ambiente.Arquivos("Processed"));
        Assert.False(Directory.Exists(ambiente.Pasta("Processados")));
    }
}
