namespace InativacaoUsuarioInter.Service.Entrada.Modelos;

/// <summary>Arquivo de entrada já lido e validado (data-model §1).</summary>
public sealed record ArquivoEntrada(
    string Caminho,
    string Nome,
    FormatoArquivo Formato,
    string Hash,
    string HashRaiz,
    string BaseRaiz,
    long TamanhoBytes,
    IReadOnlyList<RegistroEntrada> Registros);
