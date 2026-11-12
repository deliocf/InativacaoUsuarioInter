using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Entrada;
using InativacaoUsuarioInter.Service.Entrada.Modelos;
using InativacaoUsuarioInter.Service.Estado;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Service.Saude;

namespace InativacaoUsuarioInter.Service.Arquivos;

/// <summary>Registros de um arquivo agrupados pelo destino (FR-028/FR-029).</summary>
public sealed record GruposFinalizacao(
    IReadOnlyList<RegistroEntrada> Sucessos,
    IReadOnlyList<RegistroEntrada> Falhas,
    IReadOnlyList<RegistroEntrada> Pendentes);

/// <summary>Como o arquivo foi finalizado.</summary>
public enum Finalizacao
{
    /// <summary>Somente pendências: o arquivo permanece intacto na entrada.</summary>
    SemAlteracao,
    Integral,
    Segregado,
    Concluido,
}

/// <summary>Destino dos arquivos de entrada (FR-028–FR-031).</summary>
public interface IGerenciadorArquivos
{
    Task GarantirEstruturaAsync(OrcamentoCiclo? orcamento, CancellationToken cancelamento);

    Task<Finalizacao> FinalizarAsync(ResultadoLeitura leitura, GruposFinalizacao grupos, OrcamentoCiclo? orcamento, CancellationToken cancelamento);

    Task RejeitarAsync(ResultadoLeitura leitura, OrcamentoCiclo? orcamento, CancellationToken cancelamento);

    Task RemoverDuplicadoAsync(string caminho, OrcamentoCiclo? orcamento, CancellationToken cancelamento);

    /// <summary>Remove <c>.tmp</c> órfãos deixados pelo próprio serviço nas pastas de saída.</summary>
    Task LimparTemporariosAsync(OrcamentoCiclo? orcamento, CancellationToken cancelamento);
}

/// <summary>
/// Implementação com escrita atômica e ordem tolerante a crash (research R-10):
/// partes gravadas → diário → original removido. Nada é sobrescrito; destino idêntico é no-op.
/// Feature 002 (research R-03/R-05): no Inter, o conteúdo de sucesso vai para <c>Processed</c> (histórico) e,
/// antes do registro no diário, uma cópia vai para a fila <c>Output</c> com a entrega pendente; o arquivo de
/// falhas usa o nome do original em <c>Fail</c>.
/// </summary>
internal sealed class GerenciadorArquivos(
    IPoliticaFluxo politica,
    IArmazenamentoEstado estado,
    ProvedorPipelines pipelines,
    IPontoFalha pontoFalha,
    TimeProvider relogio) : IGerenciadorArquivos
{
    public const string SufixoTraceparent = ".traceparent";

    public Task GarantirEstruturaAsync(OrcamentoCiclo? orcamento, CancellationToken cancelamento) =>
        pipelines.ExecutarAsync(
            Dependencias.PastaLocal,
            _ =>
            {
                // Somente pastas da própria raiz: falha aqui interrompe o ciclo. A pasta de entrega
                // {FolderPathCondominio}/Input é criada na entrega (FR-002a), nunca aqui.
                foreach (var pasta in politica.PastasObrigatorias)
                {
                    var caminho = Path.Combine(politica.Raiz, pasta);
                    if (pasta is NomesPastas.Logs or NomesPastas.Fail)
                    {
                        // Dados pessoais (FR-038): em Unix, criadas só para o dono (0700).
                        DiretoriosRestritos.Criar(caminho);
                    }
                    else
                    {
                        Directory.CreateDirectory(caminho);
                    }
                }

                return ValueTask.CompletedTask;
            },
            orcamento,
            cancelamento);

    public async Task<Finalizacao> FinalizarAsync(
        ResultadoLeitura leitura, GruposFinalizacao grupos, OrcamentoCiclo? orcamento, CancellationToken cancelamento)
    {
        ArgumentNullException.ThrowIfNull(leitura);
        ArgumentNullException.ThrowIfNull(grupos);

        // Só pendências: nada concluído, o arquivo fica intacto para a próxima tentativa (CA-06).
        if (grupos.Sucessos.Count == 0 && grupos.Falhas.Count == 0)
        {
            return Finalizacao.SemAlteracao;
        }

        var integral = !leitura.EhParteConhecida && grupos.Falhas.Count == 0 && grupos.Pendentes.Count == 0;
        return integral
            ? await MoverIntegralAsync(leitura, orcamento, cancelamento)
            : await SegregarAsync(leitura, grupos, orcamento, cancelamento);
    }

    public async Task RejeitarAsync(ResultadoLeitura leitura, OrcamentoCiclo? orcamento, CancellationToken cancelamento)
    {
        ArgumentNullException.ThrowIfNull(leitura);
        using var atividade = IniciarSpan(leitura, TiposParte.Rejeitado);
        await pipelines.ExecutarAsync(
            Dependencias.PastaLocal,
            async ct =>
            {
                // Nome original sempre; colisão com conteúdo diferente → mescla (bug nome-original-sem-trace).
                var destino = Path.Combine(politica.PastaFail, leitura.Nome);
                await estado.RegistrarParteAsync(
                    new ParteGerada(leitura.HashDoConteudo, leitura.Hash, TiposParte.Rejeitado, destino), null, ct);
                pontoFalha.Atingido(PontoFalha.AntesRemoverOriginal);
                await MescladorHistorico.MoverAsync(leitura.Caminho, destino, ct);

                MoverSidecar(leitura.Caminho, destino);
                await estado.ConcluirArquivoAsync(leitura.Hash, EstadosArquivo.Rejeitado, relogio.GetUtcNow(), ct);
            },
            orcamento,
            cancelamento);
    }

    public Task RemoverDuplicadoAsync(string caminho, OrcamentoCiclo? orcamento, CancellationToken cancelamento) =>
        pipelines.ExecutarAsync(
            Dependencias.PastaLocal,
            _ =>
            {
                File.Delete(caminho);
                ExcluirSeExistir(caminho + SufixoTraceparent);
                return ValueTask.CompletedTask;
            },
            orcamento,
            cancelamento);

    public Task LimparTemporariosAsync(OrcamentoCiclo? orcamento, CancellationToken cancelamento) =>
        pipelines.ExecutarAsync(
            Dependencias.PastaLocal,
            _ =>
            {
                // Somente temporários gerados pelo próprio serviço: nas pastas de saída e as partes
                // __pendente- na entrada. Os .tmp de cópias do Inter no Input do Condomínio não são tocados.
                foreach (var pasta in new[] { politica.PastaConcluidos, politica.PastaFail, politica.PastaSaida })
                {
                    if (pasta is not null && Directory.Exists(pasta))
                    {
                        foreach (var tmp in Directory.EnumerateFiles(pasta, "*" + EscritorAtomico.SufixoTemporario))
                        {
                            File.Delete(tmp);
                        }
                    }
                }

                if (Directory.Exists(politica.PastaEntrada))
                {
                    foreach (var tmp in Directory.EnumerateFiles(
                        politica.PastaEntrada, $"*__{NomeadorPartes.Pendente}-*{EscritorAtomico.SufixoTemporario}"))
                    {
                        File.Delete(tmp);
                    }
                }

                return ValueTask.CompletedTask;
            },
            orcamento,
            cancelamento);

    private async Task<Finalizacao> MoverIntegralAsync(ResultadoLeitura leitura, OrcamentoCiclo? orcamento, CancellationToken cancelamento)
    {
        using var atividade = IniciarSpan(leitura, TiposParte.Integral);
        await pipelines.ExecutarAsync(
            Dependencias.PastaLocal,
            async ct =>
            {
                // Nome original sempre; colisão em Processed com conteúdo diferente → mescla (bug nome-original-sem-trace).
                var destino = Path.Combine(politica.PastaConcluidos, leitura.Nome);
                string? nomeSaida = null;
                if (politica.PastaSaida is { } saida)
                {
                    // Cópia na fila Output antes do diário; numa retomada o original já pode estar em Processed
                    // (só serve de origem se tiver exatamente o conteúdo do original, isto é, se não houve mescla).
                    var origem = File.Exists(leitura.Caminho) ? leitura.Caminho : destino;
                    nomeSaida = await CopiarParaSaidaAsync(origem, leitura.Nome, leitura.HashDoConteudo, saida, ct);
                }

                await estado.RegistrarParteAsync(
                    new ParteGerada(leitura.HashDoConteudo, leitura.Hash, TiposParte.Integral, destino), nomeSaida, ct, leitura.Nome);
                pontoFalha.Atingido(PontoFalha.AposGravarPartes);
                pontoFalha.Atingido(PontoFalha.AntesRemoverOriginal);
                await MescladorHistorico.MoverAsync(leitura.Caminho, destino, ct);

                MoverSidecar(leitura.Caminho, destino);
                await estado.ConcluirArquivoAsync(leitura.Hash, EstadosArquivo.Concluido, relogio.GetUtcNow(), ct);
            },
            orcamento,
            cancelamento);
        return Finalizacao.Integral;
    }

    private async Task<Finalizacao> SegregarAsync(
        ResultadoLeitura leitura, GruposFinalizacao grupos, OrcamentoCiclo? orcamento, CancellationToken cancelamento)
    {
        var extensao = Path.GetExtension(leitura.Nome);
        var formato = leitura.Formato;
        string? destinoPrincipal = null;
        using var atividade = IniciarSpan(leitura, grupos.Pendentes.Count > 0 ? EstadosArquivo.Segregado : EstadosArquivo.Concluido);

        await pipelines.ExecutarAsync(
            Dependencias.PastaLocal,
            async ct =>
            {
                if (grupos.Sucessos.Count > 0)
                {
                    var conteudo = SerializadorPartes.SerializarRegistros(formato, grupos.Sucessos);
                    // Nome original (bug nome-original-sem-trace): em Processed e na entrega ao Condomínio.
                    destinoPrincipal = await GravarParteAsync(
                        leitura, conteudo, politica.PastaConcluidos,
                        _ => NomeadorPartes.NomeOriginal(leitura.BaseRaiz, extensao),
                        TiposParte.Sucesso, politica.PastaSaida, ct);
                }

                if (grupos.Falhas.Count > 0)
                {
                    // Feature 002 (FR-010): mesmo nome do original; colisão com conteúdo diferente → mescla.
                    var destinoFalhas = await GravarParteAsync(
                        leitura, SerializadorPartes.SerializarBrutos(formato, grupos.Falhas), politica.PastaFail,
                        _ => NomeadorPartes.NomeOriginal(leitura.BaseRaiz, extensao),
                        TiposParte.Falhas, null, ct);
                    destinoPrincipal ??= destinoFalhas;
                }

                if (grupos.Pendentes.Count > 0)
                {
                    await GravarParteAsync(
                        leitura, SerializadorPartes.SerializarRegistros(formato, grupos.Pendentes), politica.PastaEntrada,
                        h => NomeadorPartes.Parte(leitura.BaseRaiz, NomeadorPartes.Pendente, h, extensao),
                        TiposParte.Pendente, null, ct);
                }

                pontoFalha.Atingido(PontoFalha.AposGravarPartes);
                var estadoFinal = grupos.Pendentes.Count > 0 ? EstadosArquivo.Segregado : EstadosArquivo.Concluido;
                await estado.ConcluirArquivoAsync(leitura.Hash, estadoFinal, relogio.GetUtcNow(), ct);
                pontoFalha.Atingido(PontoFalha.AntesRemoverOriginal);
                MoverSidecar(leitura.Caminho, destinoPrincipal!);
                ExcluirSeExistir(leitura.Caminho);
            },
            orcamento,
            cancelamento);

        return grupos.Pendentes.Count > 0 ? Finalizacao.Segregado : Finalizacao.Concluido;
    }

    /// <summary>
    /// Grava a parte em <paramref name="pasta"/> e, com <paramref name="pastaSaida"/> (Inter, parte de sucesso), também
    /// na fila <c>Output</c>; só então registra parte + entrega no diário (mesma transação). Em <c>Processed</c> e
    /// <c>Fail</c> o nome nunca muda: colisão com conteúdo diferente é mesclada (<see cref="MescladorHistorico"/>). Partes
    /// pendentes (na entrada) e a fila <c>Output</c>, que é interna, continuam usando nome único em colisão; a entrega ao
    /// Condomínio usa sempre o nome da parte (bug nome-original-sem-trace).
    /// </summary>
    private async Task<string> GravarParteAsync(
        ResultadoLeitura leitura, byte[] conteudo, string pasta, Func<string, string> nomear, string tipo, string? pastaSaida, CancellationToken cancelamento)
    {
        var hash = HashConteudo.Calcular(conteudo);
        string destino;
        if (tipo == TiposParte.Pendente)
        {
            destino = await DestinoLivreAsync(Path.Combine(pasta, nomear(hash)), hash, cancelamento);
            await EscritorAtomico.GravarAsync(destino, conteudo, cancelamento);
        }
        else
        {
            destino = Path.Combine(pasta, nomear(hash));
            await MescladorHistorico.GravarAsync(destino, conteudo, cancelamento);
        }

        var nome = Path.GetFileName(destino);
        string? nomeSaida = null;
        if (pastaSaida is not null)
        {
            var naFila = await DestinoLivreAsync(Path.Combine(pastaSaida, nome), hash, cancelamento);
            await EscritorAtomico.GravarAsync(naFila, conteudo, cancelamento);
            nomeSaida = Path.GetFileName(naFila);
        }

        await estado.RegistrarParteAsync(new ParteGerada(hash, leitura.Hash, tipo, destino), nomeSaida, cancelamento, nomeSaida is null ? null : nome);
        return destino;
    }

    /// <summary>Copia <paramref name="origem"/> para a fila <c>Output</c> (tmp + rename; mesmo hash → no-op).</summary>
    private static async Task<string> CopiarParaSaidaAsync(string origem, string nome, string hash, string pastaSaida, CancellationToken cancelamento)
    {
        var naFila = await DestinoLivreAsync(Path.Combine(pastaSaida, nome), hash, cancelamento);
        if (!File.Exists(naFila))
        {
            // Volume limitado (Processamento:TamanhoMaximoBytes, 1 MiB por padrão), validado na leitura.
            var conteudo = await File.ReadAllBytesAsync(origem, cancelamento);
            if (!string.Equals(HashConteudo.Calcular(conteudo), hash, StringComparison.Ordinal))
            {
                // Retomada em que o original já foi mesclado em Processed: o conteúdo a entregar não está mais disponível.
                throw new IOException($"Origem de '{nome}' para a fila Output não tem mais o conteúdo original.");
            }

            await EscritorAtomico.GravarAsync(naFila, conteudo, cancelamento);
        }

        return Path.GetFileName(naFila);
    }

    /// <summary>Span <c>arquivo.mover</c> (Princípio IV: toda E/S externa rastreada).</summary>
    private System.Diagnostics.Activity? IniciarSpan(ResultadoLeitura leitura, string operacao)
    {
        var atividade = Telemetria.Instrumentacao.Iniciar("arquivo.mover", politica.Instancia.ToString());
        atividade?.SetTag(Telemetria.Instrumentacao.Atributos.Arquivo, leitura.Nome);
        atividade?.SetTag(Telemetria.Instrumentacao.Atributos.ChaveIdempotencia, leitura.Hash);
        atividade?.SetTag(Telemetria.Instrumentacao.Atributos.StatusDetalhado, operacao);
        return atividade;
    }

    /// <summary>Destino original se livre ou idêntico; senão <c>&lt;base&gt;__&lt;h8&gt;.&lt;ext&gt;</c>.</summary>
    internal static async Task<string> DestinoLivreAsync(string destino, string hash, CancellationToken cancelamento)
    {
        if (!File.Exists(destino) || await HashConteudo.CalcularArquivoAsync(destino, cancelamento) == hash)
        {
            return destino;
        }

        return Path.Combine(Path.GetDirectoryName(destino)!, NomeadorPartes.Colisao(Path.GetFileName(destino), hash));
    }

    /// <summary>Move o sidecar <c>.traceparent</c> (se houver) para junto do destino final (US5).</summary>
    private static void MoverSidecar(string origem, string destino)
    {
        var sidecar = origem + SufixoTraceparent;
        if (!File.Exists(sidecar))
        {
            return;
        }

        var destinoSidecar = destino + SufixoTraceparent;
        if (File.Exists(destinoSidecar))
        {
            File.Delete(sidecar);
            return;
        }

        File.Move(sidecar, destinoSidecar, overwrite: false);
    }

    private static void ExcluirSeExistir(string caminho)
    {
        if (File.Exists(caminho))
        {
            File.Delete(caminho);
        }
    }
}
