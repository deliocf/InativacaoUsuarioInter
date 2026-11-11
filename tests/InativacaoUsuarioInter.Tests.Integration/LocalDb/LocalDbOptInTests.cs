using System.Text.Json;
using InativacaoUsuarioInter.Tests.Comum;
using Microsoft.Data.SqlClient;

namespace InativacaoUsuarioInter.Tests.Integration.LocalDb;

/// <summary>
/// T054 — CA-20, opt-in (<c>WACCESS_TESTE_LOCALDB=1</c>): inativação real no banco <b>local de testes</b>
/// configurado no <c>appsettings.json</c> local (documento-base §4.1). O <c>CHState</c> original é restaurado.
/// </summary>
public sealed class LocalDbOptInTests
{
    private static string RaizRepositorio()
    {
        var diretorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (diretorio is not null && !File.Exists(Path.Combine(diretorio.FullName, "global.json")))
        {
            diretorio = diretorio.Parent;
        }

        return diretorio!.FullName;
    }

    [Fact]
    public async Task CA20_inativacao_real_no_banco_local_com_restauracao()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("WACCESS_TESTE_LOCALDB") == "1",
            "Opt-in: defina WACCESS_TESTE_LOCALDB=1 para alterar o banco local de testes.");
        var ct = TestContext.Current.CancellationToken;

        var appsettings = Path.Combine(RaizRepositorio(), "src", "InativacaoUsuarioInter.Service", "appsettings.json");
        using var documento = JsonDocument.Parse(await File.ReadAllTextAsync(appsettings, ct));
        var conexao = documento.RootElement.GetProperty("ConnectionString").GetProperty("WAccess").GetString()!;
        var senha = new SqlConnectionStringBuilder(conexao).Password;

        var originais = await EscolherCpfsAsync(conexao, ct);
        Assert.Equal(2, originais.Count);
        using var ambiente = AmbienteTeste.Criar(conexao);
        try
        {
            await using var servicos = ambiente.CriarServicos();
            ambiente.Depositar("20261111103000.txt", string.Concat(originais.Select((o, i) => $"{o.Cpf},{i + 1},Responsavel Teste\r\n")));

            await ((Service.Ciclo.IOrquestradorCiclo)servicos.GetService(typeof(Service.Ciclo.IOrquestradorCiclo))!).ExecutarCicloAsync(ct);

            foreach (var (cpf, _) in originais)
            {
                Assert.Equal(1, await LerEstadoAsync(conexao, cpf, ct));
            }

            Assert.Equal(2, ambiente.LinhasLog("Auditoria").Length);
            var textos = ambiente.Logs.TextoCompleto() + string.Join('\n', ambiente.LinhasLog("Auditoria")) + string.Join('\n', ambiente.LinhasLog("Falhas"));
            Assert.DoesNotContain(senha, textos, StringComparison.Ordinal);
            Assert.Contains("Password=", await File.ReadAllTextAsync(appsettings, ct), StringComparison.Ordinal);
        }
        finally
        {
            foreach (var (cpf, estado) in originais)
            {
                await RestaurarAsync(conexao, cpf, estado, ct);
            }
        }
    }

    private static async Task<List<(string Cpf, short Estado)>> EscolherCpfsAsync(string conexao, CancellationToken ct)
    {
        await using var c = new SqlConnection(conexao);
        await c.OpenAsync(ct);
        await using var comando = c.CreateCommand();
        comando.CommandText = """
            SELECT TOP 2 IdNumber, CHState FROM dbo.CHMain
            WHERE CHState <> 1 AND LEN(IdNumber) = 11 AND IdNumber NOT LIKE '%[^0-9]%'
              AND IdNumber IN (SELECT IdNumber FROM dbo.CHMain GROUP BY IdNumber HAVING COUNT(*) = 1)
            ORDER BY CHID;
            """;
        await using var leitor = await comando.ExecuteReaderAsync(ct);
        var lista = new List<(string, short)>();
        while (await leitor.ReadAsync(ct))
        {
            lista.Add((leitor.GetString(0), leitor.GetInt16(1)));
        }

        return lista;
    }

    private static async Task<short> LerEstadoAsync(string conexao, string cpf, CancellationToken ct)
    {
        await using var c = new SqlConnection(conexao);
        await c.OpenAsync(ct);
        await using var comando = c.CreateCommand();
        comando.CommandText = "SELECT CHState FROM dbo.CHMain WHERE IdNumber = @c;";
        comando.Parameters.Add(new SqlParameter("@c", System.Data.SqlDbType.VarChar, 50) { Value = cpf });
        return (short)(await comando.ExecuteScalarAsync(ct))!;
    }

    // Restauração exclusiva do teste (não é funcionalidade do serviço).
    private static async Task RestaurarAsync(string conexao, string cpf, short estado, CancellationToken ct)
    {
        await using var c = new SqlConnection(conexao);
        await c.OpenAsync(CancellationToken.None);
        await using var comando = c.CreateCommand();
        comando.CommandText = "UPDATE dbo.CHMain SET CHState = @e WHERE IdNumber = @c;";
        comando.Parameters.Add(new SqlParameter("@e", estado));
        comando.Parameters.Add(new SqlParameter("@c", System.Data.SqlDbType.VarChar, 50) { Value = cpf });
        await comando.ExecuteNonQueryAsync(CancellationToken.None);
        _ = ct;
    }
}
