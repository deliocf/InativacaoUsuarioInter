namespace InativacaoUsuarioInter.Service.Arquivos;

/// <summary>Resultado de uma escrita ou movimentação idempotente.</summary>
public enum ResultadoEscrita
{
    /// <summary>O arquivo foi criado no destino.</summary>
    Gravado,

    /// <summary>O destino já tinha exatamente o mesmo conteúdo (no-op, Princípio III).</summary>
    JaExistia,
}

/// <summary>Destino já existe com conteúdo diferente; nada é sobrescrito (FR-030).</summary>
public sealed class ColisaoArquivoException : IOException
{
    public ColisaoArquivoException()
    {
    }

    public ColisaoArquivoException(string message)
        : base(message)
    {
    }

    public ColisaoArquivoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public static ColisaoArquivoException Para(string caminho) =>
        new($"Já existe um arquivo com conteúdo diferente em '{Path.GetFileName(caminho)}'.");
}

/// <summary>
/// Escrita atômica (temp + rename) e movimentação idempotente (Princípio III, FR-030):
/// um leitor nunca vê arquivo parcial com o nome final e nenhum arquivo existente é sobrescrito.
/// </summary>
internal static class EscritorAtomico
{
    public const string SufixoTemporario = ".tmp";

    public static async Task<ResultadoEscrita> GravarAsync(string destino, ReadOnlyMemory<byte> conteudo, CancellationToken cancelamento)
    {
        var hash = HashConteudo.Calcular(conteudo.Span);
        if (File.Exists(destino))
        {
            return await CompararAsync(destino, hash, cancelamento);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destino))!);
        var temporario = destino + SufixoTemporario;
        await using (var fluxo = new FileStream(
            temporario, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            await fluxo.WriteAsync(conteudo, cancelamento);
            await fluxo.FlushAsync(cancelamento);
        }

        try
        {
            File.Move(temporario, destino, overwrite: false);
        }
        catch (IOException) when (File.Exists(destino))
        {
            File.Delete(temporario);
            return await CompararAsync(destino, hash, cancelamento);
        }

        return ResultadoEscrita.Gravado;
    }

    /// <summary>Move <paramref name="origem"/> para <paramref name="destino"/>; com destino idêntico, apenas remove a origem.</summary>
    public static async Task<ResultadoEscrita> MoverAsync(string origem, string destino, CancellationToken cancelamento)
    {
        if (File.Exists(destino))
        {
            var hashOrigem = await HashConteudo.CalcularArquivoAsync(origem, cancelamento);
            var resultado = await CompararAsync(destino, hashOrigem, cancelamento);
            File.Delete(origem);
            return resultado;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destino))!);
        File.Move(origem, destino, overwrite: false);
        return ResultadoEscrita.Gravado;
    }

    private static async Task<ResultadoEscrita> CompararAsync(string destino, string hashEsperado, CancellationToken cancelamento)
    {
        var hashAtual = await HashConteudo.CalcularArquivoAsync(destino, cancelamento);
        return string.Equals(hashAtual, hashEsperado, StringComparison.Ordinal)
            ? ResultadoEscrita.JaExistia
            : throw ColisaoArquivoException.Para(destino);
    }
}
