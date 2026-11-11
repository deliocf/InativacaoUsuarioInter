using System.Diagnostics;
using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Tests.Comum;
using Microsoft.Extensions.DependencyInjection;

namespace InativacaoUsuarioInter.Tests.Integration.InjecaoFalhas;

/// <summary>
/// T061 — crash no meio do arquivo e retomada (CA-08, FR-021, análises C2/H1): em processo, para cada ponto,
/// e com processo real encerrado por <see cref="Environment.FailFast(string)"/>.
/// </summary>
public sealed class CrashRetomadaTests(SqlServerFixture sql)
{
    // 1º registro já inativo antes do processamento; 2º e 3º ativos.
    private const string Conteudo = "11111111111,1,Ana\r\n22222222222,2,Maria\r\n33333333333,3,Carlos\r\n";

    private static async Task SemearAsync(BancoTeste banco, CancellationToken ct)
    {
        await banco.InserirUsuarioAsync("11111111111", 1, cancelamento: ct);
        await banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        await banco.InserirUsuarioAsync("33333333333", 0, cancelamento: ct);
    }

    private static async Task VerificarAsync(AmbienteTeste ambiente, BancoTeste banco, CancellationToken ct)
    {
        foreach (var cpf in new[] { "11111111111", "22222222222", "33333333333" })
        {
            Assert.Equal([(short)1], await banco.ObterEstadosAsync(cpf, ct));
        }

        var auditoria = ambiente.LinhasLog("Auditoria");
        Assert.Equal(3, auditoria.Length);
        Assert.EndsWith("status=JA_INATIVO", Assert.Single(auditoria, l => l.Contains("cpf=11111111111", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.EndsWith("status=INATIVADO", Assert.Single(auditoria, l => l.Contains("cpf=22222222222", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.EndsWith("status=INATIVADO", Assert.Single(auditoria, l => l.Contains("cpf=33333333333", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Empty(ambiente.LinhasLog("Falhas"));
        Assert.Equal(["20261111103000.txt"], ambiente.Arquivos("Output"));
        Assert.Equal(Conteudo, await File.ReadAllTextAsync(ambiente.Pasta("Output", "20261111103000.txt"), ct));
        Assert.Empty(ambiente.Arquivos("Imput"));
        Assert.Equal(2, await banco.ContarHistoricoAsync(ct));
    }

    [Theory]
    [InlineData(PontoFalha.AposIntencao, 1)]
    [InlineData(PontoFalha.AposIntencao, 2)]
    [InlineData(PontoFalha.AposComandoWAccess, 1)]
    [InlineData(PontoFalha.AposComandoWAccess, 2)]
    [InlineData(PontoFalha.AposRegistrarResultado, 2)]
    [InlineData(PontoFalha.AposAppendLog, 1)]
    [InlineData(PontoFalha.AposAppendLog, 3)]
    [InlineData(PontoFalha.AposGravarPartes, 1)]
    [InlineData(PontoFalha.AntesRemoverOriginal, 1)]
    public async Task Crash_em_processo_e_retomada_sem_perda_nem_duplicacao(PontoFalha ponto, int ocorrencia)
    {
        var ct = TestContext.Current.CancellationToken;
        var gatilho = new PontoFalhaEmProcesso(ponto, ocorrencia);
        await using var cenario = await Cenario.CriarAsync(sql, ajustarServicos: s => s.AddSingleton<IPontoFalha>(gatilho), cancelamento: ct);
        await SemearAsync(cenario.Banco, ct);
        cenario.Ambiente.Depositar("20261111103000.txt", Conteudo);

        await Assert.ThrowsAsync<InterrupcaoSimuladaException>(() => cenario.CicloAsync(ct));
        Assert.True(gatilho.Disparado);

        await cenario.ReiniciarAsync(s => s.AddSingleton<IPontoFalha>(new PontoFalhaNulo()));
        await cenario.CicloAsync(ct);
        await cenario.CicloAsync(ct);

        await VerificarAsync(cenario.Ambiente, cenario.Banco, ct);
    }

    [Theory]
    [InlineData(PontoFalha.AposComandoWAccess)]
    [InlineData(PontoFalha.AposRegistrarResultado)]
    [InlineData(PontoFalha.AposGravarPartes)]
    public async Task Crash_de_processo_real_com_FailFast_e_retomada(PontoFalha ponto)
    {
        var ct = TestContext.Current.CancellationToken;
        var banco = await sql.CriarBancoAsync(ct);
        await SemearAsync(banco, ct);
        using var ambiente = AmbienteTeste.Criar(banco.StringConexao);
        ambiente.Depositar("20261111103000.txt", Conteudo);

        using (var primeiro = Iniciar(ambiente, ponto))
        {
            using var garantia = new EncerrarAoSair(primeiro);
            Assert.True(await ProcessoServico.AguardarSaidaAsync(primeiro, TimeSpan.FromSeconds(90), ct), "O processo não terminou no ponto de falha.");
            Assert.NotEqual(0, primeiro.ExitCode);
        }

        using (var segundo = Iniciar(ambiente, null))
        {
            using var garantia = new EncerrarAoSair(segundo);
            var prazo = Stopwatch.StartNew();
            while (prazo.Elapsed < TimeSpan.FromSeconds(90)
                   && !(ambiente.Arquivos("Imput").Length == 0 && ambiente.LinhasLog("Auditoria").Length == 3))
            {
                await Task.Delay(500, ct);
            }

            segundo.Kill(entireProcessTree: true);
            await segundo.WaitForExitAsync(ct);
        }

        await VerificarAsync(ambiente, banco, ct);
    }

    private static Process Iniciar(AmbienteTeste ambiente, PontoFalha? ponto) =>
        ProcessoServico.Iniciar(
            ambiente,
            ponto is { } p ? new Dictionary<string, string?> { [PontoFalhaProcesso.Variavel] = p.ToString() } : null);
}
