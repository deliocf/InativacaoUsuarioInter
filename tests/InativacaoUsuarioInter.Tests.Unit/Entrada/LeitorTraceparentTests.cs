using System.Diagnostics;
using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Entrada;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Tests.Comum;
using Microsoft.Extensions.Logging;

namespace InativacaoUsuarioInter.Tests.Unit.Entrada;

/// <summary>T095 — leitura do sidecar pelo pipeline, com falha classificada e registrada (Princípio II).</summary>
public sealed class LeitorTraceparentTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "traceparent-" + Guid.NewGuid().ToString("N"));
    private readonly ColetorLogs _logs = new();
    private readonly LeitorTraceparent _leitor;

    public LeitorTraceparentTests()
    {
        Directory.CreateDirectory(_pasta);
        var configuracao = ConfiguracaoTeste.Criar(_pasta);
        configuracao.Resiliencia.Arquivos.AtrasoBaseMs = 100;
        configuracao.Resiliencia.Arquivos.AtrasoMaximoMs = 200;
        configuracao.Resiliencia.Arquivos.Circuito.MinimoChamadas = 100;
        var dependencias = new EstadoDependenciasCiclo();
        dependencias.Iniciar(TimeSpan.FromSeconds(10));
        using var fabrica = LoggerFactory.Create(l => l.AddProvider(_logs));
        _leitor = new LeitorTraceparent(new ProvedorPipelines(configuracao), dependencias, fabrica.CreateLogger<LeitorTraceparent>());
    }

    private string Arquivo(string? sidecar)
    {
        var caminho = Path.Combine(_pasta, "a.txt");
        if (sidecar is not null)
        {
            File.WriteAllText(caminho + LeitorTraceparent.Sufixo, sidecar);
        }

        return caminho;
    }

    [Fact]
    public async Task Sidecar_valido_vira_contexto_remoto()
    {
        var trace = ActivityTraceId.CreateRandom();
        var span = ActivitySpanId.CreateRandom();

        var contexto = await _leitor.LerAsync(Arquivo($"00-{trace.ToHexString()}-{span.ToHexString()}-01\r\n"), TestContext.Current.CancellationToken);

        Assert.NotNull(contexto);
        Assert.Equal(trace, contexto.Value.TraceId);
        Assert.Equal(span, contexto.Value.SpanId);
        Assert.True(contexto.Value.IsRemote);
    }

    [Fact]
    public async Task Sidecar_ausente_eh_nulo_sem_aviso()
    {
        Assert.Null(await _leitor.LerAsync(Arquivo(null), TestContext.Current.CancellationToken));
        Assert.DoesNotContain(_logs.Entradas, e => e.Nivel >= LogLevel.Warning);
    }

    [Fact]
    public async Task Sidecar_invalido_eh_nulo_e_registrado()
    {
        Assert.Null(await _leitor.LerAsync(Arquivo("nao-eh-w3c"), TestContext.Current.CancellationToken));
        Assert.Contains(_logs.Entradas, e => e.Nivel == LogLevel.Warning && e.Mensagem.Contains("formato W3C inválido", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Falha_de_leitura_eh_classificada_e_registrada_em_vez_de_engolida()
    {
        var caminho = Arquivo("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        await using var bloqueio = new FileStream(caminho + LeitorTraceparent.Sufixo, FileMode.Open, FileAccess.Read, FileShare.None);

        var contexto = await _leitor.LerAsync(caminho, TestContext.Current.CancellationToken);

        Assert.Null(contexto);
        var aviso = Assert.Single(_logs.Entradas, e => e.Nivel == LogLevel.Warning && e.Mensagem.Contains("Falha ao ler a.txt.traceparent", StringComparison.Ordinal));
        if (OperatingSystem.IsWindows())
        {
            // No Windows o compartilhamento negado tem código Win32 32 → Bloqueio; em Unix o código difere.
            Assert.Contains("(Bloqueio)", aviso.Mensagem, StringComparison.Ordinal);
        }
    }

    public void Dispose() => Directory.Delete(_pasta, recursive: true);
}
