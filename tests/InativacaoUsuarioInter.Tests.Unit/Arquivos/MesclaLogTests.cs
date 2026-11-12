using System.Text;
using InativacaoUsuarioInter.Service.Arquivos;

namespace InativacaoUsuarioInter.Tests.Unit.Arquivos;

/// <summary>Feature 002, T009 — mescla idempotente dos logs antigos (research R-06).</summary>
public sealed class MesclaLogTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "mescla-" + Guid.NewGuid().ToString("N"));
    private readonly string _antigo;
    private readonly string _destino;

    public MesclaLogTests()
    {
        Directory.CreateDirectory(Path.Combine(_pasta, "Auditoria"));
        Directory.CreateDirectory(Path.Combine(_pasta, "Logs"));
        _antigo = Path.Combine(_pasta, "Auditoria", "log_inativacao.log");
        _destino = Path.Combine(_pasta, "Logs", "log_inativacao.log");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task Gravar(string caminho, string texto) => File.WriteAllTextAsync(caminho, texto, new UTF8Encoding(false), Ct);

    [Fact]
    public async Task Sem_log_antigo_nada_acontece()
    {
        Assert.Equal(ResultadoMescla.SemLogAntigo, await MesclaLog.MesclarAsync(_antigo, _destino, null, Ct));
        Assert.False(File.Exists(_destino));
    }

    [Fact]
    public async Task Destino_inexistente_recebe_o_conteudo_antigo()
    {
        await Gravar(_antigo, "a\r\nb\r\n");

        Assert.Equal(ResultadoMescla.Mesclado, await MesclaLog.MesclarAsync(_antigo, _destino, null, Ct));

        Assert.Equal("a\r\nb\r\n", await File.ReadAllTextAsync(_destino, Ct));
        Assert.False(File.Exists(_antigo));
    }

    [Fact]
    public async Task Destino_existente_fica_com_antigo_antes_do_atual_byte_a_byte()
    {
        await Gravar(_antigo, "a\r\nb\r\n");
        await Gravar(_destino, "c\r\n");

        await MesclaLog.MesclarAsync(_antigo, _destino, null, Ct);

        Assert.Equal("a\r\nb\r\nc\r\n", await File.ReadAllTextAsync(_destino, Ct));
        Assert.False(File.Exists(_antigo));
        Assert.False(File.Exists(_destino + ".tmp"));
    }

    [Fact]
    public async Task Antigo_sem_quebra_final_nao_cola_linhas()
    {
        await Gravar(_antigo, "a");
        await Gravar(_destino, "c\r\n");

        await MesclaLog.MesclarAsync(_antigo, _destino, null, Ct);

        Assert.Equal("a\r\nc\r\n", await File.ReadAllTextAsync(_destino, Ct));
    }

    [Fact]
    public async Task Interrupcao_depois_da_troca_e_retomada_nao_duplica()
    {
        await Gravar(_antigo, "a\r\n");
        await Gravar(_destino, "c\r\n");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => MesclaLog.MesclarAsync(_antigo, _destino, () => throw new InvalidOperationException("crash"), Ct));
        Assert.True(File.Exists(_antigo));

        Assert.Equal(ResultadoMescla.JaMesclado, await MesclaLog.MesclarAsync(_antigo, _destino, null, Ct));

        Assert.Equal("a\r\nc\r\n", await File.ReadAllTextAsync(_destino, Ct));
        Assert.False(File.Exists(_antigo));
    }

    [Fact]
    public async Task Antigo_vazio_eh_apenas_removido()
    {
        await Gravar(_antigo, string.Empty);
        await Gravar(_destino, "c\r\n");

        Assert.Equal(ResultadoMescla.JaMesclado, await MesclaLog.MesclarAsync(_antigo, _destino, null, Ct));

        Assert.Equal("c\r\n", await File.ReadAllTextAsync(_destino, Ct));
        Assert.False(File.Exists(_antigo));
    }

    [Fact]
    public async Task Log_maior_que_o_buffer_eh_copiado_integralmente()
    {
        var linhas = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"data=2026-11-12 | cpf=000000{i:D5} | status=INATIVADO\r\n"));
        await Gravar(_antigo, linhas);
        await Gravar(_destino, "fim\r\n");

        await MesclaLog.MesclarAsync(_antigo, _destino, null, Ct);
        Assert.Equal(linhas + "fim\r\n", await File.ReadAllTextAsync(_destino, Ct));

        // Segunda passada com um "antigo" igual (restauração de backup): reconhecido como já mesclado.
        await Gravar(_antigo, linhas);
        Assert.Equal(ResultadoMescla.JaMesclado, await MesclaLog.MesclarAsync(_antigo, _destino, null, Ct));
    }

    public void Dispose()
    {
        if (Directory.Exists(_pasta))
        {
            Directory.Delete(_pasta, recursive: true);
        }
    }
}
