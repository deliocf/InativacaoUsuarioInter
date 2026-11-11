using System.Text.Json;
using InativacaoUsuarioInter.Tests.Comum;

namespace InativacaoUsuarioInter.Tests.Integration.Saude;

/// <summary>T064 — alerta de pendência acima de 24 h (FR-042, SC-011) com relógio simulado e banco cortado.</summary>
public sealed class AlertaPendenciaTests(SqlServerFixture sql)
{
    private static JsonElement LerSaude(AmbienteTeste ambiente)
    {
        using var documento = JsonDocument.Parse(File.ReadAllText(Path.Combine(ambiente.DiretorioEstado, "saude.json")));
        return documento.RootElement.Clone();
    }

    [Fact]
    public async Task SC011_alerta_aparece_apos_24h_e_some_quando_a_pendencia_resolve()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var proxy = ProxyTcpFalhas.Iniciar(sql.Host, sql.Porta);
        await using var cenario = await Cenario.CriarAsync(
            sql, ajustarAmbiente: a => a.UsarRelogioSimulado(), conexao: b => b.StringConexaoVia(proxy.Porta), cancelamento: ct);
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        var relogio = cenario.Ambiente.Relogio!;

        proxy.Cortar();
        await cenario.CicloAsync(ct);
        var saude = LerSaude(cenario.Ambiente);
        Assert.Equal("Degradado", saude.GetProperty("status").GetString());
        Assert.Equal("Indisponivel", saude.GetProperty("dependencias").GetProperty("waccess").GetProperty("status").GetString());
        Assert.Equal(0, saude.GetProperty("alertas").GetArrayLength());
        Assert.Equal(1, saude.GetProperty("backlog").GetProperty("registrosPendentes").GetInt32());

        relogio.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        await cenario.CicloAsync(ct);
        saude = LerSaude(cenario.Ambiente);
        Assert.Equal("Alerta", saude.GetProperty("status").GetString());
        var alerta = Assert.Single(saude.GetProperty("alertas").EnumerateArray());
        Assert.Equal("PENDENCIA_ACIMA_LIMITE", alerta.GetProperty("codigo").GetString());
        Assert.Equal(1, alerta.GetProperty("quantidade").GetInt32());
        Assert.Contains(cenario.Ambiente.Logs.Entradas, e => e.Mensagem.Contains("PENDENCIA_ACIMA_LIMITE", StringComparison.Ordinal));

        proxy.Restaurar();
        await Task.Delay(TimeSpan.FromSeconds(5.5), ct);
        await cenario.CicloAsync(ct);
        saude = LerSaude(cenario.Ambiente);

        Assert.Equal("Saudavel", saude.GetProperty("status").GetString());
        Assert.Equal(0, saude.GetProperty("alertas").GetArrayLength());
        Assert.Single(cenario.Ambiente.LinhasLog("Auditoria"));
    }
}
