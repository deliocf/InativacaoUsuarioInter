using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Tests.Comum;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace InativacaoUsuarioInter.Tests.Integration.PontaAPonta;

/// <summary>T082 — ponta a ponta: Inter e Condomínio sobre as mesmas pastas e bancos distintos.</summary>
public sealed class InterCondominioTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Inter_inativa_replica_sucessos_e_Condominio_inativa_com_o_mesmo_trace_e_sem_duplicacao()
    {
        var ct = TestContext.Current.CancellationToken;
        using var telemetria = new ColetorTelemetria();
        var bancoInter = await sql.CriarBancoAsync(ct);
        var bancoCondominio = await sql.CriarBancoAsync(ct);
        await bancoInter.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        await bancoInter.InserirUsuarioAsync("33333333333", 0, cancelamento: ct);
        foreach (var cpf in new[] { "22222222222", "33333333333", "44444444444" })
        {
            await bancoCondominio.InserirUsuarioAsync(cpf, 0, cancelamento: ct);
        }

        using var ambiente = AmbienteTeste.Criar(bancoInter.StringConexao);
        ambiente.Depositar("20261111103000.json", """
            [
              { "cpf": "22222222222", "id_responsavel": "1", "nome_responsavel": "Maria Eduarda" },
              { "cpf": "33333333333", "id_responsavel": "2", "nome_responsavel": "Carlos Renato da Silva" },
              { "cpf": "44444444444", "id_responsavel": "3", "nome_responsavel": "Luana Figueiredo" }
            ]
            """);

        for (var rodada = 0; rodada < 2; rodada++)
        {
            await using (var inter = ambiente.CriarServicos())
            {
                await inter.GetRequiredService<IOrquestradorCiclo>().ExecutarCicloAsync(ct);
            }

            // Arquivo replicado precisa estar estável para o Condomínio.
            foreach (var arquivo in Directory.GetFiles(ambiente.PastaCondominio("Input")))
            {
                File.SetLastWriteTimeUtc(arquivo, DateTime.UtcNow.AddHours(-1));
            }

            await using (var condominio = ambiente.CriarServicos(sobrescrever: ambiente.ComoCondominio(bancoCondominio.StringConexao)))
            {
                await condominio.GetRequiredService<IOrquestradorCiclo>().ExecutarCicloAsync(ct);
            }

            SqliteConnection.ClearAllPools();
        }

        Assert.Equal([(short)1], await bancoInter.ObterEstadosAsync("22222222222", ct));
        Assert.Equal([(short)1], await bancoInter.ObterEstadosAsync("33333333333", ct));
        Assert.Equal([(short)1], await bancoCondominio.ObterEstadosAsync("22222222222", ct));
        Assert.Equal([(short)1], await bancoCondominio.ObterEstadosAsync("33333333333", ct));
        Assert.Equal([(short)0], await bancoCondominio.ObterEstadosAsync("44444444444", ct));

        Assert.Equal(2, ambiente.LinhasLog("Logs", ambiente.RaizInter).Length);
        Assert.Single(ambiente.LinhasLog("Fail", ambiente.RaizInter));
        var auditoriaCondominio = ambiente.LinhasLog("Logs", ambiente.RaizCondominio);
        Assert.Equal(2, auditoriaCondominio.Length);
        Assert.All(auditoriaCondominio, l => Assert.Contains("| Condominio |", l, StringComparison.Ordinal));
        Assert.Empty(ambiente.LinhasLog("Fail", ambiente.RaizCondominio));
        Assert.Empty(ambiente.Arquivos("Input", ambiente.RaizCondominio));
        Assert.Single(ambiente.Arquivos("Processed", ambiente.RaizCondominio), a => a.EndsWith(".json", StringComparison.Ordinal));

        // Feature 002 (T039): no Inter, histórico em Processed, fila Output vazia e falhas com o nome original;
        // cada instância grava as falhas na própria raiz; inativações com a marca de download.
        Assert.Single(ambiente.Arquivos("Processed", ambiente.RaizInter), a => a.StartsWith("20261111103000__sucesso-", StringComparison.Ordinal));
        Assert.Empty(ambiente.Arquivos("Output", ambiente.RaizInter));
        Assert.Contains("20261111103000.json", ambiente.Arquivos("Fail", ambiente.RaizInter));
        Assert.DoesNotContain(ambiente.Arquivos("Fail", ambiente.RaizCondominio), a => a.EndsWith(".json", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(ambiente.RaizCondominio, "Output")));
        Assert.Equal([((short)1, (bool?)true)], await bancoCondominio.ObterEstadosDownloadAsync("22222222222", ct));
        Assert.Equal([((short)1, (bool?)true)], await bancoInter.ObterEstadosDownloadAsync("33333333333", ct));

        telemetria.Coletar();
        var copia = Assert.Single(telemetria.Spans, s => s.DisplayName == "replicacao.copiar");
        Assert.Contains(
            telemetria.Spans,
            s => s.DisplayName == "arquivo" && Equals(s.GetTagItem("instancia"), "Condominio") && s.TraceId == copia.TraceId && s.ParentSpanId == copia.SpanId);
    }
}
