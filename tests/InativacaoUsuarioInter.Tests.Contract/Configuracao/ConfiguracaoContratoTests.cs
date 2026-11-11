using System.Text;
using System.Text.Json;
using InativacaoUsuarioInter.Service;
using InativacaoUsuarioInter.Service.Configuracao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace InativacaoUsuarioInter.Tests.Contract.Configuracao;

/// <summary>T053 — contrato de configuração (contracts/configuracao.md): nomes de chave e exemplo versionado.</summary>
public sealed class ConfiguracaoContratoTests
{
    private static string RaizRepositorio()
    {
        var diretorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (diretorio is not null && !File.Exists(Path.Combine(diretorio.FullName, "global.json")))
        {
            diretorio = diretorio.Parent;
        }

        return diretorio?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }

    private static string Exemplo() => File.ReadAllText(
        Path.Combine(RaizRepositorio(), "src", "InativacaoUsuarioInter.Service", "appsettings.example.json"), Encoding.UTF8);

    [Fact]
    public void Exemplo_versionado_contem_as_chaves_exatas_e_nenhuma_credencial()
    {
        using var documento = JsonDocument.Parse(Exemplo());
        var raiz = documento.RootElement;

        Assert.True(raiz.GetProperty("Settings").TryGetProperty("Instancia", out _));
        Assert.True(raiz.GetProperty("Settings").TryGetProperty("IntervalSeconds", out _));
        Assert.True(raiz.GetProperty("SharePointFolder").TryGetProperty("FolderPathInter", out _));
        Assert.True(raiz.GetProperty("SharePointFolder").TryGetProperty("FolderPathCondominio", out _));
        Assert.True(raiz.GetProperty("ConnectionString").TryGetProperty("WAccess", out var conexao));
        Assert.False(raiz.TryGetProperty("ConnectionStrings", out _));
        Assert.False(raiz.GetProperty("ConnectionString").TryGetProperty("WAccessInter", out _));
        Assert.StartsWith("<", conexao.GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", Exemplo(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exemplo_com_placeholders_preenchidos_passa_na_validacao()
    {
        var raiz = Path.Combine(Path.GetTempPath(), "cfg-contrato");
        var json = Exemplo()
            .Replace("<CAMINHO_RAIZ_INTER>", JsonEncodedText.Encode(Path.Combine(raiz, "Inter")).ToString(), StringComparison.Ordinal)
            .Replace("<CAMINHO_RAIZ_CONDOMINIO>", JsonEncodedText.Encode(Path.Combine(raiz, "Condominio")).ToString(), StringComparison.Ordinal)
            .Replace("<CONEXAO_WACCESS_DA_INSTALACAO>", "Server=srv;Database=W_ACCESS;Integrated Security=true", StringComparison.Ordinal);
        using var fluxo = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuracao = new ConfigurationBuilder().AddJsonStream(fluxo).Build();

        using var servicos = new ServiceCollection().AddLogging().AdicionarInativacao(configuracao, "Production").BuildServiceProvider();
        var resolvida = servicos.GetRequiredService<ConfiguracaoServico>();

        Assert.Equal(60, resolvida.IntervaloSegundos);
        Assert.Equal(3, resolvida.Resiliencia.WAccess.MaxTentativas);
        Assert.Equal(1000, resolvida.Processamento.MaximoRegistros);
        Assert.EndsWith(Path.Combine("InativacaoUsuarioInter", "Inter"), resolvida.DiretorioEstado, StringComparison.Ordinal);
    }

    [Fact]
    public void Secao_de_conexao_eh_singular_ConnectionString()
    {
        Assert.Equal("ConnectionString", ConnectionStringOptions.Secao);
        Assert.Equal("Settings", SettingsOptions.Secao);
        Assert.Equal("SharePointFolder", SharePointFolderOptions.Secao);
    }
}
