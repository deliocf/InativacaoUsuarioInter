using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using InativacaoUsuarioInter.Service.Entrada;
using InativacaoUsuarioInter.Service.Entrada.Modelos;

namespace InativacaoUsuarioInter.Service.Arquivos;

/// <summary>Resultado de uma gravação no histórico (<c>Processed</c>/<c>Fail</c>).</summary>
public enum ResultadoHistorico
{
    /// <summary>O arquivo não existia e foi criado.</summary>
    Gravado,

    /// <summary>O arquivo já continha todo o conteúdo novo (no-op, Princípio III).</summary>
    JaExistia,

    /// <summary>O conteúdo novo foi acrescentado ao arquivo existente.</summary>
    Mesclado,
}

/// <summary>
/// Gravação em <c>Processed</c> e <c>Fail</c> sempre com o nome original (bug nome-original-sem-trace): em colisão com
/// conteúdo diferente, os registros novos são <b>acrescentados</b> ao arquivo existente, nunca renomeados nem
/// sobrescritos com perda. A mescla é uma união ordenada (registros existentes, depois os novos ainda ausentes), então
/// repeti-la não muda nada (Princípio III); a troca do arquivo é atômica (temporário + rename).
/// <list type="bullet">
/// <item>TXT: um registro por linha; resultado com <c>\r\n</c>.</item>
/// <item>JSON: elementos dos dois arrays, no formato das partes (<see cref="SerializadorPartes"/>).</item>
/// <item>Conteúdo que não se encaixa (ex.: arquivo rejeitado inválido): acrescentado como bloco, se ainda não estiver presente.</item>
/// </list>
/// </summary>
internal static class MescladorHistorico
{
    private static readonly UTF8Encoding Utf8Estrito = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] QuebraLinha = "\r\n"u8.ToArray();

    private static readonly JsonWriterOptions OpcoesJson = new()
    {
        Indented = true,
        IndentSize = 2,
        NewLine = "\r\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Grava <paramref name="conteudo"/> em <paramref name="destino"/>, mesclando se já existir com outro conteúdo.</summary>
    public static async Task<ResultadoHistorico> GravarAsync(string destino, ReadOnlyMemory<byte> conteudo, CancellationToken cancelamento)
    {
        if (!File.Exists(destino))
        {
            return await EscritorAtomico.GravarAsync(destino, conteudo, cancelamento) == ResultadoEscrita.Gravado
                ? ResultadoHistorico.Gravado
                : ResultadoHistorico.JaExistia;
        }

        var existente = await File.ReadAllBytesAsync(destino, cancelamento);
        if (existente.AsSpan().SequenceEqual(conteudo.Span))
        {
            return ResultadoHistorico.JaExistia;
        }

        var mesclado = Mesclar(existente, conteudo.Span, LeitorArquivo.FormatoPorExtensao(destino));
        if (mesclado.AsSpan().SequenceEqual(existente))
        {
            return ResultadoHistorico.JaExistia;
        }

        await EscritorAtomico.SubstituirAsync(destino, mesclado, cancelamento);
        return ResultadoHistorico.Mesclado;
    }

    /// <summary>
    /// Move <paramref name="origem"/> para <paramref name="destino"/>; se o destino já existir com outro conteúdo, mescla
    /// e remove a origem. Retomável: com a origem já removida, não faz nada.
    /// </summary>
    public static async Task<ResultadoHistorico> MoverAsync(string origem, string destino, CancellationToken cancelamento)
    {
        if (!File.Exists(origem))
        {
            return ResultadoHistorico.JaExistia;
        }

        if (!File.Exists(destino))
        {
            await EscritorAtomico.MoverAsync(origem, destino, cancelamento);
            return ResultadoHistorico.Gravado;
        }

        var resultado = await GravarAsync(destino, await File.ReadAllBytesAsync(origem, cancelamento), cancelamento);
        File.Delete(origem);
        return resultado;
    }

    /// <summary>União ordenada de <paramref name="existente"/> com <paramref name="novo"/> (determinística e idempotente).</summary>
    public static byte[] Mesclar(ReadOnlySpan<byte> existente, ReadOnlySpan<byte> novo, FormatoArquivo? formato)
    {
        var a = SemBom(existente);
        var b = SemBom(novo);
        return formato switch
        {
            FormatoArquivo.Json when MesclarJson(a, b) is { } json => json,
            FormatoArquivo.Txt when MesclarTxt(a, b) is { } txt => txt,
            _ => MesclarBloco(a, b),
        };
    }

    private static byte[]? MesclarTxt(ReadOnlySpan<byte> existente, ReadOnlySpan<byte> novo)
    {
        if (Texto(existente) is not { } textoExistente || Texto(novo) is not { } textoNovo)
        {
            return null;
        }

        var linhas = Linhas(textoExistente).ToList();
        var vistas = new HashSet<string>(linhas, StringComparer.Ordinal);
        linhas.AddRange(Linhas(textoNovo).Where(vistas.Add));

        var resultado = new StringBuilder();
        foreach (var linha in linhas)
        {
            resultado.Append(linha).Append("\r\n");
        }

        return Encoding.UTF8.GetBytes(resultado.ToString());
    }

    private static byte[]? MesclarJson(ReadOnlySpan<byte> existente, ReadOnlySpan<byte> novo)
    {
        if (Elementos(existente) is not { } elementos || Elementos(novo) is not { } novos)
        {
            return null;
        }

        // Elementos comparados na forma compacta (indiferente à indentação de cada arquivo) e reescritos no formato das
        // partes, para que a mescla de duas partes geradas pelo serviço seja idêntica a gerar as duas juntas.
        var vistos = new HashSet<string>(elementos.Select(e => e.GetRawText()).Select(Compactar), StringComparer.Ordinal);
        elementos.AddRange(novos.Where(e => vistos.Add(Compactar(e.GetRawText()))));

        using var memoria = new MemoryStream();
        using (var escritor = new Utf8JsonWriter(memoria, OpcoesJson))
        {
            escritor.WriteStartArray();
            foreach (var elemento in elementos)
            {
                elemento.WriteTo(escritor);
            }

            escritor.WriteEndArray();
        }

        return memoria.ToArray();
    }

    private static string Compactar(string json)
    {
        using var documento = JsonDocument.Parse(json);
        using var memoria = new MemoryStream();
        using (var escritor = new Utf8JsonWriter(memoria))
        {
            documento.RootElement.WriteTo(escritor);
        }

        return Encoding.UTF8.GetString(memoria.ToArray());
    }

    /// <summary>Conteúdo sem formato reconhecível: acrescenta o bloco inteiro, se ainda não estiver no arquivo.</summary>
    private static byte[] MesclarBloco(ReadOnlySpan<byte> existente, ReadOnlySpan<byte> novo)
    {
        if (novo.IsEmpty || existente.IndexOf(novo) >= 0)
        {
            return existente.ToArray();
        }

        var separar = !existente.IsEmpty && existente[^1] != (byte)'\n';
        var resultado = new byte[existente.Length + (separar ? QuebraLinha.Length : 0) + novo.Length];
        existente.CopyTo(resultado);
        var posicao = existente.Length;
        if (separar)
        {
            QuebraLinha.CopyTo(resultado, posicao);
            posicao += QuebraLinha.Length;
        }

        novo.CopyTo(resultado.AsSpan(posicao));
        return resultado;
    }

    private static List<JsonElement>? Elementos(ReadOnlySpan<byte> conteudo)
    {
        try
        {
            using var documento = JsonDocument.Parse(conteudo.ToArray());
            return documento.RootElement.ValueKind == JsonValueKind.Array
                ? documento.RootElement.EnumerateArray().Select(e => e.Clone()).ToList()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Texto(ReadOnlySpan<byte> conteudo)
    {
        try
        {
            return Utf8Estrito.GetString(conteudo);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Linhas(string texto) =>
        texto.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !string.IsNullOrWhiteSpace(l));

    private static ReadOnlySpan<byte> SemBom(ReadOnlySpan<byte> conteudo) =>
        conteudo.StartsWith(Bom) ? conteudo[Bom.Length..] : conteudo;
}
