using InativacaoUsuarioInter.Service.Estado;
using InativacaoUsuarioInter.Service.Registros;
using InativacaoUsuarioInter.Service.Resiliencia;
using Microsoft.Extensions.Logging;

namespace InativacaoUsuarioInter.Service.Ciclo;

/// <summary>
/// Recuperação no início do ciclo (research R-08): linhas de Auditoria/Falhas já registradas no diário,
/// mas ainda não confirmadas no log, são gravadas exatamente uma vez (procura a linha exata antes de anexar).
/// Registros <c>Pendente</c>/<c>EmExecucao</c> são retomados ao reprocessar o arquivo que os contém,
/// com o <c>estado_anterior</c> gravado.
/// </summary>
internal sealed partial class Recuperacao(
    IArmazenamentoEstado estado,
    IRegistroLogs logs,
    ProvedorPipelines pipelines,
    EstadoDependenciasCiclo dependencias,
    TimeProvider relogio,
    ILogger<Recuperacao> logger)
{
    public async Task<int> ExecutarAsync(CancellationToken cancelamento)
    {
        var recuperados = 0;
        foreach (var pendente in await estado.ListarLogsPendentesAsync(cancelamento))
        {
            try
            {
                // Leitura do log no compartilhamento também passa pelo pipeline (Princípio II).
                var presente = await pipelines.ExecutarAsync(
                    Dependencias.PastaLocal,
                    ct => new ValueTask<bool>(logs.ContemLinhaAsync(pendente.Tipo, pendente.Linha, ct)),
                    dependencias.Orcamento,
                    cancelamento);
                if (!presente)
                {
                    await logs.AnexarAsync(pendente.Tipo, pendente.Linha, dependencias.Orcamento, cancelamento);
                }
            }
            catch (Exception excecao) when (ClassificadorErros.Capturavel(excecao, cancelamento))
            {
                var erro = pipelines.Classificar(Dependencias.PastaLocal, excecao);
                dependencias.MarcarIndisponivel(Dependencias.PastaLocal, erro, relogio.GetUtcNow());
                LogFalha(logger, pendente.Tipo, erro.Categoria);
                return recuperados;
            }

            if (pendente.EhArquivo)
            {
                await estado.MarcarLogArquivoGravadoAsync(pendente.Identificador, cancelamento);
            }
            else
            {
                await estado.MarcarLogGravadoAsync(pendente.Identificador, cancelamento);
            }

            recuperados++;
        }

        if (recuperados > 0)
        {
            LogRecuperados(logger, recuperados);
        }

        return recuperados;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recuperação: {Quantidade} linha(s) de log confirmadas a partir do diário.")]
    private static partial void LogRecuperados(ILogger logger, int quantidade);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recuperação adiada: log de {Tipo} indisponível ({Categoria}).")]
    private static partial void LogFalha(ILogger logger, TipoLog tipo, CategoriaErro categoria);
}
