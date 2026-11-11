using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using InativacaoUsuarioInter.Tests.Comum;

namespace InativacaoUsuarioInter.Tests.Integration.Performance;

/// <summary>
/// T089 — orçamento de performance (SC-009, Princípio VI) e bloqueio por regressão > 20% contra a linha de base
/// versionada (análise C3). A linha de base é criada na 1ª execução e só é sobrescrita com <c>ATUALIZAR_LINHA_BASE=1</c>.
/// </summary>
public sealed class CargaTests(SqlServerFixture sql)
{
    private const double Tolerancia = 1.20;

    private sealed record LinhaBase(double P95RegistroMs, double VazaoPorMinuto, double DuracaoArquivoS);

    private static string ComPool(BancoTeste banco) =>
        new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(banco.StringConexao) { Pooling = true }.ConnectionString;

    [Fact]
    public async Task SC009_arquivo_de_1000_registros_dentro_do_orcamento_e_sem_regressao()
    {
        var ct = TestContext.Current.CancellationToken;
        using var telemetria = new ColetorTelemetria();
        // Pool de conexões ligado, como em produção (os demais testes usam Pooling=False para isolar falhas).
        await using var cenario = await Cenario.CriarAsync(sql, conexao: ComPool, cancelamento: ct);
        var cpfs = Enumerable.Range(1, 1000).Select(i => (10_000_000_000L + i).ToString("D11", CultureInfo.InvariantCulture)).ToList();
        await cenario.Banco.InserirEmLoteAsync(cpfs, ct);
        var conteudo = new StringBuilder();
        foreach (var cpf in cpfs)
        {
            conteudo.Append(cpf).Append(",1,Responsavel Carga\r\n");
        }

        cenario.Ambiente.Depositar("carga.txt", conteudo.ToString());

        var cronometro = Stopwatch.StartNew();
        await cenario.CicloAsync(ct);
        cronometro.Stop();
        telemetria.Coletar();

        Assert.Equal(1000, cenario.Ambiente.LinhasLog("Auditoria").Length);
        var duracoes = telemetria.Spans.Where(s => s.DisplayName == "registro").Select(s => s.Duration.TotalMilliseconds).Order().ToList();
        Assert.Equal(1000, duracoes.Count);
        var p95 = duracoes[(int)Math.Ceiling(0.95 * duracoes.Count) - 1];
        var vazao = 1000 / cronometro.Elapsed.TotalMinutes;
        var medido = new LinhaBase(p95, vazao, cronometro.Elapsed.TotalSeconds);

        Assert.True(cronometro.Elapsed <= TimeSpan.FromMinutes(5), $"1.000 registros em {cronometro.Elapsed.TotalSeconds:F1} s (> 5 min).");
        Assert.True(p95 <= 500, $"p95 por registro = {p95:F1} ms (> 500 ms).");
        Assert.True(vazao >= 300, $"Vazão = {vazao:F0} registros/min (< 300).");

        VerificarRegressao(medido);
    }

    [Fact]
    public async Task Arquivo_tipico_de_3_registros_em_ate_10_segundos()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, conexao: ComPool, cancelamento: ct);
        await cenario.Banco.InserirEmLoteAsync(["22222222222", "33333333333", "44444444444"], ct);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n33333333333,2,Carlos\r\n44444444444,3,Luana\r\n");

        var cronometro = Stopwatch.StartNew();
        await cenario.CicloAsync(ct);

        Assert.True(cronometro.Elapsed <= TimeSpan.FromSeconds(10), $"Arquivo típico em {cronometro.Elapsed.TotalSeconds:F1} s.");
        Assert.Equal(3, cenario.Ambiente.LinhasLog("Auditoria").Length);
    }

    [Fact]
    public async Task SC009_memoria_do_servico_com_1000_registros_ate_512MB()
    {
        var ct = TestContext.Current.CancellationToken;
        var banco = await sql.CriarBancoAsync(ct);
        var cpfs = Enumerable.Range(1, 1000).Select(i => (20_000_000_000L + i).ToString("D11", CultureInfo.InvariantCulture)).ToList();
        await banco.InserirEmLoteAsync(cpfs, ct);
        using var ambiente = AmbienteTeste.Criar(ComPool(banco));
        var conteudo = new StringBuilder();
        foreach (var cpf in cpfs)
        {
            conteudo.Append(cpf).Append(",1,Responsavel Carga\r\n");
        }

        ambiente.Depositar("carga.txt", conteudo.ToString());

        long pico = 0;
        using (var servico = ProcessoServico.Iniciar(ambiente))
        {
            try
            {
            var prazo = Stopwatch.StartNew();
            while (prazo.Elapsed < TimeSpan.FromMinutes(5) && ambiente.LinhasLog("Auditoria").Length < 1000 && !servico.HasExited)
            {
                servico.Refresh();
                pico = Math.Max(pico, servico.PeakWorkingSet64);
                await Task.Delay(500, ct);
            }

            servico.Refresh();
            if (!servico.HasExited)
            {
                pico = Math.Max(pico, servico.PeakWorkingSet64);
                servico.Kill(entireProcessTree: true);
                await servico.WaitForExitAsync(ct);
            }
            }
            finally
            {
                ProcessoServico.Encerrar(servico);
            }
        }

        Assert.Equal(1000, ambiente.LinhasLog("Auditoria").Length);
        Assert.True(pico > 0, "Pico de memória do serviço não medido.");
        Assert.True(pico <= 512L * 1024 * 1024, $"Pico de memória do serviço = {pico / (1024 * 1024)} MB (> 512 MB).");
    }

    private static void VerificarRegressao(LinhaBase medido)
    {
        var caminho = Path.Combine(RaizRepositorio(), "tests", "InativacaoUsuarioInter.Tests.Integration", "Performance", "linha-base.json");
        var opcoes = new JsonSerializerOptions { WriteIndented = true };
        if (!File.Exists(caminho) || Environment.GetEnvironmentVariable("ATUALIZAR_LINHA_BASE") == "1")
        {
            File.WriteAllText(caminho, JsonSerializer.Serialize(medido, opcoes) + Environment.NewLine);
            return;
        }

        var baseline = JsonSerializer.Deserialize<LinhaBase>(File.ReadAllText(caminho))!;
        var falhas = new List<string>();
        if (medido.P95RegistroMs > Math.Max(baseline.P95RegistroMs * Tolerancia, baseline.P95RegistroMs + 5))
        {
            falhas.Add($"p95 {medido.P95RegistroMs:F1} ms vs base {baseline.P95RegistroMs:F1} ms");
        }

        if (medido.VazaoPorMinuto < baseline.VazaoPorMinuto / Tolerancia)
        {
            falhas.Add($"vazão {medido.VazaoPorMinuto:F0}/min vs base {baseline.VazaoPorMinuto:F0}/min");
        }

        if (medido.DuracaoArquivoS > baseline.DuracaoArquivoS * Tolerancia)
        {
            falhas.Add($"duração {medido.DuracaoArquivoS:F1} s vs base {baseline.DuracaoArquivoS:F1} s");
        }

        Assert.True(falhas.Count == 0, "Regressão de performance acima de 20% (Princípio VI): " + string.Join("; ", falhas));
    }

    private static string RaizRepositorio()
    {
        var diretorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (diretorio is not null && !File.Exists(Path.Combine(diretorio.FullName, "global.json")))
        {
            diretorio = diretorio.Parent;
        }

        return diretorio!.FullName;
    }
}
