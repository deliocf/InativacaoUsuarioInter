using InativacaoUsuarioInter.Service.Ciclo;

namespace InativacaoUsuarioInter.Tests.Comum;

/// <summary>Executa uma ação (ex.: cortar o proxy do banco) na N-ésima vez que um ponto do fluxo é atingido.</summary>
public sealed class GatilhoPonto(PontoFalha alvo, int ocorrencia, Action acao) : IPontoFalha
{
    private int _contagem;

    public void Atingido(PontoFalha ponto)
    {
        if (ponto == alvo && Interlocked.Increment(ref _contagem) == ocorrencia)
        {
            acao();
        }
    }
}
