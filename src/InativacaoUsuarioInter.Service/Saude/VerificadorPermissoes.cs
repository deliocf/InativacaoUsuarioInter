using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Configuracao;

namespace InativacaoUsuarioInter.Service.Saude;

/// <summary>
/// FR-038: os logs de <c>Logs/</c> e <c>Fail/</c> e o diário (<c>estado.db</c>) contêm CPF e nomes. Verifica se essas
/// pastas concedem leitura a grupos amplos — Windows: Everyone, Users, Authenticated Users; Unix: "outros" —
/// e devolve rótulos (nunca caminhos) das pastas abertas, para o alerta <c>PERMISSAO_ABERTA_DADOS_PESSOAIS</c>.
/// </summary>
internal sealed class VerificadorPermissoes(IPoliticaFluxo politica, ConfiguracaoServico configuracao)
{
    private static readonly HashSet<string> GruposAmplos =
    [
        "S-1-1-0",      // Everyone
        "S-1-5-11",     // Authenticated Users
        "S-1-5-32-545", // Users
    ];

    public IReadOnlyList<string> PastasAbertas()
    {
        var abertas = new List<string>();
        foreach (var (rotulo, pasta) in new[]
        {
            ("Logs", politica.PastaLogs),
            ("Fail", politica.PastaFail),
            ("Estado", configuracao.DiretorioEstado),
        })
        {
            if (Directory.Exists(pasta) && Aberta(pasta))
            {
                abertas.Add(rotulo);
            }
        }

        return abertas;
    }

    private static bool Aberta(string pasta) =>
        OperatingSystem.IsWindows() ? AbertaWindows(pasta) : AbertaUnix(pasta);

    [SupportedOSPlatform("windows")]
    private static bool AbertaWindows(string pasta)
    {
        var regras = new DirectoryInfo(pasta).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule regra in regras)
        {
            if (regra.AccessControlType == AccessControlType.Allow
                && (regra.FileSystemRights & FileSystemRights.ReadData) != 0
                && GruposAmplos.Contains(regra.IdentityReference.Value))
            {
                return true;
            }
        }

        return false;
    }

    [UnsupportedOSPlatform("windows")]
    private static bool AbertaUnix(string pasta) =>
        (File.GetUnixFileMode(pasta) & UnixFileMode.OtherRead) != 0;
}

/// <summary>Cria diretórios de dados pessoais com acesso só do dono em Unix (0700); no Windows herda a ACL.</summary>
internal static class DiretoriosRestritos
{
    public static void Criar(string caminho)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(caminho);
            return;
        }

        CriarUnix(caminho);
    }

    [UnsupportedOSPlatform("windows")]
    private static void CriarUnix(string caminho) =>
        Directory.CreateDirectory(caminho, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}
