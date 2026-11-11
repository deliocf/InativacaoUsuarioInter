using System.Text.Json;

namespace InativacaoUsuarioInter.Service.Entrada;

/// <summary>Registro como lido do arquivo, antes da normalização e validação.</summary>
internal sealed record RegistroBruto(
    int Indice,
    string TextoBruto,
    string? Cpf,
    string? IdResponsavel,
    string? NomeResponsavel,
    bool EstruturaValida);

/// <summary>Resultado da interpretação de um arquivo inteiro.</summary>
internal sealed record ResultadoParse(bool Valido, IReadOnlyList<RegistroBruto> Registros)
{
    public static readonly ResultadoParse Invalido = new(false, []);
}

/// <summary>
/// JSON (FR-009, contracts/arquivos-entrada.md §3): array na raiz; <c>cpf</c>, <c>id_responsavel</c> e
/// <c>nome_responsavel</c> precisam ser strings. Propriedades extras são ignoradas.
/// </summary>
internal static class ParserJson
{
    public static ResultadoParse Interpretar(string texto)
    {
        try
        {
            using var documento = JsonDocument.Parse(texto);
            if (documento.RootElement.ValueKind != JsonValueKind.Array)
            {
                return ResultadoParse.Invalido;
            }

            var registros = new List<RegistroBruto>();
            var indice = 0;
            foreach (var elemento in documento.RootElement.EnumerateArray())
            {
                var bruto = elemento.GetRawText();
                if (elemento.ValueKind != JsonValueKind.Object)
                {
                    registros.Add(new RegistroBruto(indice++, bruto, null, null, null, false));
                    continue;
                }

                var cpf = Texto(elemento, "cpf");
                var id = Texto(elemento, "id_responsavel");
                var nome = Texto(elemento, "nome_responsavel");
                registros.Add(new RegistroBruto(indice++, bruto, cpf, id, nome, cpf is not null && id is not null && nome is not null));
            }

            return new ResultadoParse(true, registros);
        }
        catch (JsonException)
        {
            return ResultadoParse.Invalido;
        }
    }

    private static string? Texto(JsonElement objeto, string propriedade) =>
        objeto.TryGetProperty(propriedade, out var valor) && valor.ValueKind == JsonValueKind.String
            ? valor.GetString()
            : null;
}

/// <summary>
/// TXT (FR-010, contracts/arquivos-entrada.md §4): um registro por linha, sem cabeçalho; divide apenas nas
/// duas primeiras vírgulas e o restante da linha é o nome. Linhas vazias são ignoradas.
/// </summary>
internal static class ParserTxt
{
    public static ResultadoParse Interpretar(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);
        var registros = new List<RegistroBruto>();
        var indice = 0;
        foreach (var linhaBruta in texto.Split('\n'))
        {
            var linha = linhaBruta.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(linha))
            {
                continue;
            }

            var primeira = linha.IndexOf(',', StringComparison.Ordinal);
            var segunda = primeira < 0 ? -1 : linha.IndexOf(',', primeira + 1);
            if (segunda < 0)
            {
                registros.Add(new RegistroBruto(indice++, linha, null, null, null, false));
                continue;
            }

            registros.Add(new RegistroBruto(
                indice++,
                linha,
                linha[..primeira],
                linha[(primeira + 1)..segunda],
                linha[(segunda + 1)..],
                true));
        }

        return new ResultadoParse(true, registros);
    }
}
