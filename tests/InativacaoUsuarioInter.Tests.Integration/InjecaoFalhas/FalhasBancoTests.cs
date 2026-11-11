using System.Diagnostics;
using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Estado;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Service.Telemetria;
using InativacaoUsuarioInter.Tests.Comum;
using Microsoft.Extensions.DependencyInjection;

namespace InativacaoUsuarioInter.Tests.Integration.InjecaoFalhas;

/// <summary>
/// T059 — injeção de falhas no WAccess com proxy TCP real (Princípios II e V): indisponibilidade, corte no meio
/// do arquivo, latência acima do timeout, curto-circuito, orçamento do ciclo e linhagem das partes pendentes.
/// </summary>
public sealed class FalhasBancoTests(SqlServerFixture sql)
{
    private static readonly TimeSpan PausaCircuito = TimeSpan.FromSeconds(5.5);

    private static Task<Cenario> CriarAsync(
        SqlServerFixture sql, ProxyTcpFalhas proxy, Action<AmbienteTeste>? ajustar = null, Action<IServiceCollection>? servicos = null) =>
        Cenario.CriarAsync(
            sql,
            ajustarAmbiente: ajustar,
            ajustarServicos: servicos,
            conexao: b => b.StringConexaoVia(proxy.Porta),
            cancelamento: TestContext.Current.CancellationToken);

    private static async Task SemearAsync(Cenario cenario, params string[] cpfs)
    {
        foreach (var cpf in cpfs)
        {
            await cenario.Banco.InserirUsuarioAsync(cpf, 0, cancelamento: TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CA06_SC004_banco_indisponivel_deixa_tudo_pendente_e_conclui_apos_a_pausa_do_circuito()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var proxy = ProxyTcpFalhas.Iniciar(sql.Host, sql.Porta);
        await using var cenario = await CriarAsync(sql, proxy);
        await SemearAsync(cenario, "22222222222", "33333333333", "44444444444");
        cenario.Ambiente.Depositar("20261111103000.txt", "22222222222,1,Maria\r\n33333333333,2,Carlos\r\n44444444444,3,Luana\r\n");

        proxy.Cortar();
        await cenario.CicloAsync(ct);

        Assert.Equal(["20261111103000.txt"], cenario.Ambiente.Arquivos("Imput"));
        Assert.Empty(cenario.Ambiente.LinhasLog("Auditoria"));
        Assert.Empty(cenario.Ambiente.LinhasLog("Falhas"));
        Assert.Equal(3, await cenario.Obter<IArmazenamentoEstado>().ContarBacklogAsync(ct));

        proxy.Restaurar();
        await cenario.CicloAsync(ct);
        Assert.Empty(cenario.Ambiente.LinhasLog("Auditoria"));

        await Task.Delay(PausaCircuito, ct);
        await cenario.CicloAsync(ct);

        Assert.Equal(3, cenario.Ambiente.LinhasLog("Auditoria").Length);
        Assert.Equal(["20261111103000.txt"], cenario.Ambiente.Arquivos("Output"));
        Assert.Empty(cenario.Ambiente.Arquivos("Imput"));
    }

    [Fact]
    public async Task CA07_corte_no_meio_segrega_sucessos_e_pendentes_preservando_a_linhagem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var proxy = ProxyTcpFalhas.Iniciar(sql.Host, sql.Porta);
        await using var cenario = await CriarAsync(
            sql, proxy, servicos: s => s.AddSingleton<IPontoFalha>(new GatilhoPonto(PontoFalha.AposRegistrarResultado, 2, proxy.Cortar)));
        await SemearAsync(cenario, "22222222222", "33333333333", "44444444444");
        cenario.Ambiente.Depositar("20261111103000.json", """
            [
              { "cpf": "22222222222", "id_responsavel": "1", "nome_responsavel": "Maria" },
              { "cpf": "33333333333", "id_responsavel": "2", "nome_responsavel": "Carlos" },
              { "cpf": "44444444444", "id_responsavel": "3", "nome_responsavel": "Luana" }
            ]
            """);

        await cenario.CicloAsync(ct);

        var sucesso = Assert.Single(cenario.Ambiente.Arquivos("Output"));
        Assert.Matches(@"^20261111103000__sucesso-[0-9a-f]{8}\.json$", sucesso);
        var pendente = Assert.Single(cenario.Ambiente.Arquivos("Imput"));
        Assert.Matches(@"^20261111103000__pendente-[0-9a-f]{8}\.json$", pendente);
        var conteudoPendente = await File.ReadAllTextAsync(cenario.Ambiente.Pasta("Imput", pendente), ct);
        Assert.Contains("44444444444", conteudoPendente, StringComparison.Ordinal);
        Assert.DoesNotContain("22222222222", conteudoPendente, StringComparison.Ordinal);
        Assert.Equal(2, cenario.Ambiente.LinhasLog("Auditoria").Length);

        var estado = cenario.Obter<IArmazenamentoEstado>();
        var raiz = await OrigemAsync(cenario, sucesso);
        var antes = (await estado.ObterRegistrosAsync(raiz, ct)).Values.Single(r => r.Estado != EstadosRegistro.Inativado);
        Assert.NotNull(antes.PendenteDesde);

        proxy.Restaurar();
        await Task.Delay(PausaCircuito, ct);
        await cenario.CicloAsync(ct);

        var depois = (await estado.ObterRegistrosAsync(raiz, ct))[antes.Chave];
        Assert.Equal(EstadosRegistro.Inativado, depois.Estado);
        Assert.Equal(antes.PendenteDesde, depois.PendenteDesde);
        Assert.Equal(3, cenario.Ambiente.LinhasLog("Auditoria").Length);
        Assert.Equal(2, cenario.Ambiente.Arquivos("Output").Length);
        Assert.All(cenario.Ambiente.Arquivos("Output"), a => Assert.StartsWith("20261111103000__sucesso-", a, StringComparison.Ordinal));
        Assert.Empty(cenario.Ambiente.Arquivos("Imput"));
        Assert.Equal([(short)1], await cenario.Banco.ObterEstadosAsync("44444444444", ct));
    }

    [Fact]
    public async Task Latencia_acima_do_timeout_eh_transitoria_com_retentativas_e_circuito_abre()
    {
        var ct = TestContext.Current.CancellationToken;
        using var telemetria = new ColetorTelemetria();
        await using var proxy = ProxyTcpFalhas.Iniciar(sql.Host, sql.Porta);
        await using var cenario = await CriarAsync(sql, proxy, a =>
        {
            a.Configuracao["Settings:IntervalSeconds"] = "30";
            a.Configuracao["Resiliencia:WAccess:TimeoutConexaoSegundos"] = "2";
            a.Configuracao["Resiliencia:WAccess:TimeoutComandoSegundos"] = "1";
            a.Configuracao["Resiliencia:WAccess:Circuito:JanelaSegundos"] = "60";
        });
        await SemearAsync(cenario, "22222222222", "33333333333", "44444444444");
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n33333333333,2,Carlos\r\n44444444444,3,Luana\r\n");

        proxy.InjetarLatencia(4000);
        await cenario.CicloAsync(ct);
        telemetria.Coletar();

        Assert.True(telemetria.Somar("retentativas", Instrumentacao.Atributos.Dependencia, Dependencias.WAccess) > 0);
        Assert.Equal(3, await cenario.Obter<IArmazenamentoEstado>().ContarBacklogAsync(ct));

        // Curto-circuito: só o 1º registro chegou a chamar o banco neste ciclo.
        var registros = telemetria.Spans.Where(s => s.DisplayName == "registro").ToList();
        Assert.Equal(3, registros.Count);
        Assert.All(registros, r => Assert.Equal("transitoria", r.GetTagItem(Instrumentacao.Atributos.Resultado)));
        var chamadas = telemetria.Spans.Where(s => s.DisplayName.StartsWith("waccess.", StringComparison.Ordinal)).ToList();
        Assert.All(chamadas, c => Assert.Equal(registros.First().TraceId, c.TraceId));
        Assert.Single(chamadas.Select(c => c.ParentSpanId).Distinct());
        Assert.Equal(1, EstadoMetricas.Circuitos[Dependencias.WAccess]);
    }

    [Fact]
    public async Task Orcamento_do_ciclo_limita_o_tempo_total_de_retentativas_ao_intervalo()
    {
        var ct = TestContext.Current.CancellationToken;
        using var telemetria = new ColetorTelemetria();
        await using var proxy = ProxyTcpFalhas.Iniciar(sql.Host, sql.Porta);
        await using var cenario = await CriarAsync(sql, proxy, a =>
        {
            a.Configuracao["Settings:IntervalSeconds"] = "10";
            a.Configuracao["Resiliencia:WAccess:MaxTentativas"] = "10";
            a.Configuracao["Resiliencia:WAccess:TimeoutConexaoSegundos"] = "2";
            a.Configuracao["Resiliencia:WAccess:TimeoutComandoSegundos"] = "1";
            a.Configuracao["Resiliencia:WAccess:Circuito:MinimoChamadas"] = "100";
        });
        await SemearAsync(cenario, "22222222222");
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");

        proxy.InjetarLatencia(5000);
        var cronometro = Stopwatch.StartNew();
        await cenario.CicloAsync(ct);
        cronometro.Stop();
        telemetria.Coletar();

        var tentativas = telemetria.Spans.Where(s => s.DisplayName == "waccess.ler_estado").OrderBy(s => s.StartTimeUtc).ToList();
        Assert.InRange(tentativas.Count, 2, 10);
        var primeira = tentativas[0].Duration;
        var extras = (tentativas[^1].StartTimeUtc + tentativas[^1].Duration) - (tentativas[0].StartTimeUtc + primeira);
        Assert.True(extras <= TimeSpan.FromSeconds(11), $"Retentativas consumiram {extras.TotalSeconds:F1} s (> 10 s + tolerância).");
        Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(20), $"Ciclo levou {cronometro.Elapsed.TotalSeconds:F1} s.");
        Assert.Equal(1, await cenario.Obter<IArmazenamentoEstado>().ContarBacklogAsync(ct));
    }

    /// <summary>Hash do arquivo original (raiz) a partir de uma parte gerada a partir dele.</summary>
    private static async Task<string> OrigemAsync(Cenario cenario, string parteOutput)
    {
        var ct = TestContext.Current.CancellationToken;
        var hash = await Service.Arquivos.HashConteudo.CalcularArquivoAsync(cenario.Ambiente.Pasta("Output", parteOutput), ct);
        var parte = await cenario.Obter<IArmazenamentoEstado>().ObterParteAsync(hash, ct);
        return parte!.HashOrigem;
    }
}
