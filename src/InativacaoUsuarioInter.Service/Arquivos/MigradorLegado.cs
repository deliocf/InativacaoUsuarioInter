using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Service.Telemetria;
using Microsoft.Extensions.Logging;

namespace InativacaoUsuarioInter.Service.Arquivos;

/// <summary>
/// Migração automática das pastas com nomes antigos (feature 002, FR-004, research R-06,
/// contracts/estrutura-pastas.md). Roda a cada ciclo, depois de garantir as pastas novas e antes de ler
/// <c>Input</c>; só age se alguma pasta antiga existir. Idempotente e retomável: arquivos são movidos com a
/// regra de colisão (mesmo hash → no-op), logs são mesclados com detecção de prefixo, <c>.tmp</c> nunca são
/// tocados (podem ser uma cópia em andamento de um Inter antigo) e a pasta antiga só é removida quando vazia.
/// O diário não precisa mudar: arquivos e registros são indexados pelo hash do conteúdo.
/// </summary>
internal sealed partial class MigradorLegado(
    IPoliticaFluxo politica,
    ProvedorPipelines pipelines,
    EstadoDependenciasCiclo dependencias,
    IPontoFalha pontoFalha,
    TimeProvider relogio,
    ILogger<MigradorLegado> logger)
{
    private static readonly (string Antiga, string Nova)[] Mapa =
    [
        (NomesPastasLegado.Imput, NomesPastas.Input),
        (NomesPastasLegado.Processados, NomesPastas.Processed),
        (NomesPastasLegado.Falhas, NomesPastas.Fail),
        (NomesPastasLegado.Auditoria, NomesPastas.Logs),
    ];

    /// <summary>Migra o que houver; devolve quantos itens (arquivos ou logs) foram migrados neste ciclo.</summary>
    public async Task<int> MigrarAsync(OrcamentoCiclo? orcamento, CancellationToken cancelamento)
    {
        var pendentes = Mapa.Where(m => Directory.Exists(Path.Combine(politica.Raiz, m.Antiga))).ToArray();
        if (pendentes.Length == 0)
        {
            return 0;
        }

        using var atividade = Instrumentacao.Iniciar("arquivo.migrar_legado", politica.Instancia.ToString());
        var migrados = 0;
        foreach (var (antiga, nova) in pendentes)
        {
            try
            {
                migrados += await pipelines.ExecutarAsync(
                    Dependencias.PastaLocal,
                    ct => new ValueTask<int>(MigrarPastaAsync(antiga, nova, ct)),
                    orcamento,
                    cancelamento);
            }
            catch (Exception excecao) when (ClassificadorErros.Capturavel(excecao, cancelamento))
            {
                // Transitório: o que já foi movido fica; o resto é retomado no próximo ciclo (FR-004).
                var erro = pipelines.Classificar(Dependencias.PastaLocal, excecao);
                dependencias.MarcarIndisponivel(Dependencias.PastaLocal, erro, relogio.GetUtcNow());
                atividade?.SetTag(Instrumentacao.Atributos.Resultado, "transitoria");
                LogFalha(logger, antiga, erro.Categoria);
            }
        }

        atividade?.SetTag(Instrumentacao.Atributos.StatusDetalhado, migrados);
        return migrados;
    }

    private async Task<int> MigrarPastaAsync(string antiga, string nova, CancellationToken cancelamento)
    {
        var origem = Path.Combine(politica.Raiz, antiga);
        var destino = Path.Combine(politica.Raiz, nova);
        if (!Directory.Exists(origem))
        {
            return 0;
        }

        Directory.CreateDirectory(destino);
        var migrados = 0;
        foreach (var caminho in Directory.GetFiles(origem))
        {
            var nome = Path.GetFileName(caminho);
            if (nome.EndsWith(EscritorAtomico.SufixoTemporario, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(nome, NomesPastas.NomeLog, StringComparison.OrdinalIgnoreCase)
                && antiga is NomesPastasLegado.Falhas or NomesPastasLegado.Auditoria)
            {
                var resultado = await MesclaLog.MesclarAsync(
                    caminho, Path.Combine(destino, NomesPastas.NomeLog), () => pontoFalha.Atingido(PontoFalha.AposSubstituirLogLegado), cancelamento);
                if (resultado != ResultadoMescla.SemLogAntigo)
                {
                    migrados++;
                    LogMigrado(logger, nome, antiga, nova);
                }

                continue;
            }

            if (nome.EndsWith(GerenciadorArquivos.SufixoTraceparent, StringComparison.OrdinalIgnoreCase))
            {
                // Sidecar acompanha o arquivo principal; órfão (principal já migrado) vai para o mesmo nome no destino.
                var principal = caminho[..^GerenciadorArquivos.SufixoTraceparent.Length];
                if (!File.Exists(principal))
                {
                    MoverSidecar(caminho, Path.Combine(destino, nome));
                }

                continue;
            }

            if (!File.Exists(caminho))
            {
                continue;
            }

            try
            {
                var hash = await HashConteudo.CalcularArquivoAsync(caminho, cancelamento);
                var final = await GerenciadorArquivos.DestinoLivreAsync(Path.Combine(destino, nome), hash, cancelamento);

                // Sidecar antes do principal: numa retomada o destino é recalculado igual (mesmo hash → mesmo nome).
                MoverSidecar(caminho + GerenciadorArquivos.SufixoTraceparent, final + GerenciadorArquivos.SufixoTraceparent);
                await EscritorAtomico.MoverAsync(caminho, final, cancelamento);
            }
            catch (IOException excecao) when (ClassificadorErros.Classificar(excecao).Categoria == CategoriaErro.Bloqueio)
            {
                // Arquivo em uso por outro processo: adia só este arquivo (como a entrada faz com arquivos bloqueados),
                // sem abrir o circuito de pasta-local nem travar o ciclo; a pasta antiga fica até ele sair.
                LogAdiado(logger, nome, antiga, ClassificadorErros.Classificar(excecao).Categoria);
                continue;
            }

            pontoFalha.Atingido(PontoFalha.AposMoverLegado);
            migrados++;
            LogMigrado(logger, nome, antiga, nova);
        }

        if (!Directory.EnumerateFileSystemEntries(origem).Any())
        {
            Directory.Delete(origem);
            LogPastaRemovida(logger, antiga);
        }

        return migrados;
    }

    private static void MoverSidecar(string origem, string destino)
    {
        if (!File.Exists(origem))
        {
            return;
        }

        if (File.Exists(destino))
        {
            File.Delete(origem);
            return;
        }

        File.Move(origem, destino, overwrite: false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MigracaoLegado: {Arquivo} migrado de {PastaOrigem} para {PastaDestino}.")]
    private static partial void LogMigrado(ILogger logger, string arquivo, string pastaOrigem, string pastaDestino);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MigracaoLegado: {Arquivo} em {Pasta} está em uso ({Categoria}); adiado para o próximo ciclo.")]
    private static partial void LogAdiado(ILogger logger, string arquivo, string pasta, CategoriaErro categoria);

    [LoggerMessage(Level = LogLevel.Information, Message = "MigracaoLegado: pasta antiga {Pasta} vazia e removida.")]
    private static partial void LogPastaRemovida(ILogger logger, string pasta);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MigracaoLegado: falha ao migrar a pasta {Pasta} ({Categoria}); retomada no próximo ciclo.")]
    private static partial void LogFalha(ILogger logger, string pasta, CategoriaErro categoria);
}

/// <summary>Resultado da mescla de um log antigo (research R-06).</summary>
internal enum ResultadoMescla
{
    SemLogAntigo,
    Mesclado,
    JaMesclado,
}

/// <summary>
/// Mescla de log idempotente sem estado extra (research R-06): o destino passa a ser
/// <c>antigo + destino atual</c>, trocado de forma atômica; se o destino já começa com os bytes exatos do
/// antigo (crash entre a troca e a remoção), apenas remove o antigo. Leitura e cópia em streaming.
/// </summary>
internal static class MesclaLog
{
    private const int Buffer = 81920;
    private static readonly byte[] QuebraLinha = "\r\n"u8.ToArray();

    public static async Task<ResultadoMescla> MesclarAsync(
        string antigo, string destino, Action? aposSubstituir, CancellationToken cancelamento)
    {
        if (!File.Exists(antigo))
        {
            return ResultadoMescla.SemLogAntigo;
        }

        var tamanhoAntigo = new FileInfo(antigo).Length;
        if (tamanhoAntigo == 0 || (File.Exists(destino) && await ComecaComAsync(destino, antigo, cancelamento)))
        {
            File.Delete(antigo);
            return ResultadoMescla.JaMesclado;
        }

        var temporario = destino + EscritorAtomico.SufixoTemporario;
        if (File.Exists(temporario))
        {
            File.Delete(temporario);
        }

        await using (var escrita = new FileStream(
            temporario, FileMode.CreateNew, FileAccess.Write, FileShare.None, Buffer, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await using (var leitura = Abrir(antigo))
            {
                await leitura.CopyToAsync(escrita, cancelamento);
            }

            if (File.Exists(destino))
            {
                if (!await TerminaComQuebraAsync(antigo, cancelamento))
                {
                    await escrita.WriteAsync(QuebraLinha, cancelamento);
                }

                await using var atual = Abrir(destino);
                await atual.CopyToAsync(escrita, cancelamento);
            }

            await escrita.FlushAsync(cancelamento);
        }

        if (File.Exists(destino))
        {
            File.Replace(temporario, destino, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temporario, destino, overwrite: false);
        }

        aposSubstituir?.Invoke();
        File.Delete(antigo);
        return ResultadoMescla.Mesclado;
    }

    private static FileStream Abrir(string caminho) =>
        new(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, Buffer, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<bool> ComecaComAsync(string destino, string prefixo, CancellationToken cancelamento)
    {
        await using var a = Abrir(destino);
        await using var b = Abrir(prefixo);
        if (a.Length < b.Length)
        {
            return false;
        }

        var bufferA = new byte[Buffer];
        var bufferB = new byte[Buffer];
        while (true)
        {
            var lidos = await b.ReadAsync(bufferB, cancelamento);
            if (lidos == 0)
            {
                return true;
            }

            await a.ReadExactlyAsync(bufferA.AsMemory(0, lidos), cancelamento);
            if (!bufferA.AsSpan(0, lidos).SequenceEqual(bufferB.AsSpan(0, lidos)))
            {
                return false;
            }
        }
    }

    private static async Task<bool> TerminaComQuebraAsync(string caminho, CancellationToken cancelamento)
    {
        await using var fluxo = Abrir(caminho);
        if (fluxo.Length == 0)
        {
            return true;
        }

        fluxo.Seek(-1, SeekOrigin.End);
        var ultimo = new byte[1];
        await fluxo.ReadExactlyAsync(ultimo, cancelamento);
        return ultimo[0] == (byte)'\n';
    }
}
