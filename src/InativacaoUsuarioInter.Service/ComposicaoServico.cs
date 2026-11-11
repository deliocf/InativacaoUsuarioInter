using InativacaoUsuarioInter.Service.Arquivos;
using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Entrada;
using InativacaoUsuarioInter.Service.Entrada.Modelos;
using InativacaoUsuarioInter.Service.Estado;
using InativacaoUsuarioInter.Service.Inativacao;
using InativacaoUsuarioInter.Service.Registros;
using InativacaoUsuarioInter.Service.Replicacao;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Service.Saude;
using InativacaoUsuarioInter.Service.Telemetria;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace InativacaoUsuarioInter.Service;

/// <summary>
/// Raiz de composição compartilhada pelo <c>Program</c> e pelos testes. Registra os módulos internos
/// (Princípio I: um único processo, módulos por interfaces em processo).
/// </summary>
public static class ComposicaoServico
{
    public static IServiceCollection AdicionarInativacao(
        this IServiceCollection servicos, IConfiguration configuracao, string ambiente)
    {
        ArgumentNullException.ThrowIfNull(servicos);
        ArgumentNullException.ThrowIfNull(configuracao);

        // Configuração validada na partida: inválida impede o início (FR-004, fail-fast). As options são lidas
        // uma única vez (IOptions): mudanças valem na próxima inicialização (FR-007).
        servicos.AddOptions<SettingsOptions>().Bind(configuracao.GetSection(SettingsOptions.Secao)).ValidateOnStart();
        servicos.AddOptions<SharePointFolderOptions>().Bind(configuracao.GetSection(SharePointFolderOptions.Secao)).ValidateOnStart();
        servicos.AddOptions<ConnectionStringOptions>().Bind(configuracao.GetSection(ConnectionStringOptions.Secao)).ValidateOnStart();
        servicos.AddOptions<ProcessamentoOptions>().Bind(configuracao.GetSection(ProcessamentoOptions.Secao)).ValidateOnStart();
        servicos.AddOptions<ResilienciaOptions>().Bind(configuracao.GetSection(ResilienciaOptions.Secao)).ValidateOnStart();
        servicos.AddOptions<TelemetriaOptions>().Bind(configuracao.GetSection(TelemetriaOptions.Secao));
        servicos.TryAddSingleton(configuracao);
        servicos.AddSingleton<IValidateOptions<SettingsOptions>, ValidadorSettings>();
        servicos.AddSingleton<IValidateOptions<SharePointFolderOptions>, ValidadorPastas>();
        servicos.AddSingleton<IValidateOptions<ConnectionStringOptions>, ValidadorConexao>();
        servicos.AddSingleton<IValidateOptions<ProcessamentoOptions>, ValidadorProcessamento>();
        servicos.AddSingleton<IValidateOptions<ResilienciaOptions>, ValidadorResiliencia>();

        servicos.TryAddSingleton(TimeProvider.System);
        servicos.AddSingleton(sp => ConfiguracaoServico.Criar(
            sp.GetRequiredService<IOptions<SettingsOptions>>(),
            sp.GetRequiredService<IOptions<SharePointFolderOptions>>(),
            sp.GetRequiredService<IOptions<ConnectionStringOptions>>(),
            sp.GetRequiredService<IOptions<ProcessamentoOptions>>(),
            sp.GetRequiredService<IOptions<ResilienciaOptions>>()));
        servicos.AddSingleton<ProvedorPipelines>();
        servicos.AddSingleton<IArmazenamentoEstado, ArmazenamentoEstadoSqlite>();

        // Diferença Inter/Condomínio somente por configuração (Princípio I).
        servicos.AddSingleton<IPoliticaFluxo>(sp =>
        {
            var cfg = sp.GetRequiredService<ConfiguracaoServico>();
            return cfg.Instancia == Instancia.Inter ? new PoliticaFluxoInter(cfg) : new PoliticaFluxoCondominio(cfg);
        });

        // Ponto de falha: no-op em produção; crash real do processo apenas no ambiente "Testing" (análise C2).
        if (string.Equals(ambiente, "Testing", StringComparison.OrdinalIgnoreCase))
        {
            servicos.TryAddSingleton<IPontoFalha, PontoFalhaProcesso>();
        }
        else
        {
            servicos.TryAddSingleton<IPontoFalha, PontoFalhaNulo>();
        }

        servicos.AddSingleton<EstadoDependenciasCiclo>();
        servicos.AddSingleton<LeitorArquivo>();
        servicos.AddSingleton<LeitorTraceparent>();
        servicos.AddSingleton<DetectorElegiveis>();
        servicos.AddSingleton<IRepositorioWAccess, RepositorioWAccessSql>();
        servicos.AddSingleton<IRegistroLogs, EscritorLogAppendOnly>();
        servicos.AddSingleton<IGerenciadorArquivos, GerenciadorArquivos>();
        servicos.AddSingleton<ProcessadorArquivo>();
        servicos.AddSingleton<Recuperacao>();
        servicos.AddSingleton<IServicoReplicacao, ServicoReplicacao>();
        servicos.AddSingleton<IPublicadorSaude, PublicadorSaudeArquivo>();
        servicos.AddSingleton<SondaDependencias>();
        servicos.AddSingleton<VerificadorPermissoes>();
        servicos.AddSingleton<IOrquestradorCiclo, OrquestradorCiclo>();

        return servicos;
    }

    /// <summary>
    /// OpenTelemetry: tracing, métricas e logs. O exportador OTLP só é habilitado com
    /// <c>Telemetria:OtlpEndpoint</c>; a instrumentação existe sempre (Princípio IV).
    /// </summary>
    public static IHostApplicationBuilder AdicionarTelemetria(this IHostApplicationBuilder construtor)
    {
        ArgumentNullException.ThrowIfNull(construtor);
        var endpoint = construtor.Configuration[$"{TelemetriaOptions.Secao}:{nameof(TelemetriaOptions.OtlpEndpoint)}"];
        var exportar = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri);

        var otel = construtor.Services.AddOpenTelemetry()
            .WithTracing(t =>
            {
                t.AddSource(Instrumentacao.Nome);
                if (exportar)
                {
                    t.AddOtlpExporter(o => o.Endpoint = uri!);
                }
            })
            .WithMetrics(m =>
            {
                m.AddMeter(Instrumentacao.Nome);
                if (exportar)
                {
                    m.AddOtlpExporter(o => o.Endpoint = uri!);
                }
            });

        construtor.Logging.AddOpenTelemetry(l =>
        {
            l.IncludeFormattedMessage = true;
            l.IncludeScopes = true;
            if (exportar)
            {
                l.AddOtlpExporter(o => o.Endpoint = uri!);
            }
        });

        _ = otel;
        return construtor;
    }
}
