using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace InativacaoUsuarioInter.Tests.Comum;

/// <summary>
/// SQL Server 2022 em container com a réplica mínima de <c>dbo.CHMain</c> descrita em
/// <c>contracts/waccess-inativacao.md</c> (inclui o trigger sem <c>SET NOCOUNT ON</c>).
/// Um container por assembly; um banco novo por teste via <see cref="CriarBancoAsync"/>.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string Imagem = "mcr.microsoft.com/mssql/server:2022-latest";

    private readonly MsSqlContainer _container = new MsSqlBuilder(Imagem).Build();

    public string Host => _container.Hostname;

    public int Porta => _container.GetMappedPublicPort(MsSqlBuilder.MsSqlPort);

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Cria um banco isolado com a réplica de <c>CHMain</c>.</summary>
    public async Task<BancoTeste> CriarBancoAsync(CancellationToken cancelamento = default)
    {
        var nome = "waccess_" + Guid.NewGuid().ToString("N")[..12];
        await using (var conexao = new SqlConnection(_container.GetConnectionString()))
        {
            await conexao.OpenAsync(cancelamento);
            await using var criar = conexao.CreateCommand();
            criar.CommandText = $"CREATE DATABASE [{nome}];";
            await criar.ExecuteNonQueryAsync(cancelamento);
        }

        var banco = new BancoTeste(StringConexao(nome, Host, Porta));
        await banco.ExecutarAsync(EsquemaReplica, cancelamento);
        await banco.ExecutarAsync(TriggerReplica, cancelamento);
        return banco;
    }

    /// <summary>String de conexão para o banco, opcionalmente via outro host/porta (proxy de falhas).</summary>
    public string StringConexao(string banco, string host, int porta)
    {
        var construtor = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            DataSource = string.Create(CultureInfo.InvariantCulture, $"{host},{porta}"),
            InitialCatalog = banco,
            Pooling = false,
            TrustServerCertificate = true,
            ConnectTimeout = 5,
        };
        return construtor.ConnectionString;
    }

    private const string EsquemaReplica = """
        CREATE TABLE dbo.CHMain (
          CHID int IDENTITY PRIMARY KEY,
          CHType smallint NOT NULL DEFAULT 2,
          IdNumber varchar(50) NULL,
          CHState smallint NOT NULL DEFAULT 0,
          CHDownloadRequired bit NULL,
          LastModifDateTime datetime NULL
        );
        CREATE INDEX IX_CHMain_CHType_IdNumber ON dbo.CHMain (CHType, IdNumber);
        CREATE TABLE dbo.CHMainHistoricoTeste (CHID int, CHState smallint, Em datetime2 DEFAULT sysdatetime());
        """;

    // Imita wtr_CHMainChangeCHState e infla @@ROWCOUNT (sem SET NOCOUNT ON).
    private const string TriggerReplica = """
        CREATE TRIGGER dbo.wtr_Teste_ChangeCHState ON dbo.CHMain AFTER UPDATE AS
          INSERT dbo.CHMainHistoricoTeste (CHID, CHState) SELECT CHID, CHState FROM inserted;
        """;
}

/// <summary>Banco de teste com helpers de semeadura e inspeção.</summary>
public sealed class BancoTeste(string stringConexao)
{
    public string StringConexao { get; } = stringConexao;

    public string Nome => new SqlConnectionStringBuilder(StringConexao).InitialCatalog;

    /// <param name="downloadRequired"><c>CHDownloadRequired</c> (<c>bit NULL</c>, como no esquema real; padrão <c>NULL</c>).</param>
    public async Task<int> InserirUsuarioAsync(
        string? idNumber, short chState = 0, short chType = 2, bool? downloadRequired = null, CancellationToken cancelamento = default)
    {
        await using var conexao = new SqlConnection(StringConexao);
        await conexao.OpenAsync(cancelamento);
        await using var comando = conexao.CreateCommand();
        comando.CommandText = """
            INSERT dbo.CHMain (CHType, IdNumber, CHState, CHDownloadRequired, LastModifDateTime) VALUES (@t, @i, @s, @d, '2026-01-01T00:00:00');
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """;
        comando.Parameters.Add(new SqlParameter("@t", chType));
        comando.Parameters.Add(new SqlParameter("@i", System.Data.SqlDbType.VarChar, 50) { Value = (object?)idNumber ?? DBNull.Value });
        comando.Parameters.Add(new SqlParameter("@s", chState));
        comando.Parameters.Add(new SqlParameter("@d", System.Data.SqlDbType.Bit) { Value = (object?)downloadRequired ?? DBNull.Value });
        return (int)(await comando.ExecuteScalarAsync(cancelamento))!;
    }

    /// <summary>Insere usuários ativos em lote (teste de carga).</summary>
    public async Task InserirEmLoteAsync(IReadOnlyList<string> cpfs, CancellationToken cancelamento = default)
    {
        await using var conexao = new SqlConnection(StringConexao);
        await conexao.OpenAsync(cancelamento);
        foreach (var lote in cpfs.Chunk(500))
        {
            await using var comando = conexao.CreateCommand();
            var valores = new StringBuilder();
            for (var i = 0; i < lote.Length; i++)
            {
                valores.Append(i == 0 ? string.Empty : ",").Append(CultureInfo.InvariantCulture, $"(2, @p{i}, 0)");
                comando.Parameters.Add(new SqlParameter($"@p{i}", System.Data.SqlDbType.VarChar, 50) { Value = lote[i] });
            }

            comando.CommandText = $"INSERT dbo.CHMain (CHType, IdNumber, CHState) VALUES {valores};";
            await comando.ExecuteNonQueryAsync(cancelamento);
        }
    }

    public async Task<IReadOnlyList<short>> ObterEstadosAsync(string cpf, CancellationToken cancelamento = default)
    {
        await using var conexao = new SqlConnection(StringConexao);
        await conexao.OpenAsync(cancelamento);
        await using var comando = conexao.CreateCommand();
        comando.CommandText = "SELECT CHState FROM dbo.CHMain WHERE IdNumber = @c ORDER BY CHID;";
        comando.Parameters.Add(new SqlParameter("@c", System.Data.SqlDbType.VarChar, 50) { Value = cpf });
        await using var leitor = await comando.ExecuteReaderAsync(cancelamento);
        var estados = new List<short>();
        while (await leitor.ReadAsync(cancelamento))
        {
            estados.Add(leitor.GetInt16(0));
        }

        return estados;
    }

    /// <summary><c>(CHState, CHDownloadRequired)</c> de cada linha do CPF, em ordem de <c>CHID</c> (feature 002, US4).</summary>
    public async Task<IReadOnlyList<(short Estado, bool? Download)>> ObterEstadosDownloadAsync(string cpf, CancellationToken cancelamento = default)
    {
        await using var conexao = new SqlConnection(StringConexao);
        await conexao.OpenAsync(cancelamento);
        await using var comando = conexao.CreateCommand();
        comando.CommandText = "SELECT CHState, CHDownloadRequired FROM dbo.CHMain WHERE IdNumber = @c ORDER BY CHID;";
        comando.Parameters.Add(new SqlParameter("@c", System.Data.SqlDbType.VarChar, 50) { Value = cpf });
        await using var leitor = await comando.ExecuteReaderAsync(cancelamento);
        var linhas = new List<(short, bool?)>();
        while (await leitor.ReadAsync(cancelamento))
        {
            linhas.Add((leitor.GetInt16(0), leitor.IsDBNull(1) ? null : leitor.GetBoolean(1)));
        }

        return linhas;
    }

    /// <summary>Todas as linhas e colunas de <c>CHMain</c>, para comparação antes/depois (SC-002).</summary>
    public async Task<IReadOnlyDictionary<int, string>> ObterSnapshotAsync(CancellationToken cancelamento = default)
    {
        await using var conexao = new SqlConnection(StringConexao);
        await conexao.OpenAsync(cancelamento);
        await using var comando = conexao.CreateCommand();
        comando.CommandText = "SELECT CHID, CHType, IdNumber, CHState, CHDownloadRequired, CONVERT(varchar(30), LastModifDateTime, 126) FROM dbo.CHMain;";
        await using var leitor = await comando.ExecuteReaderAsync(cancelamento);
        var linhas = new Dictionary<int, string>();
        while (await leitor.ReadAsync(cancelamento))
        {
            var texto = new StringBuilder();
            for (var i = 1; i < leitor.FieldCount; i++)
            {
                texto.Append(leitor.IsDBNull(i) ? "<null>" : Convert.ToString(leitor.GetValue(i), CultureInfo.InvariantCulture)).Append('|');
            }

            linhas[leitor.GetInt32(0)] = texto.ToString();
        }

        return linhas;
    }

    public async Task<int> ContarHistoricoAsync(CancellationToken cancelamento = default)
    {
        await using var conexao = new SqlConnection(StringConexao);
        await conexao.OpenAsync(cancelamento);
        await using var comando = conexao.CreateCommand();
        comando.CommandText = "SELECT COUNT(*) FROM dbo.CHMainHistoricoTeste;";
        return (int)(await comando.ExecuteScalarAsync(cancelamento))!;
    }

    public async Task ExecutarAsync(string sql, CancellationToken cancelamento = default)
    {
        await using var conexao = new SqlConnection(StringConexao);
        await conexao.OpenAsync(cancelamento);
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync(cancelamento);
    }

    /// <summary>A mesma conexão, passando por outro host/porta (ex.: <see cref="ProxyTcpFalhas"/>).</summary>
    public string StringConexaoVia(int porta)
    {
        var construtor = new SqlConnectionStringBuilder(StringConexao)
        {
            DataSource = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1,{porta}"),
        };
        return construtor.ConnectionString;
    }
}
