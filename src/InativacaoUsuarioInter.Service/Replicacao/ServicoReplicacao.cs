using System.Diagnostics;
using InativacaoUsuarioInter.Service.Arquivos;
using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Estado;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Service.Telemetria;
using Microsoft.Extensions.Logging;

namespace InativacaoUsuarioInter.Service.Replicacao;

/// <summary>Replicação Inter → Condomínio (FR-032–FR-036, contracts/replicacao.md).</summary>
public interface IServicoReplicacao
{
    Task EntregarPendentesAsync(CancellationToken cancelamento);
}

/// <summary>
/// Reconcilia <c>Output</c> com o diário pelo hash (só partes <c>Integral</c>/<c>Sucesso</c> geradas pelo Inter,
/// análises M2/N2) e entrega cada pendente com visibilidade atômica: <c>.tmp</c> → hash conferido →
/// sidecar <c>.traceparent</c> → rename → registro durável. Falha mantém a origem e repete no próximo ciclo.
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

    public async Task EntregarPendentesAsync(CancellationToken cancelamento)
    {
        HashesCalculadosNoUltimoCiclo = 0;
        if (!politica.Replica)
        {
            return;
        }

        var orcamento = dependencias.Orcamento;
        try
        {
            dependencias.ArquivosOutputDesconhecidos = await pipelines.ExecutarAsync(
                Dependencias.PastaLocal, ct => new ValueTask<int>(ReconciliarAsync(ct)), orcamento, cancelamento);
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

            var origem = Path.Combine(politica.PastaConcluidos, entrega.NomeOutput);
            if (!File.Exists(origem))
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
                var (nomeDestino, traceparent) = await pipelines.ExecutarAsync(
                    Dependencias.Replicacao,
                    ct => new ValueTask<(string, string?)>(CopiarAsync(origem, entrega, ct)),
                    orcamento,
                    cancelamento);
                await estado.RegistrarEntregaAsync(entrega.Hash, nomeDestino, traceparent, relogio.GetUtcNow(), cancelamento);
                atividade?.SetTag(Instrumentacao.Atributos.Resultado, "sucesso");
                LogEntregue(logger, entrega.NomeOutput, nomeDestino);
            }
            catch (Exception excecao) when (ClassificadorErros.Capturavel(excecao, cancelamento))
            {
                var erro = pipelines.Classificar(Dependencias.Replicacao, excecao);
                dependencias.MarcarIndisponivel(Dependencias.Replicacao, erro, relogio.GetUtcNow());
                await estado.RegistrarFalhaEntregaAsync(entrega.Hash, erro.Categoria.ToString(), cancelamento);
                atividade?.SetTag(Instrumentacao.Atributos.Resultado, "transitoria");
                LogFalhaEntrega(logger, entrega.NomeOutput, erro.Categoria);
            }
        }
    }

    private async Task<int> ReconciliarAsync(CancellationToken cancelamento)
    {
        var pasta = politica.PastaConcluidos;
        if (!Directory.Exists(pasta))
        {
            return 0;
        }

        var desconhecidos = 0;
        foreach (var caminho in Directory.EnumerateFiles(pasta))
        {
            if (Entrada.LeitorArquivo.FormatoPorExtensao(caminho) is null)
            {
                continue;
            }

            // Só calcula o hash de arquivos novos ou alterados (nome, tamanho e data de escrita), para que o
            // custo do ciclo não cresça com o histórico de Output (Princípio VI, T098).
            var info = new FileInfo(caminho);
            var escritoEm = new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc));
            var hash = await estado.ObterHashOutputAsync(info.Name, info.Length, escritoEm, cancelamento);
            if (hash is null)
            {
                hash = await HashConteudo.CalcularArquivoAsync(caminho, cancelamento);
                HashesCalculadosNoUltimoCiclo++;
                await estado.RegistrarHashOutputAsync(info.Name, info.Length, escritoEm, hash, cancelamento);
            }

            var parte = await estado.ObterParteAsync(hash, cancelamento);
            if (parte is { Tipo: TiposParte.Integral or TiposParte.Sucesso })
            {
                await estado.GarantirEntregaAsync(hash, Path.GetFileName(caminho), cancelamento);
                continue;
            }

            // Sem prova de conclusão no Inter: nunca replicar (FR-035, análise N2).
            desconhecidos++;
            LogDesconhecido(logger, Path.GetFileName(caminho), hash);
        }

        return desconhecidos;
    }

    private async Task<(string NomeDestino, string? Traceparent)> CopiarAsync(
        string origem, EntregaPendente entrega, CancellationToken cancelamento)
    {
        var pastaDestino = Path.Combine(configuracao.RaizCondominio, "Imput");
        Directory.CreateDirectory(pastaDestino);

        var nome = entrega.NomeOutput;
        var final = Path.Combine(pastaDestino, nome);
        if (File.Exists(final))
        {
            if (await HashConteudo.CalcularArquivoAsync(final, cancelamento) == entrega.Hash)
            {
                return (nome, null);
            }

            nome = NomeadorPartes.Colisao(nome, entrega.Hash);
            final = Path.Combine(pastaDestino, nome);
            if (File.Exists(final) && await HashConteudo.CalcularArquivoAsync(final, cancelamento) == entrega.Hash)
            {
                return (nome, null);
            }
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
        var traceparent = Activity.Current?.IdFormat == ActivityIdFormat.W3C ? Activity.Current.Id : null;
        if (traceparent is not null)
        {
            await File.WriteAllTextAsync(final + GerenciadorArquivos.SufixoTraceparent, traceparent, cancelamento);
        }

        File.Move(temporario, final, overwrite: false);
        return (nome, traceparent);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Replicado {Arquivo} para o Condomínio como {Destino}.")]
    private static partial void LogEntregue(ILogger logger, string arquivo, string destino);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao replicar {Arquivo} ({Categoria}); origem preservada, nova tentativa no próximo ciclo.")]
    private static partial void LogFalhaEntrega(ILogger logger, string arquivo, CategoriaErro categoria);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Arquivo {Arquivo} (hash {Hash}) em Output não foi gerado pelo serviço; não será replicado (ARQUIVO_OUTPUT_DESCONHECIDO).")]
    private static partial void LogDesconhecido(ILogger logger, string arquivo, string hash);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Entrega pendente de {Arquivo} sem origem em Output; ignorada.")]
    private static partial void LogOrigemAusente(ILogger logger, string arquivo);
}
