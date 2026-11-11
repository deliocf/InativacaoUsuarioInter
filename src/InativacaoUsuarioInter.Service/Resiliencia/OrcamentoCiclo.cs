using System.Diagnostics;
using Polly;

namespace InativacaoUsuarioInter.Service.Resiliencia;

/// <summary>
/// Orçamento de retentativas de um ciclo (Princípio II, research R-06): a duração das tentativas extras
/// e as esperas entre elas, somadas, nunca passam de <c>IntervalSeconds</c>. A 1ª tentativa de cada operação
/// não consome orçamento. Medido em tempo real (as esperas do Polly também usam o relógio do sistema).
/// </summary>
public sealed class OrcamentoCiclo
{
    /// <summary>Chave para transportar o orçamento no <see cref="ResilienceContext"/>.</summary>
    public static readonly ResiliencePropertyKey<OrcamentoCiclo> Chave = new("inativacao.orcamento-ciclo");

    private static readonly TimeSpan TimeoutMinimo = TimeSpan.FromMilliseconds(100);

    private readonly TimeSpan _total;
    private readonly Lock _trava = new();
    private TimeSpan _consumido;
    private long? _inicioTentativaExtra;

    public OrcamentoCiclo(TimeSpan total)
    {
        _total = total;
    }

    public TimeSpan Total => _total;

    public TimeSpan Consumido
    {
        get
        {
            lock (_trava)
            {
                return _consumido;
            }
        }
    }

    public TimeSpan Restante
    {
        get
        {
            lock (_trava)
            {
                var restante = _total - _consumido;
                return restante > TimeSpan.Zero ? restante : TimeSpan.Zero;
            }
        }
    }

    public bool TentativaExtraEmCurso
    {
        get
        {
            lock (_trava)
            {
                return _inicioTentativaExtra.HasValue;
            }
        }
    }

    public void Consumir(TimeSpan duracao)
    {
        if (duracao <= TimeSpan.Zero)
        {
            return;
        }

        lock (_trava)
        {
            _consumido += duracao;
        }
    }

    /// <summary>Há orçamento para esperar <paramref name="espera"/> e ainda executar uma tentativa mínima.</summary>
    public bool PodeTentar(TimeSpan espera) => Restante > espera + TimeoutMinimo;

    /// <summary>Chamado no <c>OnRetry</c>: desconta a espera e marca o início da tentativa extra.</summary>
    public void IniciarTentativaExtra(TimeSpan espera)
    {
        lock (_trava)
        {
            _consumido += espera;
            _inicioTentativaExtra = Stopwatch.GetTimestamp() + (long)(espera.TotalSeconds * Stopwatch.Frequency);
        }
    }

    /// <summary>Chamado ao fim de uma tentativa extra: desconta sua duração real.</summary>
    public void EncerrarTentativaExtra()
    {
        lock (_trava)
        {
            if (_inicioTentativaExtra is { } inicio)
            {
                var decorrido = Stopwatch.GetElapsedTime(inicio);
                if (decorrido > TimeSpan.Zero)
                {
                    _consumido += decorrido;
                }

                _inicioTentativaExtra = null;
            }
        }
    }

    /// <summary>Timeout efetivo de uma tentativa: numa tentativa extra, nunca maior que o orçamento restante.</summary>
    public TimeSpan LimitarTimeout(TimeSpan configurado)
    {
        if (!TentativaExtraEmCurso)
        {
            return configurado;
        }

        var restante = Restante;
        if (restante >= configurado)
        {
            return configurado;
        }

        return restante > TimeoutMinimo ? restante : TimeoutMinimo;
    }
}
