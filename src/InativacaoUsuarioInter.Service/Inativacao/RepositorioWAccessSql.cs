using System.Data;
using System.Diagnostics;
using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Service.Telemetria;
using Microsoft.Data.SqlClient;

namespace InativacaoUsuarioInter.Service.Inativacao;

/// <summary>
/// Resultado do passo B (alteração): linhas que mudaram de estado (antes <c>CHState &lt;&gt; 1</c>), linhas já inativas que
/// só receberam a marca <c>CHDownloadRequired = 1</c> (feature 002, FR-015) e cadastros existentes.
/// </summary>
public readonly record struct ResultadoInativacao(int Inativados, int SomenteDownload, int Existentes);

/// <summary>Acesso ao WAccess da instância corrente (contracts/waccess-inativacao.md).</summary>
public interface IRepositorioWAccess
{
    /// <summary>Passo A — pré-leitura do estado anterior.</summary>
    Task<EstadoAnterior> LerEstadoAsync(string cpf, OrcamentoCiclo? orcamento, CancellationToken cancelamento);

    /// <summary>
    /// Passo B — <c>UPDATE</c> condicional, executado quando o estado anterior é <c>Ativo</c> ou <c>Inativo</c>
    /// (feature 002: o já inativo pode precisar só da marca de download).
    /// </summary>
    Task<ResultadoInativacao> InativarAsync(string cpf, OrcamentoCiclo? orcamento, CancellationToken cancelamento);
}

/// <summary>Classificação do resultado (research R-03): usa somente o estado anterior gravado e o passo B.</summary>
internal static class ClassificacaoWAccess
{
    public static ResultadoRegistro Classificar(EstadoAnterior anterior, ResultadoInativacao? passoB) => anterior switch
    {
        EstadoAnterior.Inexistente => ResultadoRegistro.CpfNaoEncontrado,
        EstadoAnterior.Inativo => ResultadoRegistro.JaInativo,
        EstadoAnterior.Ativo when passoB is { Existentes: >= 1 } => ResultadoRegistro.Inativado,
        EstadoAnterior.Ativo when passoB is { Existentes: 0 } => ResultadoRegistro.CpfNaoEncontrado,
        _ => throw new InvalidOperationException("Estado anterior Ativo exige o resultado do passo B."),
    };

    public static EstadoAnterior InterpretarPreLeitura(int existentes, int ativos) =>
        existentes == 0 ? EstadoAnterior.Inexistente
        : ativos == 0 ? EstadoAnterior.Inativo
        : EstadoAnterior.Ativo;
}

/// <summary>
/// Implementação com <c>Microsoft.Data.SqlClient</c>: <c>@Cpf</c> sempre <c>VarChar(50)</c> (mesmo tipo de
/// <c>IdNumber</c>, research R-01), contagem por <c>OUTPUT … INTO</c> (trigger presente, R-03), timeouts da
/// configuração e execução no pipeline <c>waccess</c>.
/// </summary>
internal sealed class RepositorioWAccessSql : IRepositorioWAccess
{
    public const string SqlPreLeitura = """
        SELECT COUNT(*) AS Existentes,
               SUM(CASE WHEN CHState <> 1 THEN 1 ELSE 0 END) AS Ativos
        FROM dbo.CHMain WHERE IdNumber = @Cpf;
        """;

    // Feature 002 (contracts/waccess-inativacao.md v2, research R-01/R-02): CHDownloadRequired é bit NULL;
    // NULL e 0 significam "sem marca". deleted.CHState separa inativações de marcações só de download.
    public const string SqlInativacao = """
        DECLARE @afetados TABLE (CHID int, EstadoAnterior smallint);
        UPDATE dbo.CHMain SET CHState = 1, CHDownloadRequired = 1
          OUTPUT inserted.CHID, deleted.CHState INTO @afetados
          WHERE IdNumber = @Cpf
            AND (CHState <> 1 OR CHDownloadRequired IS NULL OR CHDownloadRequired = 0);
        SELECT (SELECT COUNT(*) FROM @afetados WHERE EstadoAnterior <> 1)  AS Inativados,
               (SELECT COUNT(*) FROM @afetados WHERE EstadoAnterior = 1)   AS SomenteDownload,
               (SELECT COUNT(*) FROM dbo.CHMain WHERE IdNumber = @Cpf)     AS Existentes;
        """;

    private readonly ProvedorPipelines _pipelines;
    private readonly string _stringConexao;
    private readonly int _timeoutComando;
    private readonly string _instancia;

    public RepositorioWAccessSql(ConfiguracaoServico configuracao, ProvedorPipelines pipelines)
    {
        ArgumentNullException.ThrowIfNull(configuracao);
        _pipelines = pipelines;
        _instancia = configuracao.Instancia.ToString();
        _timeoutComando = configuracao.Resiliencia.WAccess.TimeoutComandoSegundos;
        _stringConexao = new SqlConnectionStringBuilder(configuracao.ConexaoWAccess)
        {
            ConnectTimeout = configuracao.Resiliencia.WAccess.TimeoutConexaoSegundos,
        }.ConnectionString;
    }

    public Task<EstadoAnterior> LerEstadoAsync(string cpf, OrcamentoCiclo? orcamento, CancellationToken cancelamento) =>
        _pipelines.ExecutarAsync(
            Dependencias.WAccess,
            async ct =>
            {
                using var atividade = IniciarSpan("waccess.ler_estado", "SELECT");
                await using var conexao = new SqlConnection(_stringConexao);
                await conexao.OpenAsync(ct);
                await using var comando = Comando(conexao, SqlPreLeitura, cpf);
                await using var leitor = await comando.ExecuteReaderAsync(ct);
                await leitor.ReadAsync(ct);
                var existentes = leitor.GetInt32(0);
                var ativos = leitor.IsDBNull(1) ? 0 : leitor.GetInt32(1);
                return ClassificacaoWAccess.InterpretarPreLeitura(existentes, ativos);
            },
            orcamento,
            cancelamento);

    public Task<ResultadoInativacao> InativarAsync(string cpf, OrcamentoCiclo? orcamento, CancellationToken cancelamento) =>
        _pipelines.ExecutarAsync(
            Dependencias.WAccess,
            async ct =>
            {
                using var atividade = IniciarSpan("waccess.inativar", "UPDATE");
                await using var conexao = new SqlConnection(_stringConexao);
                await conexao.OpenAsync(ct);
                await using var transacao = (SqlTransaction)await conexao.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                await using var comando = Comando(conexao, SqlInativacao, cpf);
                comando.Transaction = transacao;

                ResultadoInativacao resultado;
                await using (var leitor = await comando.ExecuteReaderAsync(ct))
                {
                    await leitor.ReadAsync(ct);
                    resultado = new ResultadoInativacao(leitor.GetInt32(0), leitor.GetInt32(1), leitor.GetInt32(2));
                }

                await transacao.CommitAsync(ct);
                atividade?.SetTag(Instrumentacao.Atributos.LinhasAfetadas, resultado.Inativados);
                atividade?.SetTag(Instrumentacao.Atributos.LinhasMarcadasDownload, resultado.SomenteDownload);
                return resultado;
            },
            orcamento,
            cancelamento);

    private SqlCommand Comando(SqlConnection conexao, string sql, string cpf)
    {
        var comando = conexao.CreateCommand();
#pragma warning disable CA2100 // Texto SQL constante; o CPF é sempre parâmetro (FR-015).
        comando.CommandText = sql;
#pragma warning restore CA2100
        comando.CommandTimeout = _timeoutComando;
        comando.Parameters.Add(new SqlParameter("@Cpf", SqlDbType.VarChar, 50) { Value = cpf });
        return comando;
    }

    private Activity? IniciarSpan(string nome, string operacao)
    {
        var atividade = Instrumentacao.Iniciar(nome, _instancia, ActivityKind.Client);
        atividade?.SetTag("db.system.name", "microsoft.sql_server");
        atividade?.SetTag("db.operation.name", operacao);
        atividade?.SetTag("db.collection.name", "dbo.CHMain");
        return atividade;
    }
}
