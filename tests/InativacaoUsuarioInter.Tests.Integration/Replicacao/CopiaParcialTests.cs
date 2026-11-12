using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Entrada;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Tests.Comum;
using Microsoft.Extensions.DependencyInjection;

namespace InativacaoUsuarioInter.Tests.Integration.Replicacao;

/// <summary>T075 — cópia interrompida antes do rename: o receptor nunca vê arquivo parcial (CA-14).</summary>
public sealed class CopiaParcialTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Temporario_orfao_nao_eh_elegivel_e_eh_substituido_no_ciclo_seguinte()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(
            sql, ajustarServicos: s => s.AddSingleton<IPontoFalha>(new PontoFalhaEmProcesso(PontoFalha.AposCopiaTemporaria)), cancelamento: ct);
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");

        await Assert.ThrowsAsync<InterrupcaoSimuladaException>(() => cenario.CicloAsync(ct));

        var noDestino = cenario.Ambiente.Arquivos("Input", cenario.Ambiente.RaizCondominio);
        Assert.Equal(["a.txt.tmp"], noDestino);
        Assert.All(noDestino, a => Assert.Null(LeitorArquivo.FormatoPorExtensao(a)));

        await cenario.ReiniciarAsync(s => s.AddSingleton<IPontoFalha>(new PontoFalhaNulo()));
        await cenario.CicloAsync(ct);

        // O sidecar .traceparent só existe com rastreamento ativo (ver ReplicacaoContratoTests); aqui não há ouvinte.
        Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Input", cenario.Ambiente.RaizCondominio).Where(a => !a.EndsWith(".traceparent", StringComparison.Ordinal)));
        Assert.Equal("22222222222,1,Maria\r\n", await File.ReadAllTextAsync(cenario.Ambiente.PastaCondominio("Input", "a.txt"), ct));

        // Feature 002: depois da entrega confirmada a fila Output fica vazia; o histórico está em Processed.
        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
        Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Processed"));
    }
}
