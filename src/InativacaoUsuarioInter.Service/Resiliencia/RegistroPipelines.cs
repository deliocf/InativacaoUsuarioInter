using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Telemetria;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace InativacaoUsuarioInter.Service.Resiliencia;

/// <summary>Nomes das dependências externas (um pipeline e um circuito por dependência).</summary>
internal static class Dependencias
{
    public const string WAccess = "waccess";
    public const string PastaLocal = "pasta-local";
    public const string Replicacao = "pasta-replicacao";

    public static readonly IReadOnlyList<string> Todas = [WAccess, PastaLocal, Replicacao];
}

/// <summary>
/// Pipelines de resiliência centralizados (Princípio II, research R-06): Retry (exponencial + jitter,
/// apenas erros transitórios e limitado ao <see cref="OrcamentoCiclo"/>) → Circuit breaker → Timeout por tentativa.
/// Construídos com Polly.Core e o relógio do sistema.
/// </summary>
internal sealed class ProvedorPipelines
{
    private readonly Dictionary<string, ResiliencePipeline> _pipelines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CircuitBreakerStateProvider> _circuitos = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ErroClassificado> _causaAbertura = new(StringComparer.Ordinal);

    public ProvedorPipelines(ConfiguracaoServico configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);
        var r = configuracao.Resiliencia;
        Registrar(
            Dependencias.WAccess,
            r.WAccess,
            TimeSpan.FromSeconds(r.WAccess.TimeoutConexaoSegundos + r.WAccess.TimeoutComandoSegundos));
        Registrar(Dependencias.PastaLocal, r.Arquivos, TimeSpan.FromSeconds(r.Arquivos.TimeoutOperacaoSegundos));
        Registrar(Dependencias.Replicacao, r.Replicacao, TimeSpan.FromSeconds(r.Replicacao.TimeoutOperacaoSegundos));
    }

    public ResiliencePipeline Obter(string dependencia) => _pipelines[dependencia];

    /// <summary>Estado do circuito: 0=Fechado, 1=Aberto, 2=MeioAberto (3=Isolado é tratado como Aberto).</summary>
    public CircuitState EstadoCircuito(string dependencia) => _circuitos[dependencia].CircuitState;

    /// <summary>
    /// Classifica uma falha da dependência. Com o circuito aberto, devolve a categoria da falha que o abriu
    /// (ex.: Permissao, Configuracao), em vez de "circuito aberto", para que os alertas apontem a causa real.
    /// </summary>
    public ErroClassificado Classificar(string dependencia, Exception excecao) =>
        excecao is BrokenCircuitException && _causaAbertura.TryGetValue(dependencia, out var causa)
            ? causa
            : ClassificadorErros.Classificar(excecao);

    /// <summary>
    /// Executa <paramref name="operacao"/> no pipeline da dependência, propagando o orçamento do ciclo
    /// (quando houver) pelo <see cref="ResilienceContext"/>.
    /// </summary>
    public async Task<T> ExecutarAsync<T>(
        string dependencia,
        Func<CancellationToken, ValueTask<T>> operacao,
        OrcamentoCiclo? orcamento,
        CancellationToken cancelamento)
    {
        ArgumentNullException.ThrowIfNull(operacao);
        var contexto = ResilienceContextPool.Shared.Get(dependencia, cancelamento);
        if (orcamento is not null)
        {
            contexto.Properties.Set(OrcamentoCiclo.Chave, orcamento);
        }

        try
        {
            return await _pipelines[dependencia].ExecuteAsync(
                static (ctx, estado) => estado(ctx.CancellationToken), contexto, operacao);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(contexto);
        }
    }

    public async Task ExecutarAsync(
        string dependencia,
        Func<CancellationToken, ValueTask> operacao,
        OrcamentoCiclo? orcamento,
        CancellationToken cancelamento)
    {
        ArgumentNullException.ThrowIfNull(operacao);
        await ExecutarAsync<bool>(
            dependencia,
            async ct =>
            {
                await operacao(ct);
                return true;
            },
            orcamento,
            cancelamento);
    }

    private void Registrar(string nome, PoliticaDependenciaOptions politica, TimeSpan timeout)
    {
        var estadoCircuito = new CircuitBreakerStateProvider();
        var atrasoMaximo = TimeSpan.FromMilliseconds(politica.AtrasoMaximoMs);
        var construtor = new ResiliencePipelineBuilder { Name = nome };

        if (politica.MaxTentativas > 0)
        {
            construtor.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = politica.MaxTentativas,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(politica.AtrasoBaseMs),
                MaxDelay = atrasoMaximo,
                ShouldHandle = args => ValueTask.FromResult(DeveRetentar(args, atrasoMaximo)),
                OnRetry = args =>
                {
                    Instrumentacao.Retentativas.Add(1, new KeyValuePair<string, object?>(Instrumentacao.Atributos.Dependencia, nome));
                    if (args.Context.Properties.TryGetValue(OrcamentoCiclo.Chave, out var orcamento))
                    {
                        orcamento.IniciarTentativaExtra(args.RetryDelay);
                    }

                    return default;
                },
            });
        }

        construtor.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = politica.Circuito.TaxaFalha,
            SamplingDuration = TimeSpan.FromSeconds(Math.Max(1, politica.Circuito.JanelaSegundos)),
            MinimumThroughput = Math.Max(2, politica.Circuito.MinimoChamadas),
            BreakDuration = TimeSpan.FromSeconds(Math.Max(1, politica.Circuito.PausaSegundos)),
            StateProvider = estadoCircuito,
            ShouldHandle = args => ValueTask.FromResult(
                args.Outcome.Exception is { } ex && EhTransitoria(ex, args.Context.CancellationToken)),
            OnOpened = args =>
            {
                EstadoMetricas.Circuitos[nome] = 1;
                if (args.Outcome.Exception is { } causa)
                {
                    _causaAbertura[nome] = ClassificadorErros.Classificar(causa);
                }

                return default;
            },
            OnHalfOpened = _ =>
            {
                EstadoMetricas.Circuitos[nome] = 2;
                return default;
            },
            OnClosed = _ =>
            {
                EstadoMetricas.Circuitos[nome] = 0;
                return default;
            },
        });

        construtor.AddTimeout(new TimeoutStrategyOptions
        {
            TimeoutGenerator = args => ValueTask.FromResult(
                args.Context.Properties.TryGetValue(OrcamentoCiclo.Chave, out var orcamento)
                    ? orcamento.LimitarTimeout(timeout)
                    : timeout),
        });

        EstadoMetricas.Circuitos[nome] = 0;
        _circuitos[nome] = estadoCircuito;
        _pipelines[nome] = construtor.Build();
    }

    private static bool DeveRetentar(RetryPredicateArguments<object> args, TimeSpan atrasoMaximo)
    {
        if (args.Context.Properties.TryGetValue(OrcamentoCiclo.Chave, out var orcamento) && args.AttemptNumber > 0)
        {
            orcamento.EncerrarTentativaExtra();
        }

        if (args.Outcome.Exception is not { } excecao || !EhTransitoria(excecao, args.Context.CancellationToken))
        {
            return false;
        }

        // Circuito aberto: não adianta insistir neste ciclo.
        if (excecao is BrokenCircuitException)
        {
            return false;
        }

        return orcamento is null || orcamento.PodeTentar(atrasoMaximo);
    }

    private static bool EhTransitoria(Exception excecao, CancellationToken cancelamento) =>
        ClassificadorErros.Capturavel(excecao, cancelamento)
        && ClassificadorErros.Classificar(excecao).Classe == ClasseErro.Transitoria;
}
