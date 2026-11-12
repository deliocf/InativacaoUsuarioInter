using InativacaoUsuarioInter.Service.Arquivos;
using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Entrada.Modelos;

namespace InativacaoUsuarioInter.Service.Ciclo;

/// <summary>Diferenças de comportamento por instalação, resolvidas por configuração (Princípio I, FR-001).</summary>
public interface IPoliticaFluxo
{
    Instancia Instancia { get; }

    string Raiz { get; }

    /// <summary>Nome da pasta de concluídos (histórico): <c>Processed</c> nas duas instâncias (feature 002).</summary>
    string NomeConcluidos { get; }

    bool Replica { get; }

    /// <summary>Pastas da própria raiz criadas a cada ciclo (FR-002); falha ao criá-las interrompe o ciclo.</summary>
    IReadOnlyList<string> PastasObrigatorias { get; }

    string PastaEntrada => Path.Combine(Raiz, NomesPastas.Input);

    string PastaConcluidos => Path.Combine(Raiz, NomeConcluidos);

    /// <summary>Fila de saída <c>Output</c> (somente Inter, research R-03); <c>null</c> no Condomínio.</summary>
    string? PastaSaida { get; }

    string PastaFail => Path.Combine(Raiz, NomesPastas.Fail);

    string PastaLogs => Path.Combine(Raiz, NomesPastas.Logs);
}

/// <summary>
/// Instância Inter: raiz <c>FolderPathInter</c>, histórico em <c>Processed</c>, fila de saída em <c>Output</c>
/// e entrega ao Condomínio (feature 002, FR-006–FR-011).
/// </summary>
internal sealed class PoliticaFluxoInter(ConfiguracaoServico configuracao) : IPoliticaFluxo
{
    public Instancia Instancia => Instancia.Inter;

    public string Raiz { get; } = configuracao.RaizInter
        ?? throw new InvalidOperationException("SharePointFolder:FolderPathInter ausente.");

    public string NomeConcluidos => NomesPastas.Processed;

    public bool Replica => true;

    public string? PastaSaida => Path.Combine(Raiz, NomesPastas.Output);

    public IReadOnlyList<string> PastasObrigatorias { get; } =
        [NomesPastas.Input, NomesPastas.Output, NomesPastas.Processed, NomesPastas.Logs, NomesPastas.Fail];
}

/// <summary>
/// Instância Condomínio: raiz <c>FolderPathCondominio</c>, concluídos em <c>Processed</c>, falhas em
/// <c>{FolderPathCondominio}/Fail</c>; sem fila de saída nem replicação (FR-012, FR-013).
/// </summary>
internal sealed class PoliticaFluxoCondominio(ConfiguracaoServico configuracao) : IPoliticaFluxo
{
    public Instancia Instancia => Instancia.Condominio;

    public string Raiz { get; } = configuracao.RaizCondominio;

    public string NomeConcluidos => NomesPastas.Processed;

    public bool Replica => false;

    public string? PastaSaida => null;

    public IReadOnlyList<string> PastasObrigatorias { get; } =
        [NomesPastas.Input, NomesPastas.Processed, NomesPastas.Logs, NomesPastas.Fail];
}
