using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using InativacaoUsuarioInter.Service.Entrada.Modelos;

namespace InativacaoUsuarioInter.Service.Arquivos;

/// <summary>Nomes das partes geradas (research R-10): únicos, rastreáveis ao original e sem acumular sufixos.</summary>
internal static class NomeadorPartes
{
    public const string Sucesso = "sucesso";
    public const string Falhas = "falhas";
    public const string Pendente = "pendente";

    private const int TamanhoHashCurto = 8;

    /// <summary><c>&lt;baseRaiz&gt;__&lt;tipo&gt;-&lt;h8&gt;.&lt;ext&gt;</c>.</summary>
    public static string Parte(string baseRaiz, string tipo, string hash, string extensao) =>
        $"{baseRaiz}__{tipo}-{hash[..TamanhoHashCurto]}{extensao}";

    /// <summary>Nome alternativo em colisão com conteúdo diferente: <c>&lt;base&gt;__&lt;h8&gt;.&lt;ext&gt;</c>.</summary>
    public static string Colisao(string nome, string hash) =>
        $"{Path.GetFileNameWithoutExtension(nome)}__{hash[..TamanhoHashCurto]}{Path.GetExtension(nome)}";
}

/// <summary>
/// Serialização das partes no mesmo formato do original (contracts/arquivos-entrada.md §7):
/// UTF-8 sem BOM; JSON indentado (2 espaços) com <c>cpf</c>, <c>id_responsavel</c>, <c>nome_responsavel</c>;
/// TXT com <c>\r\n</c>. A parte de falhas preserva o texto bruto de cada registro.
/// </summary>
internal static class SerializadorPartes
{
    private static readonly UTF8Encoding Utf8SemBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonWriterOptions OpcoesJson = new()
    {
        Indented = true,
        IndentSize = 2,
        NewLine = "\r\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Registros válidos com CPF normalizado (partes de sucesso e pendentes).</summary>
    public static byte[] SerializarRegistros(FormatoArquivo formato, IEnumerable<RegistroEntrada> registros)
    {
        ArgumentNullException.ThrowIfNull(registros);
        if (formato == FormatoArquivo.Txt)
        {
            var texto = new StringBuilder();
            foreach (var r in registros)
            {
                texto.Append(r.Cpf).Append(',').Append(r.IdResponsavel).Append(',').Append(r.NomeResponsavel).Append("\r\n");
            }

            return Utf8SemBom.GetBytes(texto.ToString());
        }

        using var memoria = new MemoryStream();
        using (var escritor = new Utf8JsonWriter(memoria, OpcoesJson))
        {
            escritor.WriteStartArray();
            foreach (var r in registros)
            {
                escritor.WriteStartObject();
                escritor.WriteString("cpf", r.Cpf);
                escritor.WriteString("id_responsavel", r.IdResponsavel);
                escritor.WriteString("nome_responsavel", r.NomeResponsavel);
                escritor.WriteEndObject();
            }

            escritor.WriteEndArray();
        }

        return memoria.ToArray();
    }

    /// <summary>Texto bruto de cada registro (parte de falhas), para correção e reenvio.</summary>
    public static byte[] SerializarBrutos(FormatoArquivo formato, IEnumerable<RegistroEntrada> registros)
    {
        ArgumentNullException.ThrowIfNull(registros);
        if (formato == FormatoArquivo.Txt)
        {
            var texto = new StringBuilder();
            foreach (var r in registros)
            {
                texto.Append(r.TextoBruto).Append("\r\n");
            }

            return Utf8SemBom.GetBytes(texto.ToString());
        }

        using var memoria = new MemoryStream();
        using (var escritor = new Utf8JsonWriter(memoria, OpcoesJson))
        {
            escritor.WriteStartArray();
            foreach (var r in registros)
            {
                escritor.WriteRawValue(r.TextoBruto);
            }

            escritor.WriteEndArray();
        }

        return memoria.ToArray();
    }
}
