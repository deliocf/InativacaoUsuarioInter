using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using InativacaoUsuarioInter.Service;
using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Telemetria;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace InativacaoUsuarioInter.Tests.Comum;

/// <summary>
/// Ambiente de teste com diretórios temporários reais (<c>Inter/</c>, <c>Condominio/</c>, estado),
/// configuração em memória e coletores de logs e telemetria.
/// </summary>
public sealed class AmbienteTeste : IDisposable
{
    private AmbienteTeste(string conexao, string instancia)
    {
        Raiz = Path.Combine(Path.GetTempPath(), "inativacao-testes", Guid.NewGuid().ToString("N")[..12]);
        RaizInter = Path.Combine(Raiz, "Inter");
        RaizCondominio = Path.Combine(Raiz, "Condominio");
        DiretorioEstado = Path.Combine(Raiz, "estado-" + instancia);
        Directory.CreateDirectory(Raiz);
        Instancia = instancia;
        Configuracao = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Settings:Instancia"] = instancia,
            ["Settings:IntervalSeconds"] = "10",
            ["SharePointFolder:FolderPathInter"] = RaizInter,
            ["SharePointFolder:FolderPathCondominio"] = RaizCondominio,
            ["ConnectionString:WAccess"] = conexao,
            ["Processamento:DiretorioEstado"] = DiretorioEstado,
            ["Processamento:EstabilizacaoSegundos"] = "1",
        };
        foreach (var dependencia in new[] { "WAccess", "Arquivos", "Replicacao" })
        {
            Configuracao[$"Resiliencia:{dependencia}:AtrasoBaseMs"] = "100";
            Configuracao[$"Resiliencia:{dependencia}:AtrasoMaximoMs"] = "200";
            Configuracao[$"Resiliencia:{dependencia}:TimeoutConexaoSegundos"] = "5";
            Configuracao[$"Resiliencia:{dependencia}:TimeoutComandoSegundos"] = "5";
            Configuracao[$"Resiliencia:{dependencia}:TimeoutOperacaoSegundos"] = "10";
            Configuracao[$"Resiliencia:{dependencia}:Circuito:PausaSegundos"] = "5";
            Configuracao[$"Resiliencia:{dependencia}:Circuito:JanelaSegundos"] = "5";
        }
    }

    public string Raiz { get; }

    public string RaizInter { get; }

    public string RaizCondominio { get; }

    public string DiretorioEstado { get; }

    public string Instancia { get; }

    public string RaizInstancia => Instancia == "Inter" ? RaizInter : RaizCondominio;

    public Dictionary<string, string?> Configuracao { get; }

    public ColetorLogs Logs { get; } = new();

    /// <summary>Relógio simulado; quando nulo, usa o relógio do sistema.</summary>
    public FakeTimeProvider? Relogio { get; set; }

    public static AmbienteTeste Criar(string conexaoWAccess, string instancia = "Inter") => new(conexaoWAccess, instancia);

    /// <summary>Cria um relógio simulado iniciado no instante atual (para não invalidar datas de arquivo).</summary>
    public FakeTimeProvider UsarRelogioSimulado()
    {
        Relogio = new FakeTimeProvider(DateTimeOffset.UtcNow);
        return Relogio;
    }

    public IConfiguration ConstruirConfiguracao(IReadOnlyDictionary<string, string?>? sobrescrever = null)
    {
        var valores = new Dictionary<string, string?>(Configuracao, StringComparer.OrdinalIgnoreCase);
        foreach (var (chave, valor) in sobrescrever ?? new Dictionary<string, string?>())
        {
            valores[chave] = valor;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
    }

    /// <summary>Sobrescritas para subir a outra instância (Condomínio) sobre as mesmas pastas.</summary>
    public Dictionary<string, string?> ComoCondominio(string conexao) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Settings:Instancia"] = "Condominio",
        ["ConnectionString:WAccess"] = conexao,
        ["Processamento:DiretorioEstado"] = Path.Combine(Raiz, "estado-Condominio"),
    };

    public ServiceProvider CriarServicos(
        Action<IServiceCollection>? ajustar = null, string ambiente = "Testing", IReadOnlyDictionary<string, string?>? sobrescrever = null)
    {
        var servicos = new ServiceCollection();
        servicos.AddLogging(l => l.ClearProviders().SetMinimumLevel(LogLevel.Debug).AddProvider(Logs));
        if (Relogio is not null)
        {
            servicos.AddSingleton<TimeProvider>(Relogio);
        }

        servicos.AdicionarInativacao(ConstruirConfiguracao(sobrescrever), ambiente);
        ajustar?.Invoke(servicos);
        return servicos.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });
    }

    public string Pasta(params string[] partes) => Path.Combine([RaizInstancia, .. partes]);

    public string PastaInter(params string[] partes) => Path.Combine([RaizInter, .. partes]);

    public string PastaCondominio(params string[] partes) => Path.Combine([RaizCondominio, .. partes]);

    /// <summary>Deposita um arquivo em <c>Imput</c> da instância, já estável (escrita há 1 hora).</summary>
    public string Depositar(string nome, string conteudo, string? raiz = null)
    {
        var pasta = Path.Combine(raiz ?? RaizInstancia, "Imput");
        Directory.CreateDirectory(pasta);
        var caminho = Path.Combine(pasta, nome);
        File.WriteAllText(caminho, conteudo, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(caminho, DateTime.UtcNow.AddHours(-1));
        return caminho;
    }

    public string[] LinhasLog(string tipo, string? raiz = null)
    {
        var caminho = Path.Combine(raiz ?? RaizInstancia, tipo, "log_inativacao.log");
        if (!File.Exists(caminho))
        {
            return [];
        }

        // O serviço pode estar escrevendo (processo filho): ler compartilhando escrita.
        using var leitor = new StreamReader(new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8);
        var linhas = new List<string>();
        while (leitor.ReadLine() is { } linha)
        {
            linhas.Add(linha);
        }

        return linhas.ToArray();
    }

    public string[] Arquivos(string subpasta, string? raiz = null)
    {
        var pasta = Path.Combine(raiz ?? RaizInstancia, subpasta);
        return Directory.Exists(pasta)
            ? Directory.GetFiles(pasta).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray()
            : [];
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Raiz, recursive: true);
        }
        catch (IOException)
        {
            // Melhor esforço: diretório temporário.
        }
        catch (UnauthorizedAccessException)
        {
            // Melhor esforço: ACL alterada pelo teste.
        }
    }
}

/// <summary>Provedor de logs em memória (mensagem formatada + exceção) para varredura de dados sensíveis.</summary>
public sealed class ColetorLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<EntradaLog> _entradas = new();

    public IReadOnlyCollection<EntradaLog> Entradas => _entradas;

    public ILogger CreateLogger(string categoryName) => new Registrador(categoryName, _entradas);

    public void Dispose()
    {
    }

    public string TextoCompleto() =>
        string.Join('\n', _entradas.Select(e => $"{e.Nivel} {e.Categoria} {e.Mensagem} {e.Excecao}"));

    private sealed class Registrador(string categoria, ConcurrentQueue<EntradaLog> destino) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var estado = state is IEnumerable<KeyValuePair<string, object?>> pares
                ? string.Join(';', pares.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;
            destino.Enqueue(new EntradaLog(categoria, logLevel, formatter(state, exception) + " " + estado, exception?.ToString()));
        }
    }
}

public sealed record EntradaLog(string Categoria, LogLevel Nivel, string Mensagem, string? Excecao);

/// <summary>Coletor OpenTelemetry em memória (spans e métricas da fonte do serviço).</summary>
public sealed class ColetorTelemetria : IDisposable
{
    private readonly TracerProvider _rastreamento;
    private readonly MeterProvider _metricas;

    public ColetorTelemetria()
    {
        _rastreamento = Sdk.CreateTracerProviderBuilder()
            .AddSource(Instrumentacao.Nome)
            .AddInMemoryExporter(Spans)
            .Build()!;
        _metricas = Sdk.CreateMeterProviderBuilder()
            .AddMeter(Instrumentacao.Nome)
            .AddInMemoryExporter(Metricas)
            .Build()!;
    }

    public List<Activity> Spans { get; } = [];

    public List<Metric> Metricas { get; } = [];

    public void Coletar()
    {
        _rastreamento.ForceFlush();
        _metricas.ForceFlush();
    }

    /// <summary>Soma de um contador por valor de tag.</summary>
    public long Somar(string metrica, string? tag = null, string? valor = null)
    {
        Coletar();
        long total = 0;
        foreach (var m in Metricas.Where(m => m.Name == metrica))
        {
            foreach (ref readonly var ponto in m.GetMetricPoints())
            {
                if (tag is null || TemTag(ponto, tag, valor))
                {
                    total = m.MetricType switch
                    {
                        MetricType.LongSum => Math.Max(total, ponto.GetSumLong()),
                        _ => total,
                    };
                }
            }
        }

        return total;
    }

    public void Dispose()
    {
        _rastreamento.Dispose();
        _metricas.Dispose();
    }

    private static bool TemTag(in MetricPoint ponto, string tag, string? valor)
    {
        foreach (var t in ponto.Tags)
        {
            if (t.Key == tag && (valor is null || Equals(t.Value?.ToString(), valor)))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Constrói <see cref="ConfiguracaoServico"/> diretamente, para testes de unidade e de estado.</summary>
public static class ConfiguracaoTeste
{
    public static ConfiguracaoServico Criar(string diretorioEstado, string? raiz = null, int intervalo = 10) => new()
    {
        Instancia = Service.Entrada.Modelos.Instancia.Inter,
        IntervaloSegundos = intervalo,
        RaizInter = raiz ?? diretorioEstado,
        RaizCondominio = raiz ?? diretorioEstado,
        ConexaoWAccess = "Server=nao-usado",
        DiretorioEstado = diretorioEstado,
        Processamento = new ProcessamentoOptions(),
        Resiliencia = new ResilienciaOptions(),
    };
}
