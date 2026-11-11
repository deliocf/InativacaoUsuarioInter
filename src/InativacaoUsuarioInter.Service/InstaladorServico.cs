using System.Diagnostics;
using System.Runtime.Versioning;

namespace InativacaoUsuarioInter.Service;

/// <summary>
/// Registro do Windows Service pelo próprio executável (T092): <c>--instalar</c> e <c>--desinstalar</c>.
/// Permite que a custom action do MSI (ou a operação) registre o serviço sem script externo.
/// Exige privilégio de administrador.
/// </summary>
internal static class InstaladorServico
{
    public const string NomeServico = "InativacaoUsuarioInter";
    public const string ArgumentoInstalar = "--instalar";
    public const string ArgumentoDesinstalar = "--desinstalar";

    public static bool EhComando(string[] args) =>
        args.Any(a => a is ArgumentoInstalar or ArgumentoDesinstalar);

    /// <summary>Comandos <c>sc.exe</c> para criar o serviço (início automático atrasado, reinício em falha).</summary>
    public static IReadOnlyList<IReadOnlyList<string>> ComandosInstalacao(string executavel) =>
    [
        ["create", NomeServico, "binPath=", $"\"{executavel}\"", "start=", "delayed-auto", "DisplayName=", "Inativação de Usuários WAccess"],
        ["description", NomeServico, "Inativa usuários no WAccess a partir de arquivos (Inter/Condomínio)."],
        ["failure", NomeServico, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000"],
    ];

    /// <summary>Comandos <c>sc.exe</c> para parar e remover o serviço.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> ComandosDesinstalacao() =>
    [
        ["stop", NomeServico],
        ["delete", NomeServico],
    ];

    [SupportedOSPlatform("windows")]
    public static int Executar(string[] args)
    {
        var instalar = args.Contains(ArgumentoInstalar);
        var executavel = Environment.ProcessPath ?? throw new InvalidOperationException("Caminho do executável indisponível.");
        var comandos = instalar ? ComandosInstalacao(executavel) : ComandosDesinstalacao();
        var codigoFinal = 0;
        foreach (var argumentos in comandos)
        {
            var inicio = new ProcessStartInfo("sc.exe") { UseShellExecute = false };
            foreach (var argumento in argumentos)
            {
                inicio.ArgumentList.Add(argumento);
            }

            using var processo = Process.Start(inicio)!;
            processo.WaitForExit();

            // Na desinstalação, "stop" falha se o serviço já estiver parado: não é erro.
            if (processo.ExitCode != 0 && (instalar || argumentos[0] == "delete"))
            {
                codigoFinal = processo.ExitCode;
            }
        }

        return codigoFinal;
    }
}
