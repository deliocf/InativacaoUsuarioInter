using System.Text.RegularExpressions;
using InativacaoUsuarioInter.Tests.Comum;

namespace InativacaoUsuarioInter.Tests.Contract.Replicacao;

/// <summary>
/// T073 + feature 002 T020 — protocolo de entrega Inter → Condomínio v2
/// (specs/002-ajustes-pastas-fluxo/contracts/replicacao.md): fila Output, destino Condominio/Input, limpeza após Entregue.
/// </summary>
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
    public async Task Copia_byte_a_byte_sem_sidecar_traceparent_e_sem_temporario_visivel()
    {
        // Bug nome-original-sem-trace: nenhum arquivo de trace no Condomínio; o traceparent fica só no diário do Inter.
        var ct = TestContext.Current.CancellationToken;
        using var telemetria = new ColetorTelemetria();
        await using var cenario = await CriarComArquivoAsync(ct);

        await cenario.CicloAsync(ct);

        var destino = cenario.Ambiente.PastaCondominio("Input");
        Assert.Equal(["20261111103000.txt"], cenario.Ambiente.Arquivos("Input", cenario.Ambiente.RaizCondominio));
        Assert.Equal(
            await File.ReadAllBytesAsync(cenario.Ambiente.Pasta("Processed", "20261111103000.txt"), ct),
            await File.ReadAllBytesAsync(Path.Combine(destino, "20261111103000.txt"), ct));
        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
        Assert.Contains(telemetria.Spans, s => s.DisplayName == "replicacao.copiar");
        Assert.Empty(Directory.GetFiles(destino, "*.tmp"));
        Assert.Empty(Directory.GetFiles(destino, "*.traceparent"));
    }

    [Fact]
    public async Task Colisao_com_mesmo_conteudo_eh_noop()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarComArquivoAsync(ct);
        var destino = cenario.Ambiente.PastaCondominio("Input");
        Directory.CreateDirectory(destino);
        await File.WriteAllTextAsync(Path.Combine(destino, "20261111103000.txt"), Conteudo, ct);

        await cenario.CicloAsync(ct);
        await cenario.CicloAsync(ct);

        Assert.Equal(["20261111103000.txt"], cenario.Ambiente.Arquivos("Input", cenario.Ambiente.RaizCondominio));
        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
    }

    [Fact]
    public async Task Colisao_com_conteudo_diferente_aguarda_e_entrega_com_nome_original_depois_de_consumido()
    {
        // Bug nome-original-sem-trace: nunca renomear nem sobrescrever no Condomínio; a entrega espera na fila Output.
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarComArquivoAsync(ct);
        var destino = cenario.Ambiente.PastaCondominio("Input");
        Directory.CreateDirectory(destino);
        await File.WriteAllTextAsync(Path.Combine(destino, "20261111103000.txt"), "outro conteudo", ct);

        await cenario.CicloAsync(ct);
        await cenario.CicloAsync(ct);

        Assert.Equal(["20261111103000.txt"], cenario.Ambiente.Arquivos("Input", cenario.Ambiente.RaizCondominio));
        Assert.Equal("outro conteudo", await File.ReadAllTextAsync(Path.Combine(destino, "20261111103000.txt"), ct));
        Assert.Equal(["20261111103000.txt"], cenario.Ambiente.Arquivos("Output"));
        Assert.Equal(1, await cenario.Obter<Service.Estado.IArmazenamentoEstado>().ContarEntregasPendentesAsync(ct));

        // O Condomínio consumiu o arquivo anterior: a entrega sai com o nome original.
        File.Delete(Path.Combine(destino, "20261111103000.txt"));
        await cenario.CicloAsync(ct);

        Assert.Equal(["20261111103000.txt"], cenario.Ambiente.Arquivos("Input", cenario.Ambiente.RaizCondominio));
        Assert.Equal(Conteudo, await File.ReadAllTextAsync(Path.Combine(destino, "20261111103000.txt"), ct));
        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
        Assert.Equal(0, await cenario.Obter<Service.Estado.IArmazenamentoEstado>().ContarEntregasPendentesAsync(ct));
    }

    [Fact]
    public async Task Destino_inacessivel_mantem_na_fila_sem_bloquear_a_entrada_e_entrega_depois()
    {
        // FR-002a: a raiz do Condomínio é um arquivo (impossível criar Condominio/Input) → falha só da entrega.
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarComArquivoAsync(ct);
        await File.WriteAllTextAsync(cenario.Ambiente.RaizCondominio, "não é pasta", ct);

        var resumo = await cenario.CicloAsync(ct);

        Assert.Equal(0, resumo.ArquivosComErro);
        Assert.Empty(cenario.Ambiente.Arquivos("Input"));
        Assert.Equal(["20261111103000.txt"], cenario.Ambiente.Arquivos("Processed"));
        Assert.Equal(["20261111103000.txt"], cenario.Ambiente.Arquivos("Output"));

        File.Delete(cenario.Ambiente.RaizCondominio);

        // As falhas abriram o circuito de replicação (pausa de 5 s no ambiente de teste): entrega após a pausa.
        await Task.Delay(TimeSpan.FromSeconds(5.5), ct);
        await cenario.CicloAsync(ct);

        Assert.Contains("20261111103000.txt", cenario.Ambiente.Arquivos("Input", cenario.Ambiente.RaizCondominio));
        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
    }

    [Fact]
    public async Task Pendentes_falhas_e_arquivos_desconhecidos_nunca_sao_entregues()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarComArquivoAsync(ct);
        cenario.Ambiente.Depositar("falho.txt", "77777777777,1,Sem cadastro\r\n");
        Directory.CreateDirectory(cenario.Ambiente.Pasta("Output"));
        await File.WriteAllTextAsync(cenario.Ambiente.Pasta("Output", "manual.txt"), "22222222222,1,Copiado a mao\r\n", ct);

        await cenario.CicloAsync(ct);

        Assert.Equal(
            ["20261111103000.txt"],
            cenario.Ambiente.Arquivos("Input", cenario.Ambiente.RaizCondominio).Where(a => a.EndsWith(".txt", StringComparison.Ordinal)));
        Assert.Equal(["manual.txt"], cenario.Ambiente.Arquivos("Output"));
        Assert.Contains("falho.txt", cenario.Ambiente.Arquivos("Fail"));
    }
}
