using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Resiliencia;
using Microsoft.Extensions.Logging;

namespace InativacaoUsuarioInter.Service.Entrada;

/// <summary>
/// Lista os arquivos elegíveis em <c>&lt;raiz&gt;/Imput</c> (FR-008, research R-09): somente <c>.json</c>/<c>.txt</c>;
/// adia arquivos escritos há menos de <c>EstabilizacaoSegundos</c> ou que não abrem com acesso exclusivo
/// (upload ou cópia em andamento, CA-14). Ordena por data de escrita.
/// </summary>
internal sealed partial class DetectorElegiveis(
    IPoliticaFluxo politica,
    ConfiguracaoServico configuracao,
    TimeProvider relogio,
    ProvedorPipelines pipelines,
    ILogger<DetectorElegiveis> logger)
{
    public Task<IReadOnlyList<string>> ListarAsync(OrcamentoCiclo? orcamento, CancellationToken cancelamento) =>
        pipelines.ExecutarAsync<IReadOnlyList<string>>(
            Dependencias.PastaLocal,
            _ => ValueTask.FromResult<IReadOnlyList<string>>(Listar()),
            orcamento,
            cancelamento);

    /// <summary>
    /// Data de escrita do arquivo <c>.json</c>/<c>.txt</c> mais antigo em <c>Imput</c>, inclusive os adiados
    /// (instáveis ou bloqueados) — base da métrica <c>idade_arquivo_entrada_mais_antigo_s</c> (Princípio IV).
    /// </summary>
    public Task<DateTimeOffset?> ArquivoMaisAntigoAsync(OrcamentoCiclo? orcamento, CancellationToken cancelamento) =>
        pipelines.ExecutarAsync<DateTimeOffset?>(
            Dependencias.PastaLocal,
            _ =>
            {
                var pasta = politica.PastaEntrada;
                if (!Directory.Exists(pasta))
                {
                    return ValueTask.FromResult<DateTimeOffset?>(null);
                }

                DateTime? maisAntigo = null;
                foreach (var arquivo in new DirectoryInfo(pasta).EnumerateFiles())
                {
                    if (LeitorArquivo.FormatoPorExtensao(arquivo.Name) is not null
                        && (maisAntigo is null || arquivo.LastWriteTimeUtc < maisAntigo))
                    {
                        maisAntigo = arquivo.LastWriteTimeUtc;
                    }
                }

                return ValueTask.FromResult<DateTimeOffset?>(
                    maisAntigo is { } data ? new DateTimeOffset(DateTime.SpecifyKind(data, DateTimeKind.Utc)) : null);
            },
            orcamento,
            cancelamento);

    private List<string> Listar()
    {
        var pasta = politica.PastaEntrada;
        if (!Directory.Exists(pasta))
        {
            return [];
        }

        var limite = relogio.GetUtcNow().UtcDateTime.AddSeconds(-configuracao.Processamento.EstabilizacaoSegundos);
        var elegiveis = new List<FileInfo>();
        foreach (var arquivo in new DirectoryInfo(pasta).EnumerateFiles())
        {
            if (LeitorArquivo.FormatoPorExtensao(arquivo.Name) is null)
            {
                continue;
            }

            if (arquivo.LastWriteTimeUtc > limite)
            {
                LogAdiado(logger, arquivo.Name, "instavel");
                continue;
            }

            if (!AbreExclusivo(arquivo.FullName))
            {
                LogAdiado(logger, arquivo.Name, "em-uso");
                continue;
            }

            elegiveis.Add(arquivo);
        }

        return elegiveis
            .OrderBy(a => a.LastWriteTimeUtc)
            .ThenBy(a => a.Name, StringComparer.Ordinal)
            .Select(a => a.FullName)
            .ToList();
    }

    private static bool AbreExclusivo(string caminho)
    {
        try
        {
            using var fluxo = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Arquivo {Arquivo} adiado para o próximo ciclo ({Motivo}).")]
    private static partial void LogAdiado(ILogger logger, string arquivo, string motivo);
}
