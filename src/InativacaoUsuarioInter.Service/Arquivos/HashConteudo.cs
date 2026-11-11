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

    /// <summary>Calcula o hash de um arquivo em streaming, sem carregá-lo inteiro em memória.</summary>
    public static async Task<string> CalcularArquivoAsync(string caminho, CancellationToken cancelamento)
    {
        await using var fluxo = new FileStream(
            caminho, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(fluxo, cancelamento);
        return Convert.ToHexStringLower(hash);
    }
}
