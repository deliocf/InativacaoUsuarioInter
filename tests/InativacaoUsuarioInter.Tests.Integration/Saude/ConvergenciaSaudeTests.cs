using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using InativacaoUsuarioInter.Tests.Comum;
using OpenTelemetry.Metrics;

namespace InativacaoUsuarioInter.Tests.Integration.Saude;

/// <summary>
/// T096 (idade do arquivo de entrada mais antigo), T097 (readiness ativa) e T100 (permissões amplas em pastas
/// com dados pessoais) — observados em <c>saude.json</c> e na telemetria.
/// </summary>
public sealed class ConvergenciaSaudeTests(SqlServerFixture sql)
{
    private static JsonElement Saude(AmbienteTeste ambiente)
    {
        using var documento = JsonDocument.Parse(File.ReadAllText(Path.Combine(ambiente.DiretorioEstado, "saude.json")));
        return documento.RootElement.Clone();
    }

    private static double Medidor(ColetorTelemetria telemetria, string nome)
    {
        telemetria.Coletar();
        var valor = 0d;
        foreach (var metrica in telemetria.Metricas.Where(m => m.Name == nome))
        {
            foreach (ref readonly var ponto in metrica.GetMetricPoints())
            {
                valor = ponto.GetGaugeLastValueDouble();
            }
        }

        return valor;
    }

    [Fact]
    public async Task T096_arquivo_bloqueado_na_entrada_aparece_na_metrica_e_na_saude_ate_ser_processado()
    {
        var ct = TestContext.Current.CancellationToken;
        using var telemetria = new ColetorTelemetria();
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        var caminho = cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");

        await using (new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await cenario.CicloAsync(ct);
        }

        var backlog = Saude(cenario.Ambiente).GetProperty("backlog");
        Assert.Equal(JsonValueKind.String, backlog.GetProperty("arquivoEntradaMaisAntigoDesde").ValueKind);
        Assert.True(Medidor(telemetria, "idade_arquivo_entrada_mais_antigo_s") >= 3000, "O arquivo foi escrito há 1 hora.");

        telemetria.Metricas.Clear();
        await cenario.CicloAsync(ct);

        Assert.Equal(JsonValueKind.Null, Saude(cenario.Ambiente).GetProperty("backlog").GetProperty("arquivoEntradaMaisAntigoDesde").ValueKind);
        Assert.Equal(0, Medidor(telemetria, "idade_arquivo_entrada_mais_antigo_s"));
    }

    [Fact]
    public async Task T097_readiness_do_WAccess_eh_sondada_mesmo_com_entrada_vazia()
    {
        var ct = TestContext.Current.CancellationToken;
        using var telemetria = new ColetorTelemetria();
        await using var proxy = ProxyTcpFalhas.Iniciar(sql.Host, sql.Porta);
        await using var cenario = await Cenario.CriarAsync(sql, conexao: b => b.StringConexaoVia(proxy.Porta), cancelamento: ct);

        await cenario.CicloAsync(ct);
        Assert.Equal("Saudavel", Saude(cenario.Ambiente).GetProperty("status").GetString());

        proxy.Cortar();
        await cenario.CicloAsync(ct);
        var saude = Saude(cenario.Ambiente);
        Assert.Equal("Degradado", saude.GetProperty("status").GetString());
        Assert.Equal("Indisponivel", saude.GetProperty("dependencias").GetProperty("waccess").GetProperty("status").GetString());
        Assert.Contains(telemetria.Spans, s => s.DisplayName == "saude.sonda" && Equals(s.GetTagItem("resultado"), "transitoria"));

        proxy.Restaurar();
        await Task.Delay(TimeSpan.FromSeconds(5.5), ct);
        await cenario.CicloAsync(ct);

        Assert.Equal("Disponivel", Saude(cenario.Ambiente).GetProperty("dependencias").GetProperty("waccess").GetProperty("status").GetString());
    }

    [Fact]
    public async Task T100_pasta_de_auditoria_legivel_por_grupo_amplo_gera_alerta()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await cenario.CicloAsync(ct);
        Assert.Equal(0, Saude(cenario.Ambiente).GetProperty("alertas").GetArrayLength());

        var restaurar = AbrirLeitura(cenario.Ambiente.Pasta("Logs"));
        try
        {
            await cenario.CicloAsync(ct);
            var alerta = Assert.Single(Saude(cenario.Ambiente).GetProperty("alertas").EnumerateArray());
            Assert.Equal("PERMISSAO_ABERTA_DADOS_PESSOAIS", alerta.GetProperty("codigo").GetString());
            Assert.Equal(1, alerta.GetProperty("quantidade").GetInt32());
            Assert.Contains(cenario.Ambiente.Logs.Entradas, e => e.Mensagem.Contains("PERMISSAO_ABERTA_DADOS_PESSOAIS", StringComparison.Ordinal));
        }
        finally
        {
            restaurar();
        }

        await cenario.CicloAsync(ct);
        Assert.Equal(0, Saude(cenario.Ambiente).GetProperty("alertas").GetArrayLength());
    }

    private static Action AbrirLeitura(string pasta) =>
        OperatingSystem.IsWindows() ? AbrirLeituraWindows(pasta) : AbrirLeituraUnix(pasta);

    [UnsupportedOSPlatform("windows")]
    private static Action AbrirLeituraUnix(string pasta)
    {
        var original = File.GetUnixFileMode(pasta);
        File.SetUnixFileMode(pasta, original | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return () => File.SetUnixFileMode(pasta, original);
    }

    [SupportedOSPlatform("windows")]
    private static Action AbrirLeituraWindows(string pasta)
    {
        var diretorio = new DirectoryInfo(pasta);
        var regra = new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow);
        var seguranca = diretorio.GetAccessControl();
        seguranca.AddAccessRule(regra);
        diretorio.SetAccessControl(seguranca);
        return () =>
        {
            var atual = diretorio.GetAccessControl();
            atual.RemoveAccessRule(regra);
            diretorio.SetAccessControl(atual);
        };
    }
}
