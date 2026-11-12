using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using InativacaoUsuarioInter.Tests.Comum;

namespace InativacaoUsuarioInter.Tests.Integration.InjecaoFalhas;

/// <summary>T060 — falhas de sistema de arquivos: log sem permissão e pasta de saída sem permissão (Princípio V).</summary>
public sealed class FalhasArquivoTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Log_de_auditoria_sem_permissao_fica_pendente_e_eh_gravado_exatamente_uma_vez_depois()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        Directory.CreateDirectory(cenario.Ambiente.Pasta("Logs"));
        var log = cenario.Ambiente.Pasta("Logs", "log_inativacao.log");
        await File.WriteAllTextAsync(log, string.Empty, ct);
        File.SetAttributes(log, FileAttributes.ReadOnly);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");

        try
        {
            await cenario.CicloAsync(ct);
            Assert.Equal([(short)1], await cenario.Banco.ObterEstadosAsync("22222222222", ct));
            Assert.Empty(cenario.Ambiente.LinhasLog("Logs"));
        }
        finally
        {
            File.SetAttributes(log, FileAttributes.Normal);
        }

        // As falhas abriram o circuito de pasta-local: a recuperação ocorre após a pausa (SC-004).
        await Task.Delay(TimeSpan.FromSeconds(5.5), ct);
        await cenario.CicloAsync(ct);
        await cenario.CicloAsync(ct);

        Assert.EndsWith("status=INATIVADO", Assert.Single(cenario.Ambiente.LinhasLog("Logs")), StringComparison.Ordinal);
        Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Processed"));
    }

    [Fact]
    public async Task Fila_Output_sem_permissao_mantem_o_original_na_entrada_sem_perda_nem_duplicacao()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, cancelamento: ct);
        var output = cenario.Ambiente.Pasta("Output");
        Directory.CreateDirectory(output);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");

        var restaurar = NegarEscrita(output);
        try
        {
            await cenario.CicloAsync(ct);
            Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Input"));
            Assert.Single(cenario.Ambiente.LinhasLog("Logs"));
        }
        finally
        {
            restaurar();
        }

        await cenario.CicloAsync(ct);

        Assert.Empty(cenario.Ambiente.Arquivos("Input"));
        Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Processed"));
        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
        Assert.Single(cenario.Ambiente.LinhasLog("Logs"));
    }

    [Fact]
    public async Task Arquivo_bloqueado_por_outro_processo_eh_adiado_sem_erro()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        var caminho = cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");

        await using (new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var resumo = await cenario.CicloAsync(ct);
            Assert.Empty(resumo.Arquivos);
            Assert.Equal(0, resumo.ArquivosComErro);
        }

        Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Input"));
    }

    private static Action NegarEscrita(string pasta)
    {
        return OperatingSystem.IsWindows() ? NegarEscritaWindows(pasta) : NegarEscritaUnix(pasta);
    }

    [UnsupportedOSPlatform("windows")]
    private static Action NegarEscritaUnix(string pasta)
    {
        var original = File.GetUnixFileMode(pasta);
        File.SetUnixFileMode(pasta, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return () => File.SetUnixFileMode(pasta, original);
    }

    [SupportedOSPlatform("windows")]
    private static Action NegarEscritaWindows(string pasta)
    {
        var diretorio = new DirectoryInfo(pasta);
        var regra = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles | FileSystemRights.AppendData | FileSystemRights.WriteData,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);
        var seguranca = diretorio.GetAccessControl();
        seguranca.AddAccessRule(regra);
        diretorio.SetAccessControl(seguranca);
        return () =>
        {
            var atual = diretorio.GetAccessControl();
            atual.RemoveAccessRule(regra);
            diretorio.SetAccessControl(atual);
        };
    }
}
