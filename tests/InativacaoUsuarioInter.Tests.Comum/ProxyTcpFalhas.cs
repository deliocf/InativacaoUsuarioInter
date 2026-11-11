using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace InativacaoUsuarioInter.Tests.Comum;

/// <summary>
/// Proxy TCP em processo para injetar falhas de rede reais entre o serviço e o SQL Server
/// (latência, corte de conexões e recusa). Equivalente ao Toxiproxy, sem configuração HTTP (research R-17).
/// </summary>
public sealed class ProxyTcpFalhas : IAsyncDisposable
{
    private readonly TcpListener _ouvinte;
    private readonly string _hostDestino;
    private readonly int _portaDestino;
    private readonly CancellationTokenSource _cancelamento = new();
    private readonly ConcurrentDictionary<Guid, (TcpClient Cliente, TcpClient Servidor)> _conexoes = new();
    private readonly Task _laco;
    private volatile int _latenciaMs;
    private volatile bool _cortado;

    private ProxyTcpFalhas(string hostDestino, int portaDestino)
    {
        _hostDestino = hostDestino;
        _portaDestino = portaDestino;
        _ouvinte = new TcpListener(IPAddress.Loopback, 0);
        _ouvinte.Start();
        _laco = AceitarAsync(_cancelamento.Token);
    }

    public int Porta => ((IPEndPoint)_ouvinte.LocalEndpoint).Port;

    public static ProxyTcpFalhas Iniciar(string hostDestino, int portaDestino) => new(hostDestino, portaDestino);

    /// <summary>Atraso aplicado a cada bloco trafegado, nos dois sentidos.</summary>
    public void InjetarLatencia(int milissegundos) => _latenciaMs = milissegundos;

    /// <summary>Derruba as conexões abertas e recusa as novas (banco indisponível).</summary>
    public void Cortar()
    {
        _cortado = true;
        foreach (var (id, par) in _conexoes)
        {
            Fechar(par);
            _conexoes.TryRemove(id, out _);
        }
    }

    public void Restaurar()
    {
        _latenciaMs = 0;
        _cortado = false;
    }

    public async ValueTask DisposeAsync()
    {
        await _cancelamento.CancelAsync();
        _ouvinte.Stop();
        Cortar();
        try
        {
            await _laco;
        }
        catch (OperationCanceledException)
        {
            // Encerramento esperado.
        }

        _cancelamento.Dispose();
    }

    private async Task AceitarAsync(CancellationToken cancelamento)
    {
        while (!cancelamento.IsCancellationRequested)
        {
            TcpClient cliente;
            try
            {
                cliente = await _ouvinte.AcceptTcpClientAsync(cancelamento);
            }
            catch (Exception) when (cancelamento.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            if (_cortado)
            {
                cliente.Close();
                continue;
            }

            _ = Task.Run(() => EncaminharAsync(cliente, cancelamento), cancelamento);
        }
    }

    private async Task EncaminharAsync(TcpClient cliente, CancellationToken cancelamento)
    {
        var servidor = new TcpClient();
        var id = Guid.NewGuid();
        try
        {
            await servidor.ConnectAsync(_hostDestino, _portaDestino, cancelamento);
            _conexoes[id] = (cliente, servidor);
            await Task.WhenAny(
                BombearAsync(cliente.GetStream(), servidor.GetStream(), cancelamento),
                BombearAsync(servidor.GetStream(), cliente.GetStream(), cancelamento));
        }
        catch (Exception excecao) when (excecao is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Conexão encerrada (corte injetado ou fim normal).
        }
        finally
        {
            _conexoes.TryRemove(id, out _);
            Fechar((cliente, servidor));
        }
    }

    private async Task BombearAsync(NetworkStream origem, NetworkStream destino, CancellationToken cancelamento)
    {
        var buffer = new byte[16 * 1024];
        while (!cancelamento.IsCancellationRequested && !_cortado)
        {
            var lidos = await origem.ReadAsync(buffer, cancelamento);
            if (lidos == 0)
            {
                return;
            }

            if (_latenciaMs > 0)
            {
                await Task.Delay(_latenciaMs, cancelamento);
            }

            if (_cortado)
            {
                return;
            }

            await destino.WriteAsync(buffer.AsMemory(0, lidos), cancelamento);
        }
    }

    private static void Fechar((TcpClient Cliente, TcpClient Servidor) par)
    {
        try
        {
            if (par.Cliente.Client is { } socket)
            {
                socket.LingerState = new LingerOption(true, 0);
            }

            par.Cliente.Close();
        }
        catch (Exception excecao) when (excecao is SocketException or ObjectDisposedException)
        {
            // Já fechado.
        }

        try
        {
            par.Servidor.Close();
        }
        catch (Exception excecao) when (excecao is SocketException or ObjectDisposedException)
        {
            // Já fechado.
        }
    }
}
