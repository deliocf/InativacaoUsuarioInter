using InativacaoUsuarioInter.Service;

namespace InativacaoUsuarioInter.Tests.Unit.Configuracao;

/// <summary>T092 — comandos de registro do Windows Service usados pela custom action do MSI.</summary>
public sealed class InstaladorServicoTests
{
    [Theory]
    [InlineData(new[] { "--instalar" }, true)]
    [InlineData(new[] { "--desinstalar" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--environment", "Production" }, false)]
    public void Reconhece_somente_os_argumentos_de_instalacao(string[] args, bool esperado) =>
        Assert.Equal(esperado, InstaladorServico.EhComando(args));

    [Fact]
    public void Instalacao_cria_servico_automatico_atrasado_com_reinicio_em_falha()
    {
        var comandos = InstaladorServico.ComandosInstalacao(@"C:\Servicos\InativacaoUsuarioInter\InativacaoUsuarioInter.Service.exe");

        var criar = comandos[0];
        Assert.Equal(["create", "InativacaoUsuarioInter", "binPath="], criar.Take(3));
        Assert.Equal("\"C:\\Servicos\\InativacaoUsuarioInter\\InativacaoUsuarioInter.Service.exe\"", criar[3]);
        Assert.Contains("delayed-auto", criar);
        Assert.Contains(comandos, c => c[0] == "failure" && c.Contains("restart/60000/restart/60000/restart/60000"));
    }

    [Fact]
    public void Desinstalacao_para_e_remove_o_servico() =>
        Assert.Equal(["stop", "delete"], InstaladorServico.ComandosDesinstalacao().Select(c => c[0]));
}
