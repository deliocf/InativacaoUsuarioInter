using System.Diagnostics;
using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Estado;
using InativacaoUsuarioInter.Tests.Comum;
using Microsoft.Extensions.DependencyInjection;

namespace InativacaoUsuarioInter.Tests.Integration.FluxoCondominio;

/// <summary>T081 — fluxo do Condomínio (US5, CA-11, CA-12, FR-036).</summary>
public sealed class FluxoCondominioTests(SqlServerFixture sql)
{
    private Task<Cenario> CriarAsync(Action<IServiceCollection>? servicos = null, Func<BancoTeste, string>? conexao = null) =>
        Cenario.CriarAsync(sql, "Condominio", ajustarServicos: servicos, conexao: conexao, cancelamento: TestContext.Current.CancellationToken);

    [Fact]
    public async Task CA11_CA12_le_so_o_Imput_do_condominio_audita_localmente_e_conclui_em_Processados()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync();
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        cenario.Ambiente.Depositar("20261111103000__sucesso-1a2b3c4d.txt", "22222222222,1,Maria\r\n");
        cenario.Ambiente.Depositar("ignorado.txt", "22222222222,1,Maria\r\n", cenario.Ambiente.RaizInter);

        await cenario.CicloAsync(ct);

        Assert.Equal([(short)1], await cenario.Banco.ObterEstadosAsync("22222222222", ct));
        Assert.Contains("| Condominio | arquivo=20261111103000__sucesso-1a2b3c4d.txt |", Assert.Single(cenario.Ambiente.LinhasLog("Auditoria")), StringComparison.Ordinal);
        Assert.Equal(["20261111103000__sucesso-1a2b3c4d.txt"], cenario.Ambiente.Arquivos("Processados"));
        Assert.False(Directory.Exists(cenario.Ambiente.Pasta("Output")));
        Assert.Equal(["ignorado.txt"], cenario.Ambiente.Arquivos("Imput", cenario.Ambiente.RaizInter));
        Assert.False(Directory.Exists(Path.Combine(cenario.Ambiente.RaizInter, "Auditoria")));
        Assert.Equal(0, await cenario.Obter<IArmazenamentoEstado>().ContarEntregasPendentesAsync(ct));
    }

    [Fact]
    public async Task Cpf_inexistente_no_condominio_vai_para_Falhas_com_parte_de_falhas()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync();
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n44444444444,2,Inexistente\r\n");

        await cenario.CicloAsync(ct);

        Assert.EndsWith("| Condominio | arquivo=a.txt | cpf=44444444444 | causa=CPF_NAO_ENCONTRADO", Assert.Single(cenario.Ambiente.LinhasLog("Falhas")), StringComparison.Ordinal);
        Assert.Single(cenario.Ambiente.Arquivos("Falhas"), a => a.StartsWith("a__falhas-", StringComparison.Ordinal));
        Assert.Single(cenario.Ambiente.Arquivos("Processados"), a => a.StartsWith("a__sucesso-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Parcial_por_falha_transitoria_vai_para_Processados_e_pendentes_ficam_na_entrada()
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

        Assert.Single(cenario.Ambiente.Arquivos("Processados"), a => a.StartsWith("a__sucesso-", StringComparison.Ordinal));
        Assert.Single(cenario.Ambiente.Arquivos("Imput"), a => a.StartsWith("a__pendente-", StringComparison.Ordinal));
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
        Assert.Equal(["a.txt", "a.txt.traceparent"], cenario.Ambiente.Arquivos("Processados"));
        Assert.Empty(cenario.Ambiente.Arquivos("Imput"));
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
        Assert.Single(cenario.Ambiente.LinhasLog("Auditoria"));
    }
}
