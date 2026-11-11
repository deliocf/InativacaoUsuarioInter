using InativacaoUsuarioInter.Service.Arquivos;
using InativacaoUsuarioInter.Tests.Comum;
using Microsoft.Extensions.DependencyInjection;

namespace InativacaoUsuarioInter.Tests.Contract.EstruturaDiretorios;

/// <summary>T030/T080 — estrutura de diretórios por instância (contracts/arquivos-entrada.md §1).</summary>
public sealed class EstruturaTests
{
    private static async Task<string[]> GarantirAsync(AmbienteTeste ambiente)
    {
        await using var servicos = ambiente.CriarServicos();
        await servicos.GetRequiredService<IGerenciadorArquivos>().GarantirEstruturaAsync(null, TestContext.Current.CancellationToken);
        return Directory.GetDirectories(ambiente.RaizInstancia).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task Inter_cria_Imput_Output_Auditoria_Falhas_sem_Processados()
    {
        using var ambiente = AmbienteTeste.Criar("Server=nao-usado");

        Assert.Equal(["Auditoria", "Falhas", "Imput", "Output"], await GarantirAsync(ambiente));
    }

    [Fact]
    public async Task Condominio_cria_Imput_Processados_Auditoria_Falhas_sem_Output()
    {
        using var ambiente = AmbienteTeste.Criar("Server=nao-usado", "Condominio");

        Assert.Equal(["Auditoria", "Falhas", "Imput", "Processados"], await GarantirAsync(ambiente));
    }

    [Fact]
    public async Task Conteudo_existente_nunca_eh_apagado()
    {
        using var ambiente = AmbienteTeste.Criar("Server=nao-usado");
        Directory.CreateDirectory(ambiente.Pasta("Auditoria"));
        await File.WriteAllTextAsync(ambiente.Pasta("Auditoria", "log_inativacao.log"), "existente\r\n", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(ambiente.Pasta("Processados"));

        await GarantirAsync(ambiente);

        Assert.Equal("existente\r\n", await File.ReadAllTextAsync(ambiente.Pasta("Auditoria", "log_inativacao.log"), TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(ambiente.Pasta("Processados")));
    }
}
