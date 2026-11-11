using InativacaoUsuarioInter.Service.Arquivos;
using InativacaoUsuarioInter.Service.Inativacao;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace InativacaoUsuarioInter.Service.Resiliencia;

/// <summary>
/// Ponto único de classificação de exceções (Princípio II). Nenhuma exceção fica sem classificação:
/// as desconhecidas são tratadas como transitórias, para que o trabalho nunca seja descartado.
/// </summary>
internal static class ClassificadorErros
{
    public static ErroClassificado Classificar(Exception excecao)
    {
        ArgumentNullException.ThrowIfNull(excecao);

        return excecao switch
        {
            TimeoutRejectedException or TimeoutException => new(ClasseErro.Transitoria, CategoriaErro.Timeout),
            BrokenCircuitException => new(ClasseErro.Transitoria, CategoriaErro.Conexao),
            OperationCanceledException => new(ClasseErro.Transitoria, CategoriaErro.Timeout),
            _ => ClassificadorErrosSql.Classificar(excecao)
                 ?? ClassificadorErrosIO.Classificar(excecao)
                 ?? ClassificarInterna(excecao),
        };
    }

    private static ErroClassificado ClassificarInterna(Exception excecao) =>
        excecao.InnerException is { } interna
            ? Classificar(interna)
            : new(ClasseErro.Transitoria, CategoriaErro.Desconhecida);

    /// <summary>
    /// Indica se a exceção pode ser capturada pelo fluxo de negócio. Interrupções simuladas
    /// (testes de crash) e o cancelamento do host sempre se propagam.
    /// </summary>
    public static bool Capturavel(Exception excecao, CancellationToken cancelamento) =>
        excecao is not InterrupcaoSimuladaException
        && !(excecao is OperationCanceledException && cancelamento.IsCancellationRequested);
}

/// <summary>
/// Interrupção abrupta usada pelos testes de crash em processo (research R-17).
/// Nunca é capturada nem retentada pelo serviço.
/// </summary>
public sealed class InterrupcaoSimuladaException : Exception
{
    public InterrupcaoSimuladaException()
    {
    }

    public InterrupcaoSimuladaException(string message)
        : base(message)
    {
    }

    public InterrupcaoSimuladaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
