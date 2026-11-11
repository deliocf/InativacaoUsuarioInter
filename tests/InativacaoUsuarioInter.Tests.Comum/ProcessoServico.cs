using System.Diagnostics;

namespace InativacaoUsuarioInter.Tests.Comum;

/// <summary>Garante o encerramento do processo filho ao sair do escopo, mesmo com falha de asserção.</summary>
public sealed class EncerrarAoSair(Process processo) : IDisposable
{
    public void Dispose() => ProcessoServico.Encerrar(processo);
}

/// <summary>
/// Executa o serviço real (DLL compilada na mesma configuração dos testes) como processo filho, configurado
/// por variáveis de ambiente a partir do <see cref="AmbienteTeste"/>. Usado no crash real (T061) e na medição
/// de memória do serviço (T099).
/// </summary>
public static class ProcessoServico
{
    public const string VariavelPontoFalha = "INATIVACAO_PONTO_FALHA";

    public static Process Iniciar(AmbienteTeste ambiente, IReadOnlyDictionary<string, string?>? extras = null)
    {
        ArgumentNullException.ThrowIfNull(ambiente);
        var dll = LocalizarServico();
        var inicio = new ProcessStartInfo("dotnet", $"\"{dll}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        inicio.Environment["DOTNET_ENVIRONMENT"] = "Testing";
        inicio.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        foreach (var (chave, valor) in ambiente.Configuracao.Concat(extras ?? new Dictionary<string, string?>()))
        {
            inicio.Environment[chave.Replace(":", "__", StringComparison.Ordinal)] = valor;
        }

        var processo = Process.Start(inicio)!;
        processo.OutputDataReceived += (_, _) => { };
        processo.ErrorDataReceived += (_, _) => { };
        processo.BeginOutputReadLine();
        processo.BeginErrorReadLine();
        return processo;
    }

    /// <summary>Encerra o processo e seus filhos, se ainda estiver em execução (nunca deixa serviço órfão).</summary>
    public static void Encerrar(Process processo)
    {
        ArgumentNullException.ThrowIfNull(processo);
        try
        {
            if (!processo.HasExited)
            {
                processo.Kill(entireProcessTree: true);
                processo.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
            // Processo já encerrado.
        }
    }

    public static async Task<bool> AguardarSaidaAsync(Process processo, TimeSpan limite, CancellationToken cancelamento)
    {
        ArgumentNullException.ThrowIfNull(processo);
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(cancelamento);
        prazo.CancelAfter(limite);
        try
        {
            await processo.WaitForExitAsync(prazo.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            processo.Kill(entireProcessTree: true);
            return false;
        }
    }

    /// <summary>DLL do serviço compilada na mesma configuração (Debug/Release) dos testes.</summary>
    public static string LocalizarServico()
    {
        var baseTestes = AppContext.BaseDirectory;
        var configuracao = baseTestes.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            ? "Release"
            : "Debug";
        var diretorio = new DirectoryInfo(baseTestes);
        while (diretorio is not null && !File.Exists(Path.Combine(diretorio.FullName, "global.json")))
        {
            diretorio = diretorio.Parent;
        }

        var dll = Path.Combine(diretorio!.FullName, "src", "InativacaoUsuarioInter.Service", "bin", configuracao, "net10.0", "InativacaoUsuarioInter.Service.dll");
        return File.Exists(dll) ? dll : throw new FileNotFoundException($"Serviço não compilado em {dll}.", dll);
    }
}
