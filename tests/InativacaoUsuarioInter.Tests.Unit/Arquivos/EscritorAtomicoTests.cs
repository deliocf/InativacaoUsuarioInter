using System.Text;
using InativacaoUsuarioInter.Service.Arquivos;

namespace InativacaoUsuarioInter.Tests.Unit.Arquivos;

/// <summary>T012 — escrita atômica e idempotente (Princípio III, FR-030).</summary>
public sealed class EscritorAtomicoTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "escritor-" + Guid.NewGuid().ToString("N"));

    public EscritorAtomicoTests() => Directory.CreateDirectory(_pasta);

    [Fact]
    public async Task Gravar_duas_vezes_o_mesmo_conteudo_eh_noop()
    {
        var destino = Path.Combine(_pasta, "a.json");
        var conteudo = Encoding.UTF8.GetBytes("[1]");

        var primeira = await EscritorAtomico.GravarAsync(destino, conteudo, TestContext.Current.CancellationToken);
        var segunda = await EscritorAtomico.GravarAsync(destino, conteudo, TestContext.Current.CancellationToken);

        Assert.Equal(ResultadoEscrita.Gravado, primeira);
        Assert.Equal(ResultadoEscrita.JaExistia, segunda);
        Assert.Equal("[1]", await File.ReadAllTextAsync(destino, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(_pasta));
    }

    [Fact]
    public async Task Destino_com_conteudo_diferente_nunca_eh_sobrescrito()
    {
        var destino = Path.Combine(_pasta, "a.txt");
        await File.WriteAllTextAsync(destino, "original", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ColisaoArquivoException>(() =>
            EscritorAtomico.GravarAsync(destino, Encoding.UTF8.GetBytes("novo"), TestContext.Current.CancellationToken));

        Assert.Equal("original", await File.ReadAllTextAsync(destino, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(destino + EscritorAtomico.SufixoTemporario));
    }

    [Fact]
    public async Task Temporario_de_interrupcao_anterior_nao_fica_visivel_e_eh_substituido()
    {
        var destino = Path.Combine(_pasta, "b.json");
        await File.WriteAllTextAsync(destino + EscritorAtomico.SufixoTemporario, "[parci", TestContext.Current.CancellationToken);

        Assert.False(File.Exists(destino));
        await EscritorAtomico.GravarAsync(destino, Encoding.UTF8.GetBytes("[2]"), TestContext.Current.CancellationToken);

        Assert.Equal("[2]", await File.ReadAllTextAsync(destino, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(destino + EscritorAtomico.SufixoTemporario));
    }

    [Fact]
    public async Task Hash_eh_estavel_e_igual_para_bytes_e_arquivo()
    {
        var destino = Path.Combine(_pasta, "c.txt");
        var conteudo = Encoding.UTF8.GetBytes("22222222222,1,Maria Eduarda");
        await File.WriteAllBytesAsync(destino, conteudo, TestContext.Current.CancellationToken);

        var hashBytes = HashConteudo.Calcular(conteudo);
        var hashArquivo = await HashConteudo.CalcularArquivoAsync(destino, TestContext.Current.CancellationToken);

        Assert.Equal(hashBytes, hashArquivo);
        Assert.Equal(64, hashBytes.Length);
        Assert.Equal(hashBytes, HashConteudo.Calcular(conteudo));
    }

    [Fact]
    public async Task Mover_para_destino_identico_apenas_remove_a_origem()
    {
        var origem = Path.Combine(_pasta, "origem.json");
        var destino = Path.Combine(_pasta, "destino.json");
        await File.WriteAllTextAsync(origem, "[3]", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(destino, "[3]", TestContext.Current.CancellationToken);

        var resultado = await EscritorAtomico.MoverAsync(origem, destino, TestContext.Current.CancellationToken);

        Assert.Equal(ResultadoEscrita.JaExistia, resultado);
        Assert.False(File.Exists(origem));
        Assert.Equal("[3]", await File.ReadAllTextAsync(destino, TestContext.Current.CancellationToken));
    }

    public void Dispose() => Directory.Delete(_pasta, recursive: true);
}
