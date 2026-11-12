using InativacaoUsuarioInter.Service.Arquivos;
using InativacaoUsuarioInter.Tests.Comum;
using Microsoft.Data.Sqlite;

namespace InativacaoUsuarioInter.Tests.Integration.Idempotencia;

/// <summary>
/// T088 — execução dupla de cada mutação (Princípio III, gate 3): inativação, append de Auditoria e Falhas,
/// cada tipo de parte (sucesso, falhas, rejeitado), e entrega. A mesma entrada vista N vezes produz o mesmo
/// estado final (banco, pastas, logs, diário).
/// </summary>
public sealed class ExecucaoDuplaTests(SqlServerFixture sql)
{
    private const string Misto = "22222222222,1,Maria\r\n44444444444,2,Inexistente\r\nxx,3,Invalido\r\n";
    private const string Invalido = "[ { \"cpf\": ";

    private static async Task<string> FotografarAsync(Cenario cenario)
    {
        var ct = TestContext.Current.CancellationToken;
        var partes = new List<string>();
        foreach (var raiz in new[] { cenario.Ambiente.RaizInter, cenario.Ambiente.RaizCondominio })
        {
            var arquivos = Directory.Exists(raiz)
                ? Directory.GetFiles(raiz, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray()
                : [];
            foreach (var arquivo in arquivos)
            {
                var relativo = Path.GetRelativePath(cenario.Ambiente.Raiz, arquivo);
                var conteudo = relativo.EndsWith(".traceparent", StringComparison.Ordinal)
                    ? "<trace>"
                    : await HashConteudo.CalcularArquivoAsync(arquivo, ct);
                partes.Add($"{relativo}={conteudo}");
            }
        }

        partes.AddRange((await cenario.Banco.ObterSnapshotAsync(ct)).Select(l => $"db:{l.Key}={l.Value}"));
        partes.Add($"historico={await cenario.Banco.ContarHistoricoAsync(ct)}");

        SqliteConnection.ClearAllPools();
        await using var diario = new SqliteConnection($"Data Source={Path.Combine(cenario.Ambiente.DiretorioEstado, "estado.db")};Pooling=False");
        await diario.OpenAsync(ct);
        foreach (var consulta in new[]
        {
            "SELECT 'arquivo', COUNT(*) FROM arquivo",
            "SELECT 'registro', COUNT(*) || '/' || SUM(log_gravado) FROM registro",
            "SELECT 'parte', COUNT(*) FROM parte_gerada",
            "SELECT 'entrega', COUNT(*) || '/' || SUM(estado = 'Entregue') FROM entrega",
        })
        {
            await using var comando = diario.CreateCommand();
            comando.CommandText = consulta;
            await using var leitor = await comando.ExecuteReaderAsync(ct);
            await leitor.ReadAsync(ct);
            partes.Add($"{leitor.GetString(0)}={leitor.GetValue(1)}");
        }

        return string.Join('\n', partes);
    }

    [Fact]
    public async Task Mesma_entrada_reprocessada_produz_estado_final_identico()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        await cenario.Banco.InserirUsuarioAsync("33333333333", 1, cancelamento: ct);
        cenario.Ambiente.Depositar("misto.txt", Misto);
        cenario.Ambiente.Depositar("ja-inativo.txt", "33333333333,9,Ana\r\n");
        cenario.Ambiente.Depositar("invalido.json", Invalido);

        await cenario.CicloAsync(ct);
        var primeira = await FotografarAsync(cenario);

        // Reentrega das mesmas entradas + ciclos repetidos + reinício.
        cenario.Ambiente.Depositar("misto.txt", Misto);
        cenario.Ambiente.Depositar("ja-inativo.txt", "33333333333,9,Ana\r\n");
        cenario.Ambiente.Depositar("invalido.json", Invalido);
        await cenario.CicloAsync(ct);
        await cenario.CicloAsync(ct);
        await cenario.ReiniciarAsync();
        await cenario.CicloAsync(ct);
        var segunda = await FotografarAsync(cenario);

        Assert.Equal(primeira, segunda);
        Assert.Equal(2, cenario.Ambiente.LinhasLog("Logs").Length);
        Assert.Equal(3, cenario.Ambiente.LinhasLog("Fail").Length);
        Assert.Empty(cenario.Ambiente.Arquivos("Input"));
        // 22222222222 inativado + 33333333333 (já inativo, sem marca) recebe só a marca de download (feature 002, FR-015):
        // duas alterações na primeira passada e nenhuma nas seguintes.
        Assert.Equal(2, await cenario.Banco.ContarHistoricoAsync(ct));
        Assert.Equal([((short)1, (bool?)true)], await cenario.Banco.ObterEstadosDownloadAsync("33333333333", ct));
    }
}
