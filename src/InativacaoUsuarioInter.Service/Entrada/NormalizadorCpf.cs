using System.Text;

namespace InativacaoUsuarioInter.Service.Entrada;

/// <summary>
/// Normalização do CPF (FR-011, Clarification 2026-11-11): remove espaços nas pontas e somente
/// <c>.</c>, <c>-</c> e espaços internos; o resultado precisa ter exatamente 11 dígitos ASCII.
/// Dígitos verificadores não são validados.
/// </summary>
internal static class NormalizadorCpf
{
    public const int Tamanho = 11;

    /// <summary>Devolve o CPF normalizado ou <c>null</c> se inválido (<c>DADO_INVALIDO</c>).</summary>
    public static string? Normalizar(string? valor)
    {
        if (valor is null)
        {
            return null;
        }

        var resultado = new StringBuilder(Tamanho);
        foreach (var caractere in valor.Trim())
        {
            if (caractere is '.' or '-' or ' ')
            {
                continue;
            }

            if (!char.IsAsciiDigit(caractere) || resultado.Length == Tamanho)
            {
                return null;
            }

            resultado.Append(caractere);
        }

        return resultado.Length == Tamanho ? resultado.ToString() : null;
    }
}
