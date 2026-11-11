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
                foreach (var pasta in politica.PastasObrigatorias)
                {
                    var caminho = Path.Combine(politica.Raiz, pasta);
                    if (pasta is "Auditoria" or "Falhas")
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
                var destino = await DestinoLivreAsync(Path.Combine(politica.PastaFalhas, leitura.Nome), leitura.Hash, ct);
                await estado.RegistrarParteAsync(new ParteGerada(leitura.Hash, leitura.Hash, TiposParte.Rejeitado, destino), false, ct);
                pontoFalha.Atingido(PontoFalha.AntesRemoverOriginal);
                if (File.Exists(leitura.Caminho))
                {
                    await EscritorAtomico.MoverAsync(leitura.Caminho, destino, ct);
                }

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
                // __pendente- na entrada. Os .tmp de cópias do Inter no Imput do Condomínio não são tocados.
                foreach (var pasta in new[] { politica.PastaConcluidos, politica.PastaFalhas })
                {
                    if (Directory.Exists(pasta))
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
                var destino = await DestinoLivreAsync(Path.Combine(politica.PastaConcluidos, leitura.Nome), leitura.Hash, ct);
                await estado.RegistrarParteAsync(
                    new ParteGerada(leitura.Hash, leitura.Hash, TiposParte.Integral, destino), politica.Replica, ct);
                pontoFalha.Atingido(PontoFalha.AposGravarPartes);
                pontoFalha.Atingido(PontoFalha.AntesRemoverOriginal);
                if (File.Exists(leitura.Caminho))
                {
                    await EscritorAtomico.MoverAsync(leitura.Caminho, destino, ct);
                }

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
                    destinoPrincipal = await GravarParteAsync(
                        leitura, SerializadorPartes.SerializarRegistros(formato, grupos.Sucessos),
                        politica.PastaConcluidos, NomeadorPartes.Sucesso, TiposParte.Sucesso, politica.Replica, extensao, ct);
                }

                if (grupos.Falhas.Count > 0)
                {
                    var destinoFalhas = await GravarParteAsync(
                        leitura, SerializadorPartes.SerializarBrutos(formato, grupos.Falhas),
                        politica.PastaFalhas, NomeadorPartes.Falhas, TiposParte.Falhas, false, extensao, ct);
                    destinoPrincipal ??= destinoFalhas;
                }

                if (grupos.Pendentes.Count > 0)
                {
                    await GravarParteAsync(
                        leitura, SerializadorPartes.SerializarRegistros(formato, grupos.Pendentes),
                        politica.PastaEntrada, NomeadorPartes.Pendente, TiposParte.Pendente, false, extensao, ct);
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

    private async Task<string> GravarParteAsync(
        ResultadoLeitura leitura, byte[] conteudo, string pasta, string sufixo, string tipo, bool entrega, string extensao, CancellationToken cancelamento)
    {
        var hash = HashConteudo.Calcular(conteudo);
        var destino = Path.Combine(pasta, NomeadorPartes.Parte(leitura.BaseRaiz, sufixo, hash, extensao));
        await EscritorAtomico.GravarAsync(destino, conteudo, cancelamento);
        await estado.RegistrarParteAsync(new ParteGerada(hash, leitura.Hash, tipo, destino), entrega, cancelamento);
        return destino;
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
    private static async Task<string> DestinoLivreAsync(string destino, string hash, CancellationToken cancelamento)
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
