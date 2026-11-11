using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Saude;
using InativacaoUsuarioInter.Tests.Comum;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace InativacaoUsuarioInter.Tests.Unit.Ciclo;

/// <summary>T052 — agendamento do ciclo (FR-006, CA-19, SC-007) com relógio simulado.</summary>
public sealed class CicloWorkerTests
{
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(10);

    private sealed class OrquestradorFalso(FakeTimeProvider relogio, TimeSpan duracao, int falharNaChamada = -1) : IOrquestradorCiclo
    {
        private readonly SemaphoreSlim _sinal = new(0);
        private int _emExecucao;

        public List<(DateTimeOffset Inicio, DateTimeOffset Fim)> Ciclos { get; } = [];

        public int MaximoSimultaneo { get; private set; }

        public Task<ResumoCiclo> ExecutarCicloAsync(CancellationToken cancelamento)
        {
            var simultaneos = Interlocked.Increment(ref _emExecucao);
            MaximoSimultaneo = Math.Max(MaximoSimultaneo, simultaneos);
            var inicio = relogio.GetUtcNow();
            relogio.Advance(duracao);
            Ciclos.Add((inicio, relogio.GetUtcNow()));
            Interlocked.Decrement(ref _emExecucao);
            _sinal.Release();
            if (Ciclos.Count == falharNaChamada)
            {
                throw new InvalidOperationException("falha simulada do ciclo");
            }

            return Task.FromResult(new ResumoCiclo([], 0, false));
        }

        public async Task AguardarAsync(int total)
        {
            while (Ciclos.Count < total)
            {
                Assert.True(await _sinal.WaitAsync(Espera, TestContext.Current.CancellationToken), $"Ciclo {total} não ocorreu.");
            }
        }
    }

    private sealed class SaudeNula : IPublicadorSaude
    {
        public Task PartidaAsync(CancellationToken cancelamento) => Task.CompletedTask;

        public Task InicioCicloAsync(CancellationToken cancelamento) => Task.CompletedTask;

        public Task FimCicloAsync(CancellationToken cancelamento) => Task.CompletedTask;
    }

    private static CicloWorker Criar(IOrquestradorCiclo orquestrador, FakeTimeProvider relogio, int intervalo) =>
        new(orquestrador, new SaudeNula(), ConfiguracaoTeste.Criar(Path.GetTempPath(), intervalo: intervalo), relogio, NullLogger<CicloWorker>.Instance);

    private static async Task AguardarTimerAsync(FakeTimeProvider relogio)
    {
        // Dá tempo ao laço para registrar o Task.Delay no relógio simulado.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _ = relogio;
    }

    [Fact]
    public async Task CA19_primeiro_ciclo_imediato_e_seguinte_60s_apos_o_fim_do_anterior()
    {
        var relogio = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var orquestrador = new OrquestradorFalso(relogio, TimeSpan.FromSeconds(7));
        using var worker = Criar(orquestrador, relogio, 60);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await orquestrador.AguardarAsync(1);
        await AguardarTimerAsync(relogio);

        relogio.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Single(orquestrador.Ciclos);

        relogio.Advance(TimeSpan.FromSeconds(1));
        await orquestrador.AguardarAsync(2);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(60), orquestrador.Ciclos[1].Inicio - orquestrador.Ciclos[0].Fim);
        Assert.Equal(1, orquestrador.MaximoSimultaneo);
    }

    [Fact]
    public async Task Falha_em_um_ciclo_nao_encerra_o_laco()
    {
        var relogio = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var orquestrador = new OrquestradorFalso(relogio, TimeSpan.Zero, falharNaChamada: 1);
        using var worker = Criar(orquestrador, relogio, 10);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await orquestrador.AguardarAsync(1);
        await AguardarTimerAsync(relogio);
        relogio.Advance(TimeSpan.FromSeconds(10));
        await orquestrador.AguardarAsync(2);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, orquestrador.Ciclos.Count);
    }

    [Fact]
    public async Task Cancelamento_encerra_o_laco_durante_a_espera()
    {
        var relogio = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var orquestrador = new OrquestradorFalso(relogio, TimeSpan.Zero);
        using var worker = Criar(orquestrador, relogio, 21600);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await orquestrador.AguardarAsync(1);
        var parada = worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.Same(parada, await Task.WhenAny(parada, Task.Delay(Espera, TestContext.Current.CancellationToken)));
        Assert.Single(orquestrador.Ciclos);
    }
}
