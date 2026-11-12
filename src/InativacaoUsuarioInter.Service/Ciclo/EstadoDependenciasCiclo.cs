using System.Collections.Concurrent;
using InativacaoUsuarioInter.Service.Resiliencia;

namespace InativacaoUsuarioInter.Service.Ciclo;

/// <summary>Última falha de uma dependência (exposta em <c>saude.json</c> apenas como categoria).</summary>
public sealed record FalhaDependencia(DateTimeOffset Em, CategoriaErro Categoria);

/// <summary>
/// Estado do ciclo corrente: orçamento de retentativas e dependências marcadas como indisponíveis
/// (curto-circuito por ciclo, research R-06). Reiniciado no início de cada ciclo.
/// </summary>
internal sealed class EstadoDependenciasCiclo
{
    private readonly ConcurrentDictionary<string, FalhaDependencia> _indisponiveis = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FalhaDependencia> _ultimasFalhas = new(StringComparer.Ordinal);

    public OrcamentoCiclo? Orcamento { get; private set; }

    /// <summary>Arquivos em <c>Output</c> desconhecidos do diário, vistos no ciclo (alerta, análise N2).</summary>
    public int ArquivosOutputDesconhecidos { get; set; }

    /// <summary>Data de escrita do arquivo mais antigo ainda em <c>Input</c> no fim do ciclo (null = nenhum).</summary>
    public DateTimeOffset? ArquivoEntradaMaisAntigoDesde { get; set; }

    /// <summary>Pastas com dados pessoais legíveis por grupos amplos (FR-038, alerta PERMISSAO_ABERTA_DADOS_PESSOAIS).</summary>
    public int PastasAbertasDadosPessoais { get; set; }

    public void Iniciar(TimeSpan orcamento)
    {
        Orcamento = new OrcamentoCiclo(orcamento);
        _indisponiveis.Clear();
        ArquivosOutputDesconhecidos = 0;
    }

    public bool Indisponivel(string dependencia) => _indisponiveis.ContainsKey(dependencia);

    public void MarcarIndisponivel(string dependencia, ErroClassificado erro, DateTimeOffset agora)
    {
        var falha = new FalhaDependencia(agora, erro.Categoria);
        _indisponiveis[dependencia] = falha;
        _ultimasFalhas[dependencia] = falha;
    }

    /// <summary>Falhas do ciclo corrente.</summary>
    public IReadOnlyDictionary<string, FalhaDependencia> FalhasDoCiclo => _indisponiveis;

    /// <summary>Última falha registrada, mesmo de ciclos anteriores.</summary>
    public FalhaDependencia? UltimaFalha(string dependencia) =>
        _ultimasFalhas.TryGetValue(dependencia, out var falha) ? falha : null;
}
