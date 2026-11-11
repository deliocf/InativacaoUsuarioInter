using System.Diagnostics;
using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Telemetria;
using InativacaoUsuarioInter.Tests.Comum;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;

namespace InativacaoUsuarioInter.Tests.Integration.Telemetria;

/// <summary>
/// T087 — cobertura total de telemetria (Princípio IV, SC-008, gate 4): toda operação de domínio de US1–US5
/// tem span com os atributos obrigatórios; nenhum CPF, nome ou senha aparece em spans, métricas, logs técnicos
/// ou <c>saude.json</c>.
/// </summary>
public sealed class CoberturaTelemetriaTests(SqlServerFixture sql)
{
    private static readonly string[] Cpfs = ["22222222222", "33333333333", "44444444444", "55555555555"];
    private static readonly string[] Nomes = ["Maria Eduarda", "Carlos Renato", "Luana Figueiredo", "Pedro Invalido"];

    [Fact]
    public async Task Todas_as_operacoes_de_dominio_rastreadas_e_sem_dados_sensiveis()
    {
        var ct = TestContext.Current.CancellationToken;
        using var telemetria = new ColetorTelemetria();
        var bancoInter = await sql.CriarBancoAsync(ct);
        var bancoCondominio = await sql.CriarBancoAsync(ct);
        foreach (var banco in new[] { bancoInter, bancoCondominio })
        {
            await banco.InserirUsuarioAsync(Cpfs[0], 0, cancelamento: ct);
            await banco.InserirUsuarioAsync(Cpfs[1], 1, cancelamento: ct);
        }

        using var ambiente = AmbienteTeste.Criar(bancoInter.StringConexao);
        ambiente.Depositar("a.txt", $"{Cpfs[0]},1,{Nomes[0]}\r\n{Cpfs[1]},2,{Nomes[1]}\r\n{Cpfs[2]},3,{Nomes[2]}\r\n{Cpfs[3]}x,4,{Nomes[3]}\r\n");
        ambiente.Depositar("b.json", "[ { \"cpf\": ");

        await using (var inter = ambiente.CriarServicos())
        {
            await inter.GetRequiredService<IOrquestradorCiclo>().ExecutarCicloAsync(ct);
        }

        foreach (var arquivo in Directory.GetFiles(ambiente.PastaCondominio("Imput")))
        {
            File.SetLastWriteTimeUtc(arquivo, DateTime.UtcNow.AddHours(-1));
        }

        await using (var condominio = ambiente.CriarServicos(sobrescrever: ambiente.ComoCondominio(bancoCondominio.StringConexao)))
        {
            await condominio.GetRequiredService<IOrquestradorCiclo>().ExecutarCicloAsync(ct);
        }

        SqliteConnection.ClearAllPools();
        telemetria.Coletar();
        var spans = telemetria.Spans;

        foreach (var nome in new[] { "ciclo", "arquivo", "registro", "waccess.ler_estado", "waccess.inativar", "arquivo.mover", "replicacao.copiar" })
        {
            Assert.Contains(spans, s => s.DisplayName == nome);
        }

        Assert.All(spans, s => Assert.NotNull(s.GetTagItem(Instrumentacao.Atributos.Instancia)));
        Exigir(spans, "arquivo", Instrumentacao.Atributos.Arquivo, Instrumentacao.Atributos.ChaveIdempotencia, Instrumentacao.Atributos.Resultado);
        Exigir(spans, "registro", Instrumentacao.Atributos.Arquivo, Instrumentacao.Atributos.ChaveIdempotencia, Instrumentacao.Atributos.Resultado, Instrumentacao.Atributos.Tentativa);
        Exigir(spans, "replicacao.copiar", Instrumentacao.Atributos.Arquivo, Instrumentacao.Atributos.ChaveIdempotencia, Instrumentacao.Atributos.Resultado, Instrumentacao.Atributos.Tentativa);
        Exigir(spans, "arquivo.mover", Instrumentacao.Atributos.Arquivo, Instrumentacao.Atributos.ChaveIdempotencia);
        Assert.Contains(spans, s => Equals(s.GetTagItem(Instrumentacao.Atributos.Instancia), "Condominio"));

        var senha = new SqlConnectionStringBuilder(bancoInter.StringConexao).Password;
        var artefatos = string.Join(
            '\n',
            spans.Select(s => s.DisplayName)
                .Concat(spans.SelectMany(s => s.TagObjects.Select(t => $"{t.Key}={t.Value}")))
                .Concat(TextosMetricas(telemetria.Metricas))
                .Append(ambiente.Logs.TextoCompleto())
                .Append(await File.ReadAllTextAsync(Path.Combine(ambiente.DiretorioEstado, "saude.json"), ct))
                .Append(await File.ReadAllTextAsync(Path.Combine(ambiente.Raiz, "estado-Condominio", "saude.json"), ct)));

        foreach (var sensivel in Cpfs.Concat(Nomes).Append(senha).Append("User ID="))
        {
            Assert.DoesNotContain(sensivel, artefatos, StringComparison.Ordinal);
        }
    }

    private static void Exigir(List<Activity> spans, string nome, params string[] atributos)
    {
        foreach (var span in spans.Where(s => s.DisplayName == nome))
        {
            foreach (var atributo in atributos)
            {
                Assert.True(span.GetTagItem(atributo) is not null, $"Span '{nome}' sem '{atributo}'.");
            }
        }
    }

    private static IEnumerable<string> TextosMetricas(List<Metric> metricas)
    {
        foreach (var metrica in metricas)
        {
            yield return metrica.Name;
            foreach (ref readonly var ponto in metrica.GetMetricPoints())
            {
                foreach (var tag in ponto.Tags)
                {
                    yield return $"{tag.Key}={tag.Value}";
                }
            }
        }
    }
}
