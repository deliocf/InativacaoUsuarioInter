using System.Diagnostics;
using InativacaoUsuarioInter.Service.Arquivos;
using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Estado;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Service.Telemetria;
using Microsoft.Extensions.Logging;

namespace InativacaoUsuarioInter.Service.Replicacao;

/// <summary>Entrega Inter → Condomínio (feature 002, FR-006–FR-011, specs/002-ajustes-pastas-fluxo/contracts/replicacao.md).</summary>
public interface IServicoReplicacao
{
    Task EntregarPendentesAsync(CancellationToken cancelamento);
}

/// <summary>
/// Esvazia a fila <c>{FolderPathInter}/Output</c>: reconcilia os arquivos da fila com o diário pelo hash
/// (research R-04) e entrega cada pendente em <c>{FolderPathCondominio}/Input</c> com visibilidade atômica
/// (<c>.tmp</c> → hash conferido → rename → <c>Entregue</c> durável), sempre com o nome original e sem arquivo
/// <c>.traceparent</c> (bug nome-original-sem-trace). Se o Condomínio ainda tiver um arquivo com esse nome e outro
/// conteúdo, a entrega espera na fila. Depois de <c>Entregue</c>, o arquivo sai de <c>Output</c> (ou vai para
/// <c>Processed</c>, se for herdado da versão anterior, FR-004a). Falha do destino é transitória: o arquivo fica na
/// fila para o próximo ciclo (FR-002a).
/// </summary>
internal sealed partial class ServicoReplicacao(
    ConfiguracaoServico configuracao,
    IPoliticaFluxo politica,
    IArmazenamentoEstado estado,
    ProvedorPipelines pipelines,
    EstadoDependenciasCiclo dependencias,
    IPontoFalha pontoFalha,
    TimeProvider relogio,
    ILogger<ServicoReplicacao> logger) : IServicoReplicacao
{
    /// <summary>Quantos hashes de <c>Output</c> foram calculados na última reconciliação (os demais vieram do cache, T098).</summary>
    public int HashesCalculadosNoUltimoCiclo { get; private set; }

    /// <summary>Motivo gravado em <c>entrega.ultima_falha</c> quando o nome ainda está ocupado no Condomínio.</summary>
    public const string DestinoOcupado = "DestinoOcupado";

    public async Task EntregarPendentesAsync(CancellationToken cancelamento)
    {
        HashesCalculadosNoUltimoCiclo = 0;
        if (!politica.Replica || politica.PastaSaida is not { } saida)
        {
            return;
        }

        var orcamento = dependencias.Orcamento;
        try
        {
            dependencias.ArquivosOutputDesconhecidos = await pipelines.ExecutarAsync(
                Dependencias.PastaLocal, ct => new ValueTask<int>(ReconciliarAsync(saida, ct)), orcamento, cancelamento);
        }
        catch (Exception excecao) when (ClassificadorErros.Capturavel(excecao, cancelamento))
        {
            dependencias.MarcarIndisponivel(Dependencias.PastaLocal, pipelines.Classificar(Dependencias.PastaLocal, excecao), relogio.GetUtcNow());
            return;
        }

        foreach (var entrega in await estado.ListarEntregasPendentesAsync(cancelamento))
        {
            if (dependencias.Indisponivel(Dependencias.Replicacao))
            {
                return;
            }

            var origem = Path.Combine(saida, entrega.NomeOutput);
            if (!File.Exists(origem) && !await RestaurarNaFilaAsync(origem, entrega.Hash, cancelamento))
            {
                LogOrigemAusente(logger, entrega.NomeOutput);
                continue;
            }

            using var atividade = Instrumentacao.Iniciar("replicacao.copiar", configuracao.Instancia.ToString(), ActivityKind.Producer);
            atividade?.SetTag(Instrumentacao.Atributos.Arquivo, entrega.NomeOutput);
            atividade?.SetTag(Instrumentacao.Atributos.ChaveIdempotencia, entrega.Hash);
            atividade?.SetTag(Instrumentacao.Atributos.Tentativa, entrega.Tentativas + 1);
            try
            {
                var (ocupado, nomeDestino, traceparent) = await pipelines.ExecutarAsync(
                    Dependencias.Replicacao,
                    ct => new ValueTask<ResultadoCopia>(CopiarAsync(origem, entrega, ct)),
                    orcamento,
                    cancelamento);
                if (ocupado)
                {
                    // O Condomínio ainda não consumiu o arquivo anterior com o mesmo nome: não renomeia nem sobrescreve,
                    // tenta de novo no próximo ciclo. Não é indisponibilidade do destino (as demais entregas seguem).
                    await estado.RegistrarFalhaEntregaAsync(entrega.Hash, entrega.NomeOutput, DestinoOcupado, cancelamento);
                    atividade?.SetTag(Instrumentacao.Atributos.Resultado, "transitoria");
                    atividade?.SetTag(Instrumentacao.Atributos.StatusDetalhado, DestinoOcupado);
                    LogDestinoOcupado(logger, entrega.NomeOutput, nomeDestino);
                    continue;
                }

                await estado.RegistrarEntregaAsync(entrega.Hash, entrega.NomeOutput, nomeDestino, traceparent, relogio.GetUtcNow(), cancelamento);
                atividade?.SetTag(Instrumentacao.Atributos.Resultado, "sucesso");
                LogEntregue(logger, entrega.NomeOutput, nomeDestino);
            }
            catch (Exception excecao) when (ClassificadorErros.Capturavel(excecao, cancelamento))
            {
                var erro = pipelines.Classificar(Dependencias.Replicacao, excecao);
                dependencias.MarcarIndisponivel(Dependencias.Replicacao, erro, relogio.GetUtcNow());
                await estado.RegistrarFalhaEntregaAsync(entrega.Hash, entrega.NomeOutput, erro.Categoria.ToString(), cancelamento);
                atividade?.SetTag(Instrumentacao.Atributos.Resultado, "transitoria");
                LogFalhaEntrega(logger, entrega.NomeOutput, erro.Categoria);
                continue;
            }

            // Entregue de forma durável: só agora sai da fila (FR-008). Falha aqui é retomada na próxima reconciliação.
            pontoFalha.Atingido(PontoFalha.AposRegistrarEntregue);
            try
            {
                await pipelines.ExecutarAsync(
                    Dependencias.PastaLocal, ct => new ValueTask(LimparSaidaAsync(origem, entrega.Hash, ct)), orcamento, cancelamento);
            }
            catch (Exception excecao) when (ClassificadorErros.Capturavel(excecao, cancelamento))
            {
                dependencias.MarcarIndisponivel(Dependencias.PastaLocal, pipelines.Classificar(Dependencias.PastaLocal, excecao), relogio.GetUtcNow());
            }
        }
    }

    private async Task<int> ReconciliarAsync(string saida, CancellationToken cancelamento)
    {
        if (!Directory.Exists(saida))
        {
            return 0;
        }

        var desconhecidos = 0;
        foreach (var caminho in Directory.EnumerateFiles(saida).ToList())
        {
            if (Entrada.LeitorArquivo.FormatoPorExtensao(caminho) is null)
            {
                continue;
            }

            // Só calcula o hash de arquivos novos ou alterados (nome, tamanho e data de escrita), para que o
            // custo do ciclo não cresça com o tamanho da fila (Princípio VI, T098).
            var info = new FileInfo(caminho);
            var escritoEm = new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc));
            var hash = await estado.ObterHashOutputAsync(info.Name, info.Length, escritoEm, cancelamento);
            if (hash is null)
            {
                hash = await HashConteudo.CalcularArquivoAsync(caminho, cancelamento);
                HashesCalculadosNoUltimoCiclo++;
                await estado.RegistrarHashOutputAsync(info.Name, info.Length, escritoEm, hash, cancelamento);
            }

            // Já entregue (reinício entre "Entregue" e a remoção, ou histórico da versão anterior): só limpa a fila.
            // A entrega é do arquivo da fila (nome + conteúdo): o mesmo conteúdo vindo de outra origem tem outro nome
            // e é entregue de novo (bug ja-inativo-sem-replicacao).
            if (await estado.ObterEntregaAsync(hash, info.Name, cancelamento) is { Estado: EstadosEntrega.Entregue } entregue)
            {
                LogJaEntregue(logger, info.Name, entregue.NomeDestino);
                await LimparSaidaAsync(caminho, hash, cancelamento);
                continue;
            }

            var parte = await estado.ObterParteAsync(hash, cancelamento);
            if (parte is { Tipo: TiposParte.Integral or TiposParte.Sucesso })
            {
                await estado.GarantirEntregaAsync(hash, Path.GetFileName(caminho), cancelamento);
                continue;
            }

            // Sem prova de conclusão no Inter: nunca entregar nem remover (FR-011, análise N2).
            desconhecidos++;
            LogDesconhecido(logger, Path.GetFileName(caminho), hash);
        }

        return desconhecidos;
    }

    /// <summary>
    /// Research R-04: remove <paramref name="caminho"/> da fila se <c>Processed</c> já tem o mesmo conteúdo (mesmo nome ou
    /// nome de colisão); senão o move para <c>Processed</c> (arquivo herdado, cujo histórico vivia em <c>Output</c>).
    /// </summary>
    private async Task LimparSaidaAsync(string caminho, string hash, CancellationToken cancelamento)
    {
        if (!File.Exists(caminho))
        {
            return;
        }

        using var atividade = Instrumentacao.Iniciar("replicacao.limpar_output", configuracao.Instancia.ToString());
        var nome = Path.GetFileName(caminho);
        atividade?.SetTag(Instrumentacao.Atributos.Arquivo, nome);
        atividade?.SetTag(Instrumentacao.Atributos.ChaveIdempotencia, hash);
        var noHistorico = Path.Combine(politica.PastaConcluidos, nome);
        var colisao = Path.Combine(politica.PastaConcluidos, NomeadorPartes.Colisao(nome, hash));
        if (await MesmoConteudoAsync(noHistorico, hash, cancelamento)
            || await MesmoConteudoAsync(colisao, hash, cancelamento)
            || await HistoricoGravadoAsync(hash, cancelamento))
        {
            // O histórico já está em Processed (possivelmente mesclado com outra parte do mesmo original).
            File.Delete(caminho);
            ExcluirSeExistir(caminho + GerenciadorArquivos.SufixoTraceparent);
            atividade?.SetTag(Instrumentacao.Atributos.StatusDetalhado, "removido");
            return;
        }

        // Herdado da versão anterior (histórico vivia em Output): vai para Processed com o nome original; colisão → mescla.
        ExcluirSeExistir(caminho + GerenciadorArquivos.SufixoTraceparent);
        await MescladorHistorico.MoverAsync(caminho, noHistorico, cancelamento);
        atividade?.SetTag(Instrumentacao.Atributos.StatusDetalhado, "movido_para_processed");
        LogHerdadoMovido(logger, nome);
    }

    /// <summary>
    /// A parte foi gerada pelo serviço e o seu histórico existe fora de <c>Output</c> (em <c>Processed</c>, com o nome
    /// original, possivelmente mesclado com outra parte do mesmo original). Partes herdadas da versão anterior tinham
    /// <c>caminho_final</c> na própria fila; histórico ausente também cai no caminho de mover para <c>Processed</c>.
    /// </summary>
    private async Task<bool> HistoricoGravadoAsync(string hash, CancellationToken cancelamento)
    {
        if (await estado.ObterParteAsync(hash, cancelamento) is not { Tipo: TiposParte.Integral or TiposParte.Sucesso } parte
            || politica.PastaSaida is not { } saida
            || !File.Exists(parte.CaminhoFinal))
        {
            return false;
        }

        var pasta = Path.GetDirectoryName(Path.GetFullPath(parte.CaminhoFinal));
        return !string.Equals(
            pasta?.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(saida).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Entrega pendente sem o arquivo na fila: recria a cópia a partir de <c>Processed</c>, se o hash conferir.</summary>
    private async Task<bool> RestaurarNaFilaAsync(string origem, string hash, CancellationToken cancelamento)
    {
        try
        {
            return await pipelines.ExecutarAsync(
                Dependencias.PastaLocal,
                async ct =>
                {
                    if (await estado.ObterParteAsync(hash, ct) is not { } parte
                        || !await MesmoConteudoAsync(parte.CaminhoFinal, hash, ct))
                    {
                        return false;
                    }

                    await EscritorAtomico.GravarAsync(origem, await File.ReadAllBytesAsync(parte.CaminhoFinal, ct), ct);
                    return true;
                },
                dependencias.Orcamento,
                cancelamento);
        }
        catch (Exception excecao) when (ClassificadorErros.Capturavel(excecao, cancelamento))
        {
            dependencias.MarcarIndisponivel(Dependencias.PastaLocal, pipelines.Classificar(Dependencias.PastaLocal, excecao), relogio.GetUtcNow());
            return false;
        }
    }

    private async Task<ResultadoCopia> CopiarAsync(string origem, EntregaPendente entrega, CancellationToken cancelamento)
    {
        // Criada aqui, dentro do pipeline de replicação: indisponibilidade do Condomínio não interrompe o ciclo (FR-002a).
        var pastaDestino = Path.Combine(configuracao.RaizCondominio, NomesPastas.Input);
        Directory.CreateDirectory(pastaDestino);

        // Nome original, nunca renomeado (bug nome-original-sem-trace). Entregas antigas não guardavam o nome de destino.
        var nome = entrega.NomeDestino ?? entrega.NomeOutput;
        var final = Path.Combine(pastaDestino, nome);
        if (File.Exists(final))
        {
            return await HashConteudo.CalcularArquivoAsync(final, cancelamento) == entrega.Hash
                ? new ResultadoCopia(false, nome, null)
                : new ResultadoCopia(true, nome, null);
        }

        var temporario = final + EscritorAtomico.SufixoTemporario;
        if (File.Exists(temporario))
        {
            File.Delete(temporario);
        }

        await using (var leitura = new FileStream(origem, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
        await using (var escrita = new FileStream(
            temporario, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await leitura.CopyToAsync(escrita, cancelamento);
            await escrita.FlushAsync(cancelamento);
        }

        if (await HashConteudo.CalcularArquivoAsync(temporario, cancelamento) != entrega.Hash)
        {
            File.Delete(temporario);
            throw new IOException($"Cópia de '{entrega.NomeOutput}' com hash divergente.");
        }

        pontoFalha.Atingido(PontoFalha.AposCopiaTemporaria);

        // Sem sidecar .traceparent no Condomínio (bug nome-original-sem-trace, constituição v2.0.0, Princípio IV): o
        // contexto fica só no diário do Inter (entrega.traceparent); o Condomínio abre uma raiz de trace própria.
        var traceparent = Activity.Current?.IdFormat == ActivityIdFormat.W3C ? Activity.Current.Id : null;
        File.Move(temporario, final, overwrite: false);
        return new ResultadoCopia(false, nome, traceparent);
    }

    /// <summary>Resultado da cópia: <paramref name="Ocupado"/> = o Condomínio já tem outro conteúdo com esse nome.</summary>
    private readonly record struct ResultadoCopia(bool Ocupado, string NomeDestino, string? Traceparent);

    private static async Task<bool> MesmoConteudoAsync(string caminho, string hash, CancellationToken cancelamento) =>
        File.Exists(caminho) && string.Equals(await HashConteudo.CalcularArquivoAsync(caminho, cancelamento), hash, StringComparison.Ordinal);

    private static void ExcluirSeExistir(string caminho)
    {
        if (File.Exists(caminho))
        {
            File.Delete(caminho);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Entregue {Arquivo} ao Condomínio como {Destino}.")]
    private static partial void LogEntregue(ILogger logger, string arquivo, string destino);

    [LoggerMessage(Level = LogLevel.Information, Message = "Entrega de {Arquivo} aguarda: o Condomínio ainda tem outro arquivo {Destino}; permanece em Output, nova tentativa no próximo ciclo.")]
    private static partial void LogDestinoOcupado(ILogger logger, string arquivo, string destino);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao entregar {Arquivo} ({Categoria}); permanece em Output, nova tentativa no próximo ciclo.")]
    private static partial void LogFalhaEntrega(ILogger logger, string arquivo, CategoriaErro categoria);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Arquivo {Arquivo} (hash {Hash}) em Output não foi gerado pelo serviço; não será entregue (ARQUIVO_OUTPUT_DESCONHECIDO).")]
    private static partial void LogDesconhecido(ILogger logger, string arquivo, string hash);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Entrega pendente de {Arquivo} sem origem em Output nem em Processed; ignorada.")]
    private static partial void LogOrigemAusente(ILogger logger, string arquivo);

    [LoggerMessage(Level = LogLevel.Information, Message = "Arquivo {Arquivo} em Output já foi entregue ao Condomínio como {Destino}; sai da fila sem nova cópia.")]
    private static partial void LogJaEntregue(ILogger logger, string arquivo, string? destino);

    [LoggerMessage(Level = LogLevel.Information, Message = "Arquivo {Arquivo} já entregue foi movido de Output para Processed (histórico da versão anterior).")]
    private static partial void LogHerdadoMovido(ILogger logger, string arquivo);
}
