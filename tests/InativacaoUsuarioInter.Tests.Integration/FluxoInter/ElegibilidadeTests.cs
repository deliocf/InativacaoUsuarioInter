using InativacaoUsuarioInter.Tests.Comum;

namespace InativacaoUsuarioInter.Tests.Integration.FluxoInter;

/// <summary>T034 — arquivo em escrita ou instável não é lido (CA-14).</summary>
public sealed class ElegibilidadeTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Arquivo_aberto_para_escrita_eh_adiado_e_processado_depois()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        var caminho = cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");

        await using (new FileStream(caminho, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            await cenario.CicloAsync(ct);
            Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Input"));
            Assert.Equal([(short)0], await cenario.Banco.ObterEstadosAsync("22222222222", ct));
        }

        await cenario.CicloAsync(ct);

        Assert.Empty(cenario.Ambiente.Arquivos("Input"));
        Assert.Equal([(short)1], await cenario.Banco.ObterEstadosAsync("22222222222", ct));
    }

    [Fact]
    public async Task Arquivo_escrito_ha_menos_que_a_estabilizacao_eh_adiado()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(
            sql,
            ajustarAmbiente: a =>
            {
                a.Configuracao["Processamento:EstabilizacaoSegundos"] = "5";
                a.UsarRelogioSimulado();
            },
            cancelamento: ct);
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        var caminho = cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        File.SetLastWriteTimeUtc(caminho, cenario.Ambiente.Relogio!.GetUtcNow().UtcDateTime);

        await cenario.CicloAsync(ct);
        Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Input"));

        cenario.Ambiente.Relogio.Advance(TimeSpan.FromSeconds(6));
        await cenario.CicloAsync(ct);

        Assert.Empty(cenario.Ambiente.Arquivos("Input"));
        Assert.Equal([(short)1], await cenario.Banco.ObterEstadosAsync("22222222222", ct));
    }
}
