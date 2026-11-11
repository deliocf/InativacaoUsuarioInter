namespace InativacaoUsuarioInter.Service.Entrada.Modelos;

/// <summary>
/// Registro lido de um arquivo de entrada (data-model §1).
/// <see cref="TextoBruto"/> preserva a linha TXT ou o elemento JSON original, usado na parte de falhas.
/// </summary>
public sealed record RegistroEntrada(
    int Indice,
    string TextoBruto,
    string? CpfOriginal,
    string? Cpf,
    string? IdResponsavel,
    string? NomeResponsavel,
    string? ChaveIdempotencia,
    ValidacaoRegistro Validacao);
