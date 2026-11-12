using System.Security.Cryptography;
using System.Text;

namespace InativacaoUsuarioInter.Service.Arquivos;

/// <summary>SHA-256 em hexadecimal minúsculo (identidade de conteúdo e chaves de idempotência).</summary>
internal static class HashConteudo
{
    public static string Calcular(ReadOnlySpan<byte> conteudo) =>
        Convert.ToHexStringLower(SHA256.HashData(conteudo));

    public static string Calcular(string texto) =>
        Calcular(Encoding.UTF8.GetBytes(texto));

    /// <summary>
    /// Identidade de um arquivo de entrada no diário: <c>SHA-256(nome|hashConteudo)</c>. O mesmo arquivo (mesmo nome e
    /// conteúdo) visto de novo é no-op; o mesmo conteúdo com outro nome (ex.: CPFs reenviados num novo arquivo, cuja parte
    /// de sucesso repete bytes já entregues) é um arquivo novo — processado e entregue de novo (bug ja-inativo-sem-replicacao).
    /// Determinística, sem horário nem GUID (Princípio III).
    /// </summary>
    public static string ChaveArquivo(string nome, string hashConteudo) =>
        Calcular($"{nome}|{hashConteudo}");

    /// <summary>Calcula o hash de um arquivo em streaming, sem carregá-lo inteiro em memória.</summary>
    public static async Task<string> CalcularArquivoAsync(string caminho, CancellationToken cancelamento)
    {
        await using var fluxo = new FileStream(
            caminho, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(fluxo, cancelamento);
        return Convert.ToHexStringLower(hash);
    }
}
