using InativacaoUsuarioInter.Service.Resiliencia;

namespace InativacaoUsuarioInter.Service.Ciclo;

/// <summary>Pontos onde os testes de crash interrompem o processamento (T061, research R-17).</summary>
public enum PontoFalha
{
    AposIntencao,
    AposComandoWAccess,
    AposRegistrarResultado,
    AposAppendLog,
    AposGravarPartes,
    AntesRemoverOriginal,
    AposCopiaTemporaria,

    /// <summary>Feature 002: após mover um arquivo de uma pasta antiga (migração, FR-004).</summary>
    AposMoverLegado,

    /// <summary>Feature 002: após substituir o log de destino pela mescla, antes de remover o log antigo.</summary>
    AposSubstituirLogLegado,

    /// <summary>Feature 002: após registrar <c>Entregue</c> no diário, antes de limpar <c>Output</c> (FR-008).</summary>
    AposRegistrarEntregue,
}

/// <summary>Ponto de injeção de falhas. Em produção é no-op.</summary>
public interface IPontoFalha
{
    void Atingido(PontoFalha ponto);
}

/// <summary>Implementação de produção: não faz nada.</summary>
public sealed class PontoFalhaNulo : IPontoFalha
{
    public void Atingido(PontoFalha ponto)
    {
    }
}

/// <summary>Implementação em processo para testes: lança <see cref="InterrupcaoSimuladaException"/> no ponto escolhido.</summary>
public sealed class PontoFalhaEmProcesso(PontoFalha alvo, int ocorrencia = 1) : IPontoFalha
{
    private int _contagem;

    public bool Disparado { get; private set; }

    public void Atingido(PontoFalha ponto)
    {
        if (Disparado || ponto != alvo || Interlocked.Increment(ref _contagem) != ocorrencia)
        {
            return;
        }

        Disparado = true;
        throw new InterrupcaoSimuladaException($"Interrupção simulada em {ponto}.");
    }
}

/// <summary>
/// Crash real do processo para o teste com processo filho (análise C2): lê <c>INATIVACAO_PONTO_FALHA</c>
/// e chama <see cref="Environment.FailFast(string)"/>. Registrada somente com <c>DOTNET_ENVIRONMENT=Testing</c>.
/// </summary>
public sealed class PontoFalhaProcesso : IPontoFalha
{
    public const string Variavel = "INATIVACAO_PONTO_FALHA";

    private readonly PontoFalha? _alvo =
        Enum.TryParse<PontoFalha>(Environment.GetEnvironmentVariable(Variavel), out var ponto) ? ponto : null;

    public void Atingido(PontoFalha ponto)
    {
        if (_alvo == ponto)
        {
            Environment.FailFast($"Crash simulado em {ponto}.");
        }
    }
}
