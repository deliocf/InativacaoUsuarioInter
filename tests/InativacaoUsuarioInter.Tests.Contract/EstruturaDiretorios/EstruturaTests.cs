using System.Text.RegularExpressions;
using InativacaoUsuarioInter.Service.Arquivos;
using InativacaoUsuarioInter.Tests.Comum;
using Microsoft.Extensions.DependencyInjection;

namespace InativacaoUsuarioInter.Tests.Contract.EstruturaDiretorios;

/// <summary>
/// Feature 002, T007 — estrutura de diretórios por instância (specs/002-ajustes-pastas-fluxo/contracts/estrutura-pastas.md)
/// e ausência dos nomes antigos fora da migração (SC-002).
/// </summary>
public sealed partial class EstruturaTests
{
    private static async Task<string[]> GarantirAsync(AmbienteTeste ambiente)
    {
        await using var servicos = ambiente.CriarServicos();
        await servicos.GetRequiredService<IGerenciadorArquivos>().GarantirEstruturaAsync(null, TestContext.Current.CancellationToken);
        return Directory.GetDirectories(ambiente.RaizInstancia).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task Inter_cria_Input_Output_Processed_Logs_Fail_e_nao_toca_a_raiz_do_Condominio()
    {
        using var ambiente = AmbienteTeste.Criar("Server=nao-usado");

        Assert.Equal(["Fail", "Input", "Logs", "Output", "Processed"], await GarantirAsync(ambiente));
        Assert.False(Directory.Exists(ambiente.RaizCondominio), "A pasta de entrega é criada só na entrega (FR-002a).");
    }

    [Fact]
    public async Task Condominio_cria_Input_Processed_Logs_Fail_sem_Output()
    {
        using var ambiente = AmbienteTeste.Criar("Server=nao-usado", "Condominio");

        Assert.Equal(["Fail", "Input", "Logs", "Processed"], await GarantirAsync(ambiente));
    }

    [Fact]
    public async Task Pastas_de_dados_pessoais_sao_restritas_ao_dono_em_Unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var ambiente = AmbienteTeste.Criar("Server=nao-usado");
        await GarantirAsync(ambiente);

        foreach (var pasta in new[] { "Logs", "Fail" })
        {
            Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(ambiente.Pasta(pasta)) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead));
        }
    }

    [Fact]
    public async Task Conteudo_existente_nunca_eh_apagado()
    {
        using var ambiente = AmbienteTeste.Criar("Server=nao-usado");
        Directory.CreateDirectory(ambiente.Pasta("Logs"));
        await File.WriteAllTextAsync(ambiente.Pasta("Logs", "log_inativacao.log"), "existente\r\n", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(ambiente.Pasta("Processed"));
        await File.WriteAllTextAsync(ambiente.Pasta("Processed", "a.json"), "[]", TestContext.Current.CancellationToken);

        await GarantirAsync(ambiente);

        Assert.Equal("existente\r\n", await File.ReadAllTextAsync(ambiente.Pasta("Logs", "log_inativacao.log"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(ambiente.Pasta("Processed", "a.json")));
    }

    /// <summary>
    /// SC-002: nomes antigos de pasta não aparecem no código do serviço, nos testes nem na documentação de operação,
    /// exceto na migração (FR-004). Procura literais de nome de pasta, não as palavras de negócio
    /// (<c>TipoLog.Auditoria</c>, "auditoria", "falhas definitivas").
    /// </summary>
    [Fact]
    public void Nomes_antigos_de_pasta_so_aparecem_na_migracao()
    {
        var raiz = RaizRepositorio();
        var ocorrencias = new List<string>();

        void Varrer(string pasta, string padrao, Func<string, bool> ignorarArquivo, Regex regra, Func<string, bool> ignorarLinha)
        {
            foreach (var arquivo in Directory.EnumerateFiles(pasta, padrao, SearchOption.AllDirectories))
            {
                var relativo = Path.GetRelativePath(raiz, arquivo).Replace('\\', '/');
                if (relativo.Contains("/obj/", StringComparison.Ordinal) || relativo.Contains("/bin/", StringComparison.Ordinal) || ignorarArquivo(relativo))
                {
                    continue;
                }

                var numero = 0;
                foreach (var linha in File.ReadLines(arquivo))
                {
                    numero++;
                    if (regra.IsMatch(linha) && !ignorarLinha(linha))
                    {
                        ocorrencias.Add($"{relativo}:{numero}: {linha.Trim()}");
                    }
                }
            }
        }

        // Código e testes: literal "Imput"/"Auditoria"/"Processados"/"Falhas" ou a palavra Imput.
        var codigo = LiteralCodigo();
        Varrer(
            Path.Combine(raiz, "src", "InativacaoUsuarioInter.Service"),
            "*.cs",
            r => r.EndsWith("Arquivos/NomesPastas.cs", StringComparison.Ordinal) || r.EndsWith("Arquivos/MigradorLegado.cs", StringComparison.Ordinal),
            codigo,
            // Valor persistido no diário (tipo de parte), não nome de pasta.
            l => l.Contains("const string Falhas = \"Falhas\"", StringComparison.Ordinal));
        Varrer(
            Path.Combine(raiz, "tests"),
            "*.cs",
            r => r.Contains("/Migracao/", StringComparison.Ordinal)
                || r.EndsWith("Arquivos/MesclaLogTests.cs", StringComparison.Ordinal)
                || r.EndsWith("EstruturaDiretorios/EstruturaTests.cs", StringComparison.Ordinal),
            codigo,
            _ => false);

        // Documentação de operação: nomes de pasta antigos, salvo em linhas sobre a migração/implantação.
        var docs = LiteralDocs();
        bool LinhaDeMigracao(string l) =>
            l.Contains("antig", StringComparison.OrdinalIgnoreCase)
            || l.Contains("migra", StringComparison.OrdinalIgnoreCase)
            || l.Contains("legado", StringComparison.OrdinalIgnoreCase)
            || l.Contains("antes ", StringComparison.OrdinalIgnoreCase);
        Varrer(Path.Combine(raiz, "docs"), "*.md", _ => false, docs, LinhaDeMigracao);
        foreach (var linha in File.ReadLines(Path.Combine(raiz, "README.md")).Select((l, i) => (l, i)))
        {
            if (docs.IsMatch(linha.l) && !LinhaDeMigracao(linha.l))
            {
                ocorrencias.Add($"README.md:{linha.i + 1}: {linha.l.Trim()}");
            }
        }

        Assert.True(ocorrencias.Count == 0, "Nomes antigos de pasta encontrados:\n" + string.Join('\n', ocorrencias));
    }

    private static string RaizRepositorio()
    {
        var diretorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (diretorio is not null && !File.Exists(Path.Combine(diretorio.FullName, "README.md")))
        {
            diretorio = diretorio.Parent;
        }

        return diretorio?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }

    [GeneratedRegex("\"(Imput|Auditoria|Processados|Falhas)\"|\\bImput\\b")]
    private static partial Regex LiteralCodigo();

    [GeneratedRegex(@"\bImput\b|\bProcessados/|`Processados`|\bAuditoria/|`Auditoria`|\bFalhas/|`Falhas`")]
    private static partial Regex LiteralDocs();
}
