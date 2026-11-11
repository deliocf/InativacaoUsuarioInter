using System.Text.Json;
using System.Text.RegularExpressions;
using InativacaoUsuarioInter.Tests.Comum;

namespace InativacaoUsuarioInter.Tests.Contract.Saude;

/// <summary>T063 — contrato de <c>saude.json</c> (contracts/saude.md).</summary>
public sealed partial class SaudeContratoTests(SqlServerFixture sql)
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}$")]
    private static partial Regex DataHora();

    [Fact]
    public async Task Campos_e_valores_conforme_contrato_sem_dados_sensiveis()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria Eduarda\r\n");

        await cenario.CicloAsync(ct);

        var caminho = Path.Combine(cenario.Ambiente.DiretorioEstado, "saude.json");
        var texto = await File.ReadAllTextAsync(caminho, ct);
        using var documento = JsonDocument.Parse(texto);
        var raiz = documento.RootElement;

        Assert.Equal(1, raiz.GetProperty("versao").GetInt32());
        Assert.Equal("Inter", raiz.GetProperty("instancia").GetString());
        Assert.Contains(raiz.GetProperty("status").GetString(), new[] { "Saudavel", "Degradado", "Alerta" });
        Assert.Equal("Saudavel", raiz.GetProperty("status").GetString());
        foreach (var campo in new[] { "iniciadoEm", "ultimoCicloInicio", "ultimoCicloFim", "proximoCicloPrevisto" })
        {
            Assert.Matches(DataHora(), raiz.GetProperty(campo).GetString());
        }

        Assert.Equal(10, raiz.GetProperty("intervaloSegundos").GetInt32());
        var dependencias = raiz.GetProperty("dependencias");
        foreach (var nome in new[] { "waccess", "pastaLocal", "replicacao" })
        {
            var dependencia = dependencias.GetProperty(nome);
            Assert.Contains(dependencia.GetProperty("status").GetString(), new[] { "Disponivel", "Indisponivel", "NaoAplicavel" });
            Assert.Contains(dependencia.GetProperty("circuito").GetString(), new[] { "Fechado", "Aberto", "MeioAberto" });
            Assert.True(dependencia.TryGetProperty("ultimaFalha", out _));
        }

        var backlog = raiz.GetProperty("backlog");
        Assert.Equal(0, backlog.GetProperty("registrosPendentes").GetInt32());
        Assert.Equal(JsonValueKind.Null, backlog.GetProperty("pendenciaMaisAntigaDesde").ValueKind);
        Assert.Equal(0, backlog.GetProperty("entregasPendentes").GetInt32());
        Assert.Equal(JsonValueKind.Array, raiz.GetProperty("alertas").ValueKind);

        Assert.False(File.Exists(caminho + ".tmp"));
        Assert.DoesNotContain("22222222222", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("Maria", texto, StringComparison.Ordinal);
        Assert.DoesNotContain(cenario.Banco.Nome, texto, StringComparison.Ordinal);
        Assert.DoesNotContain(JsonEncodedText.Encode(cenario.Ambiente.Raiz).ToString(), texto, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Condominio_marca_replicacao_como_nao_aplicavel()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, "Condominio", cancelamento: ct);

        await cenario.CicloAsync(ct);

        using var documento = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(cenario.Ambiente.DiretorioEstado, "saude.json"), ct));
        Assert.Equal("NaoAplicavel", documento.RootElement.GetProperty("dependencias").GetProperty("replicacao").GetProperty("status").GetString());
        Assert.Equal("Condominio", documento.RootElement.GetProperty("instancia").GetString());
    }
}
