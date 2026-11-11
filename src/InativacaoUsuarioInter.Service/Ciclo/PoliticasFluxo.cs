using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Entrada.Modelos;

namespace InativacaoUsuarioInter.Service.Ciclo;

/// <summary>Diferenças de comportamento por instalação, resolvidas por configuração (Princípio I, FR-001).</summary>
public interface IPoliticaFluxo
{
    Instancia Instancia { get; }

    string Raiz { get; }

    /// <summary>Nome da pasta de concluídos: <c>Output</c> (Inter) ou <c>Processados</c> (Condomínio).</summary>
    string NomeConcluidos { get; }

    bool Replica { get; }

    IReadOnlyList<string> PastasObrigatorias { get; }

    string PastaEntrada => Path.Combine(Raiz, "Imput");

    string PastaConcluidos => Path.Combine(Raiz, NomeConcluidos);

    string PastaFalhas => Path.Combine(Raiz, "Falhas");

    string PastaAuditoria => Path.Combine(Raiz, "Auditoria");
}

/// <summary>Instância Inter: raiz <c>FolderPathInter</c>, concluídos em <c>Output</c>, replica ao Condomínio.</summary>
internal sealed class PoliticaFluxoInter(ConfiguracaoServico configuracao) : IPoliticaFluxo
{
    public Instancia Instancia => Instancia.Inter;

    public string Raiz { get; } = configuracao.RaizInter
        ?? throw new InvalidOperationException("SharePointFolder:FolderPathInter ausente.");

    public string NomeConcluidos => "Output";

    public bool Replica => true;

    public IReadOnlyList<string> PastasObrigatorias { get; } = ["Imput", "Output", "Auditoria", "Falhas"];
}

/// <summary>Instância Condomínio: raiz <c>FolderPathCondominio</c>, concluídos em <c>Processados</c>, sem replicação (FR-036).</summary>
internal sealed class PoliticaFluxoCondominio(ConfiguracaoServico configuracao) : IPoliticaFluxo
{
    public Instancia Instancia => Instancia.Condominio;

    public string Raiz { get; } = configuracao.RaizCondominio;

    public string NomeConcluidos => "Processados";

    public bool Replica => false;

    public IReadOnlyList<string> PastasObrigatorias { get; } = ["Imput", "Processados", "Auditoria", "Falhas"];
}
