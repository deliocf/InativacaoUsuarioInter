using System.Text;
using InativacaoUsuarioInter.Service.Arquivos;
using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Estado;
using InativacaoUsuarioInter.Service.Resiliencia;

namespace InativacaoUsuarioInter.Service.Registros;

/// <summary>Logs contratuais de auditoria (<c>Logs/</c>) e falhas (<c>Fail/</c>), somente acréscimo (FR-003).</summary>
public interface IRegistroLogs
{
    /// <summary>Acrescenta exatamente uma linha (via pipeline <c>pasta-local</c>).</summary>
    Task AnexarAsync(TipoLog tipo, string linha, OrcamentoCiclo? orcamento, CancellationToken cancelamento);

    /// <summary>Verifica, em streaming, se a linha exata já está no log (recuperação, research R-08).</summary>
    Task<bool> ContemLinhaAsync(TipoLog tipo, string linha, CancellationToken cancelamento);

    string Caminho(TipoLog tipo);
}

/// <summary>
/// Escritor append-only: UTF-8 sem BOM, <c>\r\n</c>, <c>FileShare.Read</c>, <c>WriteThrough</c>.
/// Nunca reescreve nem trunca. Se uma tentativa anterior falhou, confere se a linha já foi gravada
/// antes de repetir, para não duplicar (Princípio III).
/// </summary>
internal sealed class EscritorLogAppendOnly(ConfiguracaoServico configuracao, ProvedorPipelines pipelines) : IRegistroLogs
{
    public const string NomeArquivo = NomesPastas.NomeLog;

    private static readonly UTF8Encoding Utf8SemBom = new(encoderShouldEmitUTF8Identifier: false);

    public string Caminho(TipoLog tipo) =>
        Path.Combine(configuracao.RaizInstancia, tipo == TipoLog.Auditoria ? NomesPastas.Logs : NomesPastas.Fail, NomeArquivo);

    public async Task AnexarAsync(TipoLog tipo, string linha, OrcamentoCiclo? orcamento, CancellationToken cancelamento)
    {
        ArgumentNullException.ThrowIfNull(linha);
        var caminho = Caminho(tipo);
        var bytes = Utf8SemBom.GetBytes(linha + "\r\n");
        var tentativaAnteriorFalhou = false;

        await pipelines.ExecutarAsync(
            Dependencias.PastaLocal,
            async ct =>
            {
                if (tentativaAnteriorFalhou && await ContemLinhaAsync(tipo, linha, ct))
                {
                    return;
                }

                tentativaAnteriorFalhou = true;
                Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
                await using var fluxo = new FileStream(
                    caminho, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous);
                await fluxo.WriteAsync(bytes, ct);
                await fluxo.FlushAsync(ct);
                tentativaAnteriorFalhou = false;
            },
            orcamento,
            cancelamento);
    }

    public async Task<bool> ContemLinhaAsync(TipoLog tipo, string linha, CancellationToken cancelamento)
    {
        var caminho = Caminho(tipo);
        if (!File.Exists(caminho))
        {
            return false;
        }

        using var leitor = new StreamReader(
            new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan),
            Utf8SemBom);
        while (await leitor.ReadLineAsync(cancelamento) is { } existente)
        {
            if (string.Equals(existente, linha, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
