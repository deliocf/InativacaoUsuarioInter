using System.Diagnostics;
using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Service.Telemetria;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace InativacaoUsuarioInter.Service.Saude;

/// <summary>
/// Readiness ativa das dependências a cada ciclo (FR-040, constituição "Health"): sonda o WAccess com
/// <c>SELECT 1</c> e (Inter) o destino da replicação, pelos mesmos pipelines (timeout, retry, circuito),
/// mesmo quando não há arquivos a processar. Dependências já marcadas indisponíveis no ciclo não são sondadas
/// de novo. A pasta local já é exercitada a cada ciclo pela garantia da estrutura de diretórios.
/// </summary>
internal sealed partial class SondaDependencias(
    ConfiguracaoServico configuracao,
    IPoliticaFluxo politica,
    ProvedorPipelines pipelines,
    EstadoDependenciasCiclo dependencias,
    TimeProvider relogio,
    ILogger<SondaDependencias> logger)
{
    private readonly string _stringConexao = new SqlConnectionStringBuilder(configuracao.ConexaoWAccess)
    {
        ConnectTimeout = configuracao.Resiliencia.WAccess.TimeoutConexaoSegundos,
    }.ConnectionString;

    public async Task SondarAsync(CancellationToken cancelamento)
    {
        if (!dependencias.Indisponivel(Dependencias.WAccess))
        {
            await SondarAsync(Dependencias.WAccess, SondarWAccessAsync, cancelamento);
        }

        if (politica.Replica && !dependencias.Indisponivel(Dependencias.Replicacao))
        {
            await SondarAsync(
                Dependencias.Replicacao,
                _ =>
                {
                    Directory.CreateDirectory(Path.Combine(configuracao.RaizCondominio, "Imput"));
                    return ValueTask.CompletedTask;
                },
                cancelamento);
        }
    }

    private async Task SondarAsync(string dependencia, Func<CancellationToken, ValueTask> sonda, CancellationToken cancelamento)
    {
        using var atividade = Instrumentacao.Iniciar("saude.sonda", configuracao.Instancia.ToString(), ActivityKind.Client);
        atividade?.SetTag(Instrumentacao.Atributos.Dependencia, dependencia);
        try
        {
            await pipelines.ExecutarAsync(dependencia, sonda, dependencias.Orcamento, cancelamento);
            atividade?.SetTag(Instrumentacao.Atributos.Resultado, "sucesso");
        }
        catch (Exception excecao) when (ClassificadorErros.Capturavel(excecao, cancelamento))
        {
            var erro = pipelines.Classificar(dependencia, excecao);
            dependencias.MarcarIndisponivel(dependencia, erro, relogio.GetUtcNow());
            atividade?.SetTag(Instrumentacao.Atributos.Resultado, "transitoria");
            LogIndisponivel(logger, dependencia, erro.Categoria);
        }
    }

    private async ValueTask SondarWAccessAsync(CancellationToken cancelamento)
    {
        await using var conexao = new SqlConnection(_stringConexao);
        await conexao.OpenAsync(cancelamento);
        await using var comando = conexao.CreateCommand();
        comando.CommandText = "SELECT 1;";
        comando.CommandTimeout = configuracao.Resiliencia.WAccess.TimeoutComandoSegundos;
        await comando.ExecuteScalarAsync(cancelamento);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sonda de readiness: dependência {Dependencia} indisponível ({Categoria}).")]
    private static partial void LogIndisponivel(ILogger logger, string dependencia, CategoriaErro categoria);
}
