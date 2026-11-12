using System.Diagnostics;
using InativacaoUsuarioInter.Service.Arquivos;
using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Entrada;
using InativacaoUsuarioInter.Service.Estado;
using InativacaoUsuarioInter.Service.Replicacao;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Service.Saude;
using InativacaoUsuarioInter.Service.Telemetria;
using Microsoft.Extensions.Logging;

namespace InativacaoUsuarioInter.Service.Ciclo;

/// <summary>Resumo de um ciclo (testes e telemetria).</summary>
public sealed record ResumoCiclo(IReadOnlyList<ResumoArquivo> Arquivos, int ArquivosComErro, bool HaviaArquivosNovos);

/// <summary>Executa um ciclo de varredura completo.</summary>
public interface IOrquestradorCiclo
{
    Task<ResumoCiclo> ExecutarCicloAsync(CancellationToken cancelamento);
}

/// <summary>
/// Ciclo (feature 002, plan.md "Ordem do ciclo"): saúde → pastas da própria raiz → migração das pastas antigas →
/// limpeza de temporários próprios → recuperação → elegíveis de <c>Input</c> → arquivo a arquivo →
/// entrega ao Condomínio (somente Inter, no mesmo ciclo, contracts/replicacao.md v2) → alertas e saúde.
/// Erro em um arquivo é classificado e registrado sem interromper os demais (FR-027).
/// </summary>
internal sealed partial class OrquestradorCiclo(
    ConfiguracaoServico configuracao,
    IPoliticaFluxo politica,
    IArmazenamentoEstado estado,
    IGerenciadorArquivos gerenciador,
    MigradorLegado migrador,
    DetectorElegiveis detector,
    ProcessadorArquivo processador,
    Recuperacao recuperacao,
    IServicoReplicacao replicacao,
    IPublicadorSaude saude,
    SondaDependencias sonda,
    VerificadorPermissoes permissoes,
    ProvedorPipelines pipelines,
    EstadoDependenciasCiclo dependencias,
    TimeProvider relogio,
    ILogger<OrquestradorCiclo> logger) : IOrquestradorCiclo
{
    public async Task<ResumoCiclo> ExecutarCicloAsync(CancellationToken cancelamento)
    {
        var inicio = Stopwatch.GetTimestamp();
        dependencias.Iniciar(configuracao.Intervalo);
        using var atividade = Instrumentacao.Iniciar("ciclo", configuracao.Instancia.ToString());
        await estado.InicializarAsync(cancelamento);
        await saude.InicioCicloAsync(cancelamento);

        var resumos = new List<ResumoArquivo>();
        var comErro = 0;
        try
        {
            await gerenciador.GarantirEstruturaAsync(dependencias.Orcamento, cancelamento);
            await migrador.MigrarAsync(dependencias.Orcamento, cancelamento);
            await gerenciador.LimparTemporariosAsync(dependencias.Orcamento, cancelamento);
            await recuperacao.ExecutarAsync(cancelamento);

            foreach (var caminho in await detector.ListarAsync(dependencias.Orcamento, cancelamento))
            {
                try
                {
                    resumos.Add(await processador.ProcessarAsync(caminho, cancelamento));
                }
                catch (Exception excecao) when (ClassificadorErros.Capturavel(excecao, cancelamento))
                {
                    comErro++;
                    var erro = ClassificadorErros.Classificar(excecao);
                    dependencias.MarcarIndisponivel(Dependencias.PastaLocal, erro, relogio.GetUtcNow());
                    LogErroArquivo(logger, excecao, Path.GetFileName(caminho), erro.Categoria);
                }
            }

            if (politica.Replica)
            {
                // No mesmo ciclo, depois de processados os arquivos de Input (feature 002, FR-006/FR-007).
                // Falha do destino é transitória da entrega e não afeta o que já foi processado (FR-002a).
                await replicacao.EntregarPendentesAsync(cancelamento);
            }

            // Readiness ativa (FR-040) e idade do arquivo mais antigo não processado (Princípio IV).
            await sonda.SondarAsync(cancelamento);
            dependencias.ArquivoEntradaMaisAntigoDesde = await detector.ArquivoMaisAntigoAsync(dependencias.Orcamento, cancelamento);
            dependencias.PastasAbertasDadosPessoais = await pipelines.ExecutarAsync(
                Dependencias.PastaLocal, _ => ValueTask.FromResult(permissoes.PastasAbertas().Count), dependencias.Orcamento, cancelamento);
        }
        catch (Exception excecao) when (ClassificadorErros.Capturavel(excecao, cancelamento))
        {
            var erro = ClassificadorErros.Classificar(excecao);
            dependencias.MarcarIndisponivel(Dependencias.PastaLocal, erro, relogio.GetUtcNow());
            LogErroCiclo(logger, excecao, erro.Categoria);
        }
        finally
        {
            Instrumentacao.DuracaoCicloMs.Record(Stopwatch.GetElapsedTime(inicio).TotalMilliseconds);
        }

        await saude.FimCicloAsync(cancelamento);
        atividade?.SetTag(Instrumentacao.Atributos.Resultado, comErro == 0 && dependencias.FalhasDoCiclo.Count == 0 ? "sucesso" : "transitoria");
        return new ResumoCiclo(resumos, comErro, resumos.Any(r => r.Novo));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao processar {Arquivo} ({Categoria}); será retomado no próximo ciclo.")]
    private static partial void LogErroArquivo(ILogger logger, Exception excecao, string arquivo, CategoriaErro categoria);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha no ciclo ({Categoria}); trabalho preservado para o próximo ciclo.")]
    private static partial void LogErroCiclo(ILogger logger, Exception excecao, CategoriaErro categoria);
}
