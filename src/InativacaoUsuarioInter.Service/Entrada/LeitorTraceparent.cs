using System.Diagnostics;
using System.Globalization;
using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Resiliencia;
using Microsoft.Extensions.Logging;

namespace InativacaoUsuarioInter.Service.Entrada;

/// <summary>
/// Lê o sidecar <c>&lt;arquivo&gt;.traceparent</c> (W3C) gravado pelo Inter na replicação (contracts/replicacao.md),
/// para continuar o mesmo trace no Condomínio. A leitura passa pelo pipeline <c>pasta-local</c> (timeout, retry,
/// circuito — Princípio II). Sidecar ausente ou inválido, ou falha de leitura classificada e registrada → <c>null</c>
/// (nova raiz de trace), sem interromper o processamento do arquivo.
/// </summary>
internal sealed partial class LeitorTraceparent(
    ProvedorPipelines pipelines,
    EstadoDependenciasCiclo dependencias,
    ILogger<LeitorTraceparent> logger)
{
    public const string Sufixo = ".traceparent";

    public async Task<ActivityContext?> LerAsync(string caminhoArquivo, CancellationToken cancelamento)
    {
        var sidecar = caminhoArquivo + Sufixo;
        string? texto;
        try
        {
            texto = await pipelines.ExecutarAsync<string?>(
                Dependencias.PastaLocal,
                async ct => File.Exists(sidecar) ? await File.ReadAllTextAsync(sidecar, ct) : null,
                dependencias.Orcamento,
                cancelamento);
        }
        catch (Exception excecao) when (ClassificadorErros.Capturavel(excecao, cancelamento))
        {
            var erro = pipelines.Classificar(Dependencias.PastaLocal, excecao);
            LogFalhaLeitura(logger, Path.GetFileName(sidecar), erro.Categoria);
            return null;
        }

        if (texto is null)
        {
            return null;
        }

        if (ActivityContext.TryParse(texto.Trim(), null, isRemote: true, out var contexto))
        {
            return contexto;
        }

        LogInvalido(logger, Path.GetFileName(sidecar));
        return null;
    }

    /// <summary>Formato W3C <c>00-&lt;trace-id&gt;-&lt;span-id&gt;-&lt;flags&gt;</c>.</summary>
    public static string Formatar(ActivityContext contexto) =>
        $"00-{contexto.TraceId.ToHexString()}-{contexto.SpanId.ToHexString()}-{((int)contexto.TraceFlags).ToString("x2", CultureInfo.InvariantCulture)}";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao ler {Sidecar} ({Categoria}); o arquivo seguirá com nova raiz de trace.")]
    private static partial void LogFalhaLeitura(ILogger logger, string sidecar, CategoriaErro categoria);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sidecar {Sidecar} com formato W3C inválido; ignorado (nova raiz de trace).")]
    private static partial void LogInvalido(ILogger logger, string sidecar);
}
