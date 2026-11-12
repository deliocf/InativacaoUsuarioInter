using System.Text;
using InativacaoUsuarioInter.Service.Arquivos;
using InativacaoUsuarioInter.Service.Entrada.Modelos;

namespace InativacaoUsuarioInter.Tests.Unit.Arquivos;

/// <summary>
/// Bug nome-original-sem-trace — histórico (<c>Processed</c>/<c>Fail</c>) sempre com o nome original: colisão com
/// conteúdo diferente é mesclada (união ordenada, idempotente), nunca renomeada nem sobrescrita com perda.
/// </summary>
public sealed class MescladorHistoricoTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "mescla-" + Guid.NewGuid().ToString("N"));

    private static byte[] B(string texto) => Encoding.UTF8.GetBytes(texto);

    private static string T(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    [Fact]
    public void Txt_acrescenta_so_linhas_novas_e_eh_idempotente()
    {
        var primeira = MescladorHistorico.Mesclar(B("22222222222,1,Maria\r\n"), B("33333333333,2,Carlos\r\n22222222222,1,Maria\r\n"), FormatoArquivo.Txt);
        var segunda = MescladorHistorico.Mesclar(primeira, B("33333333333,2,Carlos\r\n"), FormatoArquivo.Txt);

        Assert.Equal("22222222222,1,Maria\r\n33333333333,2,Carlos\r\n", T(primeira));
        Assert.Equal(primeira, segunda);
    }

    [Fact]
    public void Txt_normaliza_quebra_de_linha_e_bom_do_original()
    {
        var existente = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(B("22222222222,1,Maria\n33333333333,2,Carlos")).ToArray();

        var mesclado = MescladorHistorico.Mesclar(existente, B("44444444444,3,Luana\r\n"), FormatoArquivo.Txt);

        Assert.Equal("22222222222,1,Maria\r\n33333333333,2,Carlos\r\n44444444444,3,Luana\r\n", T(mesclado));
    }

    [Fact]
    public void Json_une_os_elementos_dos_dois_arrays_sem_repetir()
    {
        var a = SerializadorPartes.SerializarRegistros(FormatoArquivo.Json, [Registro("22222222222", "1", "Maria")]);
        var b = SerializadorPartes.SerializarRegistros(FormatoArquivo.Json, [Registro("33333333333", "2", "Carlos"), Registro("22222222222", "1", "Maria")]);
        var esperado = SerializadorPartes.SerializarRegistros(FormatoArquivo.Json, [Registro("22222222222", "1", "Maria"), Registro("33333333333", "2", "Carlos")]);

        var mesclado = MescladorHistorico.Mesclar(a, b, FormatoArquivo.Json);

        Assert.Equal(T(esperado), T(mesclado));
        Assert.Equal(mesclado, MescladorHistorico.Mesclar(mesclado, b, FormatoArquivo.Json));
    }

    [Fact]
    public void Conteudo_invalido_eh_acrescentado_como_bloco_uma_unica_vez()
    {
        var mesclado = MescladorHistorico.Mesclar(B("[ {"), B("{ x"), FormatoArquivo.Json);

        Assert.Equal("[ {\r\n{ x", T(mesclado));
        Assert.Equal(mesclado, MescladorHistorico.Mesclar(mesclado, B("{ x"), FormatoArquivo.Json));
    }

    [Fact]
    public async Task Gravar_cria_mescla_e_repete_sem_efeito_mantendo_o_nome()
    {
        var ct = TestContext.Current.CancellationToken;
        var destino = Path.Combine(_pasta, "20261111103000.txt");

        Assert.Equal(ResultadoHistorico.Gravado, await MescladorHistorico.GravarAsync(destino, B("22222222222,1,Maria\r\n"), ct));
        Assert.Equal(ResultadoHistorico.JaExistia, await MescladorHistorico.GravarAsync(destino, B("22222222222,1,Maria\r\n"), ct));
        Assert.Equal(ResultadoHistorico.Mesclado, await MescladorHistorico.GravarAsync(destino, B("33333333333,2,Carlos\r\n"), ct));
        Assert.Equal(ResultadoHistorico.JaExistia, await MescladorHistorico.GravarAsync(destino, B("33333333333,2,Carlos\r\n"), ct));

        Assert.Equal(["20261111103000.txt"], Directory.GetFiles(_pasta).Select(Path.GetFileName));
        Assert.Equal("22222222222,1,Maria\r\n33333333333,2,Carlos\r\n", await File.ReadAllTextAsync(destino, ct));
    }

    [Fact]
    public async Task Mover_com_destino_diferente_mescla_e_remove_a_origem()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_pasta, "in"));
        var origem = Path.Combine(_pasta, "in", "a.txt");
        var destino = Path.Combine(_pasta, "a.txt");
        await File.WriteAllTextAsync(destino, "22222222222,1,Maria\r\n", ct);
        await File.WriteAllTextAsync(origem, "33333333333,2,Carlos\r\n", ct);

        Assert.Equal(ResultadoHistorico.Mesclado, await MescladorHistorico.MoverAsync(origem, destino, ct));
        Assert.Equal(ResultadoHistorico.JaExistia, await MescladorHistorico.MoverAsync(origem, destino, ct));

        Assert.False(File.Exists(origem));
        Assert.Equal("22222222222,1,Maria\r\n33333333333,2,Carlos\r\n", await File.ReadAllTextAsync(destino, ct));
        Assert.Empty(Directory.GetFiles(_pasta, "*.tmp"));
    }

    private static RegistroEntrada Registro(string cpf, string id, string nome) =>
        new(0, cpf, cpf, cpf, id, nome, "k", ValidacaoRegistro.Valido);

    public void Dispose()
    {
        if (Directory.Exists(_pasta))
        {
            Directory.Delete(_pasta, recursive: true);
        }
    }
}
