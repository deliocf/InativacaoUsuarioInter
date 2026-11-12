using System.Globalization;
using System.Text.Json;
using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Estado;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Service.Telemetria;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;

namespace InativacaoUsuarioInter.Service.Saude;

/// <summary>Sinal de saúde (FR-040, FR-042, contracts/saude.md).</summary>
public interface IPublicadorSaude
{
    Task PartidaAsync(CancellationToken cancelamento);

    Task InicioCicloAsync(CancellationToken cancelamento);

    Task FimCicloAsync(CancellationToken cancelamento);
}

/// <summary>Códigos de alerta de <c>saude.json</c>.</summary>
internal static class CodigosAlerta
{
    public const string PendenciaAcimaLimite = "PENDENCIA_ACIMA_LIMITE";
    public const string PermissaoNegada = "PERMISSAO_NEGADA";
    public const string ConfiguracaoWAccess = "CONFIGURACAO_WACCESS";
    public const string ArquivoOutputDesconhecido = "ARQUIVO_OUTPUT_DESCONHECIDO";

    /// <summary>Feature 002 (FR-007a): entrega ao Condomínio parada na fila <c>Output</c> além do limite.</summary>
    public const string EntregaPendenteAcimaLimite = "ENTREGA_PENDENTE_ACIMA_LIMITE";
    public const string PermissaoAbertaDadosPessoais = "PERMISSAO_ABERTA_DADOS_PESSOAIS";
}

/// <summary>
/// Grava <c>&lt;DiretorioEstado&gt;/saude.json</c> de forma atômica na partida e no início e fim de cada ciclo.
/// Sem endpoint de rede (research R-13). Não contém CPF, nome, connection string nem caminho de pasta.
/// </summary>
internal sealed partial class PublicadorSaudeArquivo(
    ConfiguracaoServico configuracao,
    IPoliticaFluxo politica,
    IArmazenamentoEstado estado,
    EstadoDependenciasCiclo dependencias,
    ProvedorPipelines pipelines,
    TimeProvider relogio,
    ILogger<PublicadorSaudeArquivo> logger) : IPublicadorSaude
{
    public const string NomeArquivo = "saude.json";

    private static readonly JsonSerializerOptions Opcoes = new() { WriteIndented = true };

    private readonly Dictionary<string, DateTimeOffset> _alertasDesde = new(StringComparer.Ordinal);
    private readonly Lock _trava = new();
    private DateTimeOffset _iniciadoEm;
    private DateTimeOffset? _ultimoInicio;
    private DateTimeOffset? _ultimoFim;
    private List<object> _alertas = [];

    public string Caminho => Path.Combine(configuracao.DiretorioEstado, NomeArquivo);

    public async Task PartidaAsync(CancellationToken cancelamento)
    {
        _iniciadoEm = relogio.GetLocalNow();
        await GravarAsync(cancelamento);
    }

    public async Task InicioCicloAsync(CancellationToken cancelamento)
    {
        if (_iniciadoEm == default)
        {
            _iniciadoEm = relogio.GetLocalNow();
        }

        _ultimoInicio = relogio.GetLocalNow();
        await GravarAsync(cancelamento);
    }

    public async Task FimCicloAsync(CancellationToken cancelamento)
    {
        _ultimoFim = relogio.GetLocalNow();
        await AvaliarAlertasAsync(cancelamento);
        await GravarAsync(cancelamento);
    }

    private async Task AvaliarAlertasAsync(CancellationToken cancelamento)
    {
        var agora = relogio.GetLocalNow();
        var ativos = new Dictionary<string, int>(StringComparer.Ordinal);

        var limite = relogio.GetUtcNow().AddHours(-configuracao.Processamento.AlertaPendenciaHoras);
        var acimaDoLimite = await estado.ContarPendenciasAnterioresAsync(limite, cancelamento);
        if (acimaDoLimite > 0)
        {
            ativos[CodigosAlerta.PendenciaAcimaLimite] = acimaDoLimite;
        }

        var permissoes = dependencias.FalhasDoCiclo.Values.Count(f => f.Categoria == CategoriaErro.Permissao);
        if (permissoes > 0)
        {
            ativos[CodigosAlerta.PermissaoNegada] = permissoes;
        }

        if (dependencias.FalhasDoCiclo.TryGetValue(Dependencias.WAccess, out var falhaWAccess)
            && falhaWAccess.Categoria == CategoriaErro.Configuracao)
        {
            ativos[CodigosAlerta.ConfiguracaoWAccess] = 1;
        }

        if (politica.Replica
            && await estado.ObterEntregaPendenteMaisAntigaAsync(cancelamento) is { } entregaMaisAntiga
            && entregaMaisAntiga < limite)
        {
            ativos[CodigosAlerta.EntregaPendenteAcimaLimite] = await estado.ContarEntregasPendentesAsync(cancelamento);
        }

        if (politica.Replica && dependencias.ArquivosOutputDesconhecidos > 0)
        {
            ativos[CodigosAlerta.ArquivoOutputDesconhecido] = dependencias.ArquivosOutputDesconhecidos;
        }

        if (dependencias.PastasAbertasDadosPessoais > 0)
        {
            ativos[CodigosAlerta.PermissaoAbertaDadosPessoais] = dependencias.PastasAbertasDadosPessoais;
        }

        foreach (var codigo in _alertasDesde.Keys.Except(ativos.Keys).ToList())
        {
            _alertasDesde.Remove(codigo);
            LogAlertaEncerrado(logger, codigo);
        }

        foreach (var (codigo, quantidade) in ativos)
        {
            if (_alertasDesde.TryAdd(codigo, agora))
            {
                LogAlerta(logger, codigo, quantidade);
            }
        }

        lock (_trava)
        {
            _alertas = ativos
                .Select(a => (object)new { codigo = a.Key, desde = Formatar(_alertasDesde[a.Key]), quantidade = a.Value })
                .ToList();
        }
    }

    private async Task GravarAsync(CancellationToken cancelamento)
    {
        var backlog = await estado.ContarBacklogAsync(cancelamento);
        var maisAntiga = await estado.ObterPendenciaMaisAntigaAsync(cancelamento);
        var entregas = politica.Replica ? await estado.ContarEntregasPendentesAsync(cancelamento) : 0;
        Interlocked.Exchange(ref EstadoMetricas.BacklogPendente, backlog);
        EstadoMetricas.IdadePendenciaMaisAntigaSegundos = maisAntiga is { } m ? (relogio.GetUtcNow() - m).TotalSeconds : 0;
        var arquivoMaisAntigo = dependencias.ArquivoEntradaMaisAntigoDesde;
        EstadoMetricas.IdadeArquivoEntradaMaisAntigoSegundos =
            arquivoMaisAntigo is { } a ? Math.Max(0, (relogio.GetUtcNow() - a).TotalSeconds) : 0;

        var dependenciasSaude = new Dictionary<string, DependenciaSaude>
        {
            ["waccess"] = Dependencia(Dependencias.WAccess, aplicavel: true),
            ["pastaLocal"] = Dependencia(Dependencias.PastaLocal, aplicavel: true),
            ["replicacao"] = Dependencia(Dependencias.Replicacao, aplicavel: politica.Replica),
        };
        List<object> alertas;
        lock (_trava)
        {
            alertas = _alertas;
        }

        var indisponivel = dependenciasSaude.Values.Any(d => d.Status == "Indisponivel");
        var documento = new
        {
            versao = 1,
            instancia = configuracao.Instancia.ToString(),
            status = alertas.Count > 0 ? "Alerta" : indisponivel ? "Degradado" : "Saudavel",
            iniciadoEm = Formatar(_iniciadoEm),
            ultimoCicloInicio = _ultimoInicio is { } i ? Formatar(i) : null,
            ultimoCicloFim = _ultimoFim is { } f ? Formatar(f) : null,
            proximoCicloPrevisto = _ultimoFim is { } fim ? Formatar(fim + configuracao.Intervalo) : null,
            intervaloSegundos = configuracao.IntervaloSegundos,
            dependencias = dependenciasSaude,
            backlog = new
            {
                registrosPendentes = backlog,
                pendenciaMaisAntigaDesde = maisAntiga is { } antiga ? Formatar(antiga.ToOffset(relogio.GetLocalNow().Offset)) : null,
                entregasPendentes = entregas,
                arquivoEntradaMaisAntigoDesde = arquivoMaisAntigo is { } antigoArquivo
                    ? Formatar(antigoArquivo.ToOffset(relogio.GetLocalNow().Offset))
                    : null,
            },
            alertas,
        };

        try
        {
            Directory.CreateDirectory(configuracao.DiretorioEstado);
            var temporario = Caminho + ".tmp";
            await File.WriteAllTextAsync(temporario, JsonSerializer.Serialize(documento, Opcoes), cancelamento);
            File.Move(temporario, Caminho, overwrite: true);
        }
        catch (Exception excecao) when (excecao is IOException or UnauthorizedAccessException)
        {
            LogFalhaGravacao(logger, ClassificadorErros.Classificar(excecao).Categoria);
        }
    }

    private DependenciaSaude Dependencia(string nome, bool aplicavel)
    {
        if (!aplicavel)
        {
            return new DependenciaSaude("NaoAplicavel", "Fechado", null);
        }

        var circuito = pipelines.EstadoCircuito(nome) switch
        {
            CircuitState.Closed => "Fechado",
            CircuitState.HalfOpen => "MeioAberto",
            _ => "Aberto",
        };
        var falha = dependencias.UltimaFalha(nome);
        var status = dependencias.Indisponivel(nome) || circuito == "Aberto" ? "Indisponivel" : "Disponivel";
        return new DependenciaSaude(
            status,
            circuito,
            falha is null ? null : new FalhaSaude(Formatar(falha.Em.ToOffset(relogio.GetLocalNow().Offset)), falha.Categoria.ToString()));
    }

    private sealed record DependenciaSaude(
        [property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status,
        [property: System.Text.Json.Serialization.JsonPropertyName("circuito")] string Circuito,
        [property: System.Text.Json.Serialization.JsonPropertyName("ultimaFalha")] FalhaSaude? UltimaFalha);

    private sealed record FalhaSaude(
        [property: System.Text.Json.Serialization.JsonPropertyName("em")] string Em,
        [property: System.Text.Json.Serialization.JsonPropertyName("categoria")] string Categoria);

    private static string Formatar(DateTimeOffset data) =>
        data.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alerta {Codigo} ativo (quantidade {Quantidade}).")]
    private static partial void LogAlerta(ILogger logger, string codigo, int quantidade);

    [LoggerMessage(Level = LogLevel.Information, Message = "Alerta {Codigo} encerrado.")]
    private static partial void LogAlertaEncerrado(ILogger logger, string codigo);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao gravar saude.json ({Categoria}).")]
    private static partial void LogFalhaGravacao(ILogger logger, CategoriaErro categoria);
}
