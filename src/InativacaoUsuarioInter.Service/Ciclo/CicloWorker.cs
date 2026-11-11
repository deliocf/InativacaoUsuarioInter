using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Service.Saude;
using Microsoft.Extensions.Logging;

namespace InativacaoUsuarioInter.Service.Ciclo;

/// <summary>
/// Laço do serviço (FR-006, research R-12): o 1º ciclo roda imediatamente e cada ciclo seguinte começa
/// <c>IntervalSeconds</c> após o <b>término</b> do anterior. Nunca há dois ciclos simultâneos; uma falha
/// de ciclo é classificada e registrada sem encerrar o laço.
/// </summary>
internal sealed partial class CicloWorker(
    IOrquestradorCiclo orquestrador,
    IPublicadorSaude saude,
    ConfiguracaoServico configuracao,
    TimeProvider relogio,
    ILogger<CicloWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogIniciado(logger, configuracao.Instancia.ToString(), configuracao.IntervaloSegundos);
        try
        {
            await saude.PartidaAsync(stoppingToken);
        }
        catch (Exception excecao) when (ClassificadorErros.Capturavel(excecao, stoppingToken))
        {
            LogFalhaCiclo(logger, excecao, ClassificadorErros.Classificar(excecao).Categoria);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await orquestrador.ExecutarCicloAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception excecao) when (ClassificadorErros.Capturavel(excecao, stoppingToken))
            {
                LogFalhaCiclo(logger, excecao, ClassificadorErros.Classificar(excecao).Categoria);
            }

            try
            {
                await Task.Delay(configuracao.Intervalo, relogio, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        LogEncerrado(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Serviço iniciado: instância {Instancia}, intervalo de {IntervaloSegundos} s após o fim de cada ciclo.")]
    private static partial void LogIniciado(ILogger logger, string instancia, int intervaloSegundos);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha no ciclo ({Categoria}); o próximo ciclo ocorrerá normalmente.")]
    private static partial void LogFalhaCiclo(ILogger logger, Exception excecao, CategoriaErro categoria);

    [LoggerMessage(Level = LogLevel.Information, Message = "Serviço encerrado.")]
    private static partial void LogEncerrado(ILogger logger);
}
