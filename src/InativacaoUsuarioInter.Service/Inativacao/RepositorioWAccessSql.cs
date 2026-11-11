using System.Data;
using System.Diagnostics;
using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Service.Telemetria;
using Microsoft.Data.SqlClient;

namespace InativacaoUsuarioInter.Service.Inativacao;

/// <summary>Resultado do passo B (alteração): linhas inativadas por este comando e cadastros existentes.</summary>
public readonly record struct ResultadoInativacao(int Inativados, int Existentes);

/// <summary>Acesso ao WAccess da instância corrente (contracts/waccess-inativacao.md).</summary>
public interface IRepositorioWAccess
{
    /// <summary>Passo A — pré-leitura do estado anterior.</summary>
    Task<EstadoAnterior> LerEstadoAsync(string cpf, OrcamentoCiclo? orcamento, CancellationToken cancelamento);

    /// <summary>Passo B — <c>UPDATE</c> condicional, só quando o estado anterior é <c>Ativo</c>.</summary>
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

    public const string SqlInativacao = """
        DECLARE @afetados TABLE (CHID int);
        UPDATE dbo.CHMain SET CHState = 1
          OUTPUT inserted.CHID INTO @afetados
          WHERE IdNumber = @Cpf AND CHState <> 1;
        SELECT (SELECT COUNT(*) FROM @afetados)                      AS Inativados,
               (SELECT COUNT(*) FROM dbo.CHMain WHERE IdNumber = @Cpf) AS Existentes;
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
                    resultado = new ResultadoInativacao(leitor.GetInt32(0), leitor.GetInt32(1));
                }

                await transacao.CommitAsync(ct);
                atividade?.SetTag(Instrumentacao.Atributos.LinhasAfetadas, resultado.Inativados);
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
