using InativacaoUsuarioInter.Service.Arquivos;
using InativacaoUsuarioInter.Service.Entrada.Modelos;

namespace InativacaoUsuarioInter.Service.Entrada;

/// <summary>
/// Validação e chave de idempotência dos registros (FR-011, FR-013, data-model §1):
/// CPF normalizado com 11 dígitos; responsável (matrícula e nome) obrigatório e não vazio;
/// a 2ª ocorrência do mesmo CPF no arquivo é <see cref="ValidacaoRegistro.Duplicado"/>.
/// </summary>
internal static class ValidadorRegistro
{
    public static IReadOnlyList<RegistroEntrada> Validar(
        IReadOnlyList<RegistroBruto> registros, Instancia instancia, string hashRaiz)
    {
        ArgumentNullException.ThrowIfNull(registros);
        var vistos = new HashSet<string>(StringComparer.Ordinal);
        var resultado = new List<RegistroEntrada>(registros.Count);

        foreach (var bruto in registros)
        {
            var cpf = NormalizadorCpf.Normalizar(bruto.Cpf);
            var id = bruto.IdResponsavel?.Trim();
            var nome = bruto.NomeResponsavel?.Trim();
            var valido = bruto.EstruturaValida && cpf is not null && !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(nome);

            if (!valido)
            {
                resultado.Add(new RegistroEntrada(
                    bruto.Indice, bruto.TextoBruto, bruto.Cpf, cpf, id, nome,
                    ChaveInvalido(instancia, hashRaiz, bruto.Indice), ValidacaoRegistro.DadoInvalido));
                continue;
            }

            if (!vistos.Add(cpf!))
            {
                resultado.Add(new RegistroEntrada(
                    bruto.Indice, bruto.TextoBruto, bruto.Cpf, cpf, id, nome, null, ValidacaoRegistro.Duplicado));
                continue;
            }

            resultado.Add(new RegistroEntrada(
                bruto.Indice, bruto.TextoBruto, bruto.Cpf, cpf, id, nome,
                Chave(instancia, hashRaiz, cpf!), ValidacaoRegistro.Valido));
        }

        return resultado;
    }

    /// <summary><c>SHA-256(instancia|hashRaiz|cpf)</c>: determinística, sem horário nem GUID (Princípio III).</summary>
    public static string Chave(Instancia instancia, string hashRaiz, string cpf) =>
        HashConteudo.Calcular($"{instancia}|{hashRaiz}|{cpf}");

    /// <summary><c>SHA-256(instancia|hashRaiz|#indice)</c> para registros inválidos.</summary>
    public static string ChaveInvalido(Instancia instancia, string hashRaiz, int indice) =>
        HashConteudo.Calcular(FormattableString.Invariant($"{instancia}|{hashRaiz}|#{indice}"));
}
