using System.Diagnostics;
using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Estado;
using InativacaoUsuarioInter.Tests.Comum;
using Microsoft.Extensions.DependencyInjection;

namespace InativacaoUsuarioInter.Tests.Integration.FluxoCondominio;

/// <summary>T081 + feature 002 T038 — fluxo do Condomínio (US3: Input → Processed, falhas em {FolderPathCondominio}/Fail, FR-012, FR-013).</summary>
public sealed class FluxoCondominioTests(SqlServerFixture sql)
{
    private Task<Cenario> CriarAsync(Action<IServiceCollection>? servicos = null, Func<BancoTeste, string>? conexao = null) =>
        Cenario.CriarAsync(sql, "Condominio", ajustarServicos: servicos, conexao: conexao, cancelamento: TestContext.Current.CancellationToken);

    [Fact]
    public async Task CA11_CA12_le_so_o_Input_do_condominio_audita_localmente_e_conclui_em_Processed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync();
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        cenario.Ambiente.Depositar("20261111103000__sucesso-1a2b3c4d.txt", "22222222222,1,Maria\r\n");
        cenario.Ambiente.Depositar("ignorado.txt", "22222222222,1,Maria\r\n", cenario.Ambiente.RaizInter);

        await cenario.CicloAsync(ct);

        Assert.Equal([(short)1], await cenario.Banco.ObterEstadosAsync("22222222222", ct));
        Assert.Contains("| Condominio | arquivo=20261111103000__sucesso-1a2b3c4d.txt |", Assert.Single(cenario.Ambiente.LinhasLog("Logs")), StringComparison.Ordinal);
        Assert.Equal(["20261111103000__sucesso-1a2b3c4d.txt"], cenario.Ambiente.Arquivos("Processed"));
        Assert.False(Directory.Exists(cenario.Ambiente.Pasta("Output")));
        Assert.Equal(["ignorado.txt"], cenario.Ambiente.Arquivos("Input", cenario.Ambiente.RaizInter));
        Assert.False(Directory.Exists(Path.Combine(cenario.Ambiente.RaizInter, "Logs")));
        Assert.Equal(0, await cenario.Obter<IArmazenamentoEstado>().ContarEntregasPendentesAsync(ct));
    }

    [Fact]
    public async Task Bug_entrega_com_conteudo_ja_processado_e_outro_nome_reinativa_cadastro_reativado()
    {
        // Bug ja-inativo-sem-replicacao: o Inter reentrega a mesma parte (mesmos bytes) com outro nome; o Condomínio
        // precisa processá-la de novo, porque o cadastro pode ter voltado a CHState = 0.
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync();
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        cenario.Ambiente.Depositar("20261112111122__sucesso-95990873.txt", "22222222222,3,Delio\r\n");
        await cenario.CicloAsync(ct);
        Assert.Equal([(short)1], await cenario.Banco.ObterEstadosAsync("22222222222", ct));

        await cenario.Banco.ExecutarAsync("UPDATE dbo.CHMain SET CHState = 0 WHERE IdNumber = '22222222222';", ct);
        cenario.Ambiente.Depositar("20261112111333__sucesso-95990873.txt", "22222222222,3,Delio\r\n");
        await cenario.CicloAsync(ct);

        Assert.Equal([(short)1], await cenario.Banco.ObterEstadosAsync("22222222222", ct));
        var linhas = cenario.Ambiente.LinhasLog("Logs");
        Assert.Equal(2, linhas.Length);
        Assert.Contains("arquivo=20261112111333__sucesso-95990873.txt", linhas[1], StringComparison.Ordinal);
        Assert.EndsWith("status=INATIVADO", linhas[1], StringComparison.Ordinal);
        Assert.Empty(cenario.Ambiente.Arquivos("Input"));
        Assert.Equal(
            ["20261112111122__sucesso-95990873.txt", "20261112111333__sucesso-95990873.txt"],
            cenario.Ambiente.Arquivos("Processed").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Cpf_inexistente_no_condominio_vai_para_o_Fail_do_condominio_com_o_nome_original()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync();
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n44444444444,2,Inexistente\r\n");

        await cenario.CicloAsync(ct);

        Assert.EndsWith("| Condominio | arquivo=a.txt | cpf=44444444444 | causa=CPF_NAO_ENCONTRADO", Assert.Single(cenario.Ambiente.LinhasLog("Fail")), StringComparison.Ordinal);
        Assert.Single(cenario.Ambiente.Arquivos("Fail"), a => a == "a.txt");
        Assert.Equal("44444444444,2,Inexistente\r\n", await File.ReadAllTextAsync(cenario.Ambiente.Pasta("Fail", "a.txt"), ct));
        Assert.False(Directory.Exists(Path.Combine(cenario.Ambiente.RaizInter, "Fail")), "O Condomínio não grava na raiz do Inter (FR-012).");
        Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Processed"));
        Assert.Equal("22222222222,1,Maria\r\n", await File.ReadAllTextAsync(cenario.Ambiente.Pasta("Processed", "a.txt"), ct));
    }

    [Fact]
    public async Task Parcial_por_falha_transitoria_vai_para_Processed_e_pendentes_ficam_na_entrada()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var proxy = ProxyTcpFalhas.Iniciar(sql.Host, sql.Porta);
        await using var cenario = await CriarAsync(
            s => s.AddSingleton<IPontoFalha>(new GatilhoPonto(PontoFalha.AposRegistrarResultado, 1, proxy.Cortar)),
            b => b.StringConexaoVia(proxy.Porta));
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        await cenario.Banco.InserirUsuarioAsync("33333333333", 0, cancelamento: ct);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n33333333333,2,Carlos\r\n");

        await cenario.CicloAsync(ct);

        Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Processed"));
        Assert.Single(cenario.Ambiente.Arquivos("Input"), a => a.StartsWith("a__pendente-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Traceparent_do_Inter_vira_pai_do_span_e_acompanha_o_arquivo()
    {
        var ct = TestContext.Current.CancellationToken;
        using var telemetria = new ColetorTelemetria();
        await using var cenario = await CriarAsync();
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        var arquivo = cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        var trace = ActivityTraceId.CreateRandom();
        var span = ActivitySpanId.CreateRandom();
        await File.WriteAllTextAsync(arquivo + ".traceparent", $"00-{trace.ToHexString()}-{span.ToHexString()}-01", ct);

        await cenario.CicloAsync(ct);
        telemetria.Coletar();

        var spanArquivo = Assert.Single(telemetria.Spans, s => s.DisplayName == "arquivo");
        Assert.Equal(trace, spanArquivo.TraceId);
        Assert.Equal(span, spanArquivo.ParentSpanId);
        Assert.Equal(["a.txt", "a.txt.traceparent"], cenario.Ambiente.Arquivos("Processed"));
        Assert.Empty(cenario.Ambiente.Arquivos("Input"));
    }

    [Fact]
    public async Task Funciona_sem_FolderPathInter_configurado_e_nao_cria_Output()
    {
        // FR-012: a instância Condominio não depende da raiz do Inter.
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync();
        cenario.Ambiente.Configuracao["SharePointFolder:FolderPathInter"] = null;
        await cenario.ReiniciarAsync();
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");

        var resumo = await cenario.CicloAsync(ct);

        Assert.Equal(0, resumo.ArquivosComErro);
        Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Processed"));
        Assert.Equal(["Fail", "Input", "Logs", "Processed"], Directory.GetDirectories(cenario.Ambiente.RaizCondominio).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(cenario.Ambiente.RaizInter));
    }

    [Fact]
    public async Task Sem_traceparent_cria_nova_raiz_sem_erro()
    {
        var ct = TestContext.Current.CancellationToken;
        using var telemetria = new ColetorTelemetria();
        await using var cenario = await CriarAsync();
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        var arquivo = cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        await File.WriteAllTextAsync(arquivo + ".traceparent", "invalido", ct);

        var resumo = await cenario.CicloAsync(ct);
        telemetria.Coletar();

        Assert.Equal(0, resumo.ArquivosComErro);
        var spanArquivo = Assert.Single(telemetria.Spans, s => s.DisplayName == "arquivo");
        var ciclo = Assert.Single(telemetria.Spans, s => s.DisplayName == "ciclo");
        Assert.Equal(ciclo.TraceId, spanArquivo.TraceId);
        Assert.Single(cenario.Ambiente.LinhasLog("Logs"));
    }
}
