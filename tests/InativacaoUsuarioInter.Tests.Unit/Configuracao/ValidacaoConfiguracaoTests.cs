using InativacaoUsuarioInter.Service;
using InativacaoUsuarioInter.Service.Configuracao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InativacaoUsuarioInter.Tests.Unit.Configuracao;

/// <summary>T051 — validação fail-fast da configuração (FR-004, FR-005, CA-16–CA-18, SC-006).</summary>
public sealed class ValidacaoConfiguracaoTests
{
    private const string Senha = "SegredoTeste123";
    private static readonly string Raiz = Path.Combine(Path.GetTempPath(), "cfg-teste");

    private static Dictionary<string, string?> Valida() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Settings:Instancia"] = "Inter",
        ["Settings:IntervalSeconds"] = "60",
        ["SharePointFolder:FolderPathInter"] = Path.Combine(Raiz, "Inter"),
        ["SharePointFolder:FolderPathCondominio"] = Path.Combine(Raiz, "Condominio"),
        ["ConnectionString:WAccess"] = $"Server=srv;Database=W_ACCESS;User ID=waccess;Password={Senha};",
        ["Processamento:DiretorioEstado"] = Path.Combine(Raiz, "estado"),
    };

    private static string? Validar(Dictionary<string, string?> valores)
    {
        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
        var servicos = new ServiceCollection().AddLogging().AdicionarInativacao(configuracao, "Production").BuildServiceProvider();
        try
        {
            _ = servicos.GetRequiredService<ConfiguracaoServico>();
            return null;
        }
        catch (OptionsValidationException excecao)
        {
            return string.Join(" | ", excecao.Failures);
        }
    }

    [Theory]
    [InlineData("10")]
    [InlineData("21600")]
    [InlineData("60")]
    public void CA16_CA17_intervalos_nos_limites_sao_aceitos(string intervalo)
    {
        var valores = Valida();
        valores["Settings:IntervalSeconds"] = intervalo;

        Assert.Null(Validar(valores));
    }

    [Theory]
    [InlineData("9")]
    [InlineData("21601")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("10.5")]
    [InlineData("-10")]
    public void CA18_intervalo_invalido_impede_a_partida_citando_chave_e_faixa(string? intervalo)
    {
        var valores = Valida();
        valores["Settings:IntervalSeconds"] = intervalo;

        var erro = Validar(valores);

        Assert.NotNull(erro);
        Assert.Contains("Settings:IntervalSeconds", erro, StringComparison.Ordinal);
        Assert.Contains("10–21600", erro, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Matriz")]
    public void Instancia_invalida_eh_rejeitada(string? instancia)
    {
        var valores = Valida();
        valores["Settings:Instancia"] = instancia;

        Assert.Contains("Settings:Instancia", Validar(valores), StringComparison.Ordinal);
    }

    [Fact]
    public void Instancia_nao_diferencia_maiusculas()
    {
        var valores = Valida();
        valores["Settings:Instancia"] = "condominio";

        Assert.Null(Validar(valores));
    }

    [Fact]
    public void Inter_exige_FolderPathInter()
    {
        var valores = Valida();
        valores.Remove("SharePointFolder:FolderPathInter");

        Assert.Contains("SharePointFolder:FolderPathInter", Validar(valores), StringComparison.Ordinal);
    }

    [Fact]
    public void Condominio_nao_exige_FolderPathInter_mas_exige_FolderPathCondominio()
    {
        var valores = Valida();
        valores["Settings:Instancia"] = "Condominio";
        valores.Remove("SharePointFolder:FolderPathInter");
        Assert.Null(Validar(valores));

        valores.Remove("SharePointFolder:FolderPathCondominio");
        Assert.Contains("SharePointFolder:FolderPathCondominio", Validar(valores), StringComparison.Ordinal);
    }

    [Fact]
    public void Caminho_relativo_eh_rejeitado()
    {
        var valores = Valida();
        valores["SharePointFolder:FolderPathCondominio"] = "Files\\Condominio";

        Assert.Contains("caminho absoluto", Validar(valores), StringComparison.Ordinal);
    }

    [Fact]
    public void Conexao_ausente_eh_rejeitada()
    {
        var valores = Valida();
        valores.Remove("ConnectionString:WAccess");

        Assert.Contains("ConnectionString:WAccess", Validar(valores), StringComparison.Ordinal);
    }

    [Fact]
    public void Chave_legada_WAccessInter_pede_renomeacao_sem_mostrar_o_valor()
    {
        var valores = Valida();
        valores.Remove("ConnectionString:WAccess");
        valores["ConnectionString:WAccessInter"] = $"Server=srv;Password={Senha};";

        var erro = Validar(valores);

        Assert.Contains("Renomeie ConnectionString:WAccessInter para ConnectionString:WAccess", erro, StringComparison.Ordinal);
        Assert.DoesNotContain(Senha, erro, StringComparison.Ordinal);
    }

    [Fact]
    public void Soma_das_esperas_de_retentativa_precisa_caber_no_intervalo()
    {
        var valores = Valida();
        valores["Settings:IntervalSeconds"] = "10";
        valores["Resiliencia:WAccess:MaxTentativas"] = "5";
        valores["Resiliencia:WAccess:AtrasoMaximoMs"] = "4000";

        var erro = Validar(valores);

        Assert.Contains("Resiliencia:WAccess:", erro, StringComparison.Ordinal);
        Assert.Contains("excede Settings:IntervalSeconds", erro, StringComparison.Ordinal);
        Assert.Equal(7000, ValidadorResiliencia.SomaEsperasMs(new PoliticaDependenciaOptions()));
    }

    [Theory]
    [InlineData("Processamento:MaximoRegistros", "1001")]
    [InlineData("Processamento:EstabilizacaoSegundos", "0")]
    [InlineData("Processamento:AlertaPendenciaHoras", "721")]
    [InlineData("Processamento:TimeoutDiarioMs", "999")]
    [InlineData("Resiliencia:Arquivos:MaxTentativas", "11")]
    [InlineData("Resiliencia:Replicacao:Circuito:PausaSegundos", "4")]
    public void Chaves_tecnicas_fora_da_faixa_sao_rejeitadas(string chave, string valor)
    {
        var valores = Valida();
        valores[chave] = valor;

        Assert.Contains(chave, Validar(valores), StringComparison.Ordinal);
    }

    [Fact]
    public void Nenhuma_mensagem_expoe_a_string_de_conexao()
    {
        var valores = Valida();
        valores["Settings:Instancia"] = "Matriz";
        valores["Settings:IntervalSeconds"] = "1";
        valores["SharePointFolder:FolderPathCondominio"] = "rel";

        var erro = Validar(valores);

        Assert.NotNull(erro);
        Assert.DoesNotContain(Senha, erro, StringComparison.Ordinal);
        Assert.DoesNotContain("Server=srv", erro, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_com_configuracao_invalida_nao_inicia()
    {
        var valores = Valida();
        valores["Settings:IntervalSeconds"] = "9";
        var construtor = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production" });
        construtor.Configuration.AddInMemoryCollection(valores);
        construtor.Logging.ClearProviders();
        construtor.Services.AdicionarInativacao(construtor.Configuration, "Production");
        using var host = construtor.Build();

        var excecao = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Settings:IntervalSeconds", string.Join(" ", excecao.Failures), StringComparison.Ordinal);
    }
}
