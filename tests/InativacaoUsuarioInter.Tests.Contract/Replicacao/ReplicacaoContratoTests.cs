using System.Text.RegularExpressions;
using InativacaoUsuarioInter.Tests.Comum;

namespace InativacaoUsuarioInter.Tests.Contract.Replicacao;

/// <summary>T073 — protocolo do artefato de replicação Inter → Condomínio (contracts/replicacao.md).</summary>
public sealed partial class ReplicacaoContratoTests(SqlServerFixture sql)
{
    private const string Conteudo = "22222222222,1,Maria\r\n";

    [GeneratedRegex("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$")]
    private static partial Regex Traceparent();

    private async Task<Cenario> CriarComArquivoAsync(CancellationToken ct)
    {
        var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        cenario.Ambiente.Depositar("20261111103000.txt", Conteudo);
        return cenario;
    }

    [Fact]
    public async Task Copia_byte_a_byte_com_sidecar_traceparent_e_sem_temporario_visivel()
    {
        var ct = TestContext.Current.CancellationToken;
        using var telemetria = new ColetorTelemetria();
        await using var cenario = await CriarComArquivoAsync(ct);

        await cenario.CicloAsync(ct);

        var destino = cenario.Ambiente.PastaCondominio("Imput");
        Assert.Equal(["20261111103000.txt", "20261111103000.txt.traceparent"], cenario.Ambiente.Arquivos("Imput", cenario.Ambiente.RaizCondominio));
        Assert.Equal(
            await File.ReadAllBytesAsync(cenario.Ambiente.Pasta("Output", "20261111103000.txt"), ct),
            await File.ReadAllBytesAsync(Path.Combine(destino, "20261111103000.txt"), ct));
        var traceparent = (await File.ReadAllTextAsync(Path.Combine(destino, "20261111103000.txt.traceparent"), ct)).Trim();
        Assert.Matches(Traceparent(), traceparent);
        Assert.Contains(telemetria.Spans, s => s.DisplayName == "replicacao.copiar" && traceparent.Contains(s.TraceId.ToHexString(), StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(destino, "*.tmp"));
    }

    [Fact]
    public async Task Colisao_com_mesmo_conteudo_eh_noop()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarComArquivoAsync(ct);
        var destino = cenario.Ambiente.PastaCondominio("Imput");
        Directory.CreateDirectory(destino);
        await File.WriteAllTextAsync(Path.Combine(destino, "20261111103000.txt"), Conteudo, ct);

        await cenario.CicloAsync(ct);
        await cenario.CicloAsync(ct);

        Assert.Equal(["20261111103000.txt"], cenario.Ambiente.Arquivos("Imput", cenario.Ambiente.RaizCondominio));
    }

    [Fact]
    public async Task Colisao_com_conteudo_diferente_entrega_com_nome_unico()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarComArquivoAsync(ct);
        var destino = cenario.Ambiente.PastaCondominio("Imput");
        Directory.CreateDirectory(destino);
        await File.WriteAllTextAsync(Path.Combine(destino, "20261111103000.txt"), "outro conteudo", ct);

        await cenario.CicloAsync(ct);

        var arquivos = cenario.Ambiente.Arquivos("Imput", cenario.Ambiente.RaizCondominio).Where(a => a.EndsWith(".txt", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, arquivos.Length);
        Assert.Equal("outro conteudo", await File.ReadAllTextAsync(Path.Combine(destino, "20261111103000.txt"), ct));
        var entregue = Assert.Single(arquivos, a => a != "20261111103000.txt");
        Assert.Matches(@"^20261111103000__[0-9a-f]{8}\.txt$", entregue);
        Assert.Equal(Conteudo, await File.ReadAllTextAsync(Path.Combine(destino, entregue), ct));
    }
}
