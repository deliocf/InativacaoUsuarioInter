using System.Text.RegularExpressions;
using InativacaoUsuarioInter.Tests.Comum;

namespace InativacaoUsuarioInter.Tests.Integration.FluxoInter;

/// <summary>T033 — fluxo Inter ponta a ponta com SQL Server real e pastas reais (US1).</summary>
public sealed partial class FluxoInterTests(SqlServerFixture sql)
{
    private const string Json3 = """
        [
          { "cpf": "22222222222", "id_responsavel": "1", "nome_responsavel": "Maria Eduarda" },
          { "cpf": "33333333333", "id_responsavel": "2", "nome_responsavel": "Carlos Renato da Silva" },
          { "cpf": "44444444444", "id_responsavel": "3", "nome_responsavel": "Luana Figueiredo" }
        ]
        """;

    private const string Txt3 = "22222222222,1,Maria Eduarda\r\n33333333333,2,Carlos Renato da Silva\r\n44444444444,3,Luana Figueiredo\r\n";

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2} \| Inter \| arquivo=(?<arquivo>[^|]+) \| cpf=(?<cpf>\d{11}) \| id_responsavel=(?<id>[^|]+) \| nome_responsavel=(?<nome>[^|]+) \| status=(?<status>INATIVADO|JA_INATIVO)$")]
    private static partial Regex LinhaAuditoria();

    private static async Task SemearAsync(Cenario cenario, params string[] cpfs)
    {
        foreach (var cpf in cpfs)
        {
            await cenario.Banco.InserirUsuarioAsync(cpf, 0, cancelamento: TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData("20261111103000.json", Json3)]
    [InlineData("20261111103000.txt", Txt3)]
    public async Task CA02_CA03_tres_cpfs_existentes_sao_inativados_auditados_e_arquivo_vai_inteiro_para_Output(string nome, string conteudo)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await SemearAsync(cenario, "22222222222", "33333333333", "44444444444");
        cenario.Ambiente.Depositar(nome, conteudo);

        await cenario.CicloAsync(ct);

        foreach (var cpf in new[] { "22222222222", "33333333333", "44444444444" })
        {
            Assert.Equal([(short)1], await cenario.Banco.ObterEstadosAsync(cpf, ct));
        }

        var auditoria = cenario.Ambiente.LinhasLog("Auditoria");
        Assert.Equal(3, auditoria.Length);
        Assert.All(auditoria, l => Assert.Matches(LinhaAuditoria(), l));
        Assert.Contains(auditoria, l => l.Contains("| cpf=33333333333 | id_responsavel=2 | nome_responsavel=Carlos Renato da Silva | status=INATIVADO", StringComparison.Ordinal));
        Assert.All(auditoria, l => Assert.Contains($"arquivo={nome}", l, StringComparison.Ordinal));
        Assert.Empty(cenario.Ambiente.LinhasLog("Falhas"));
        Assert.Equal([nome], cenario.Ambiente.Arquivos("Output"));
        Assert.Equal(conteudo, await File.ReadAllTextAsync(cenario.Ambiente.Pasta("Output", nome), ct));
        Assert.Empty(cenario.Ambiente.Arquivos("Imput"));
    }

    [Fact]
    public async Task CA04_zeros_a_esquerda_preservados()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await SemearAsync(cenario, "01234567890", "1234567890");
        cenario.Ambiente.Depositar("a.txt", "012.345.678-90,1,Maria\r\n");

        await cenario.CicloAsync(ct);

        Assert.Equal([(short)1], await cenario.Banco.ObterEstadosAsync("01234567890", ct));
        Assert.Equal([(short)0], await cenario.Banco.ObterEstadosAsync("1234567890", ct));
        Assert.Contains("| cpf=01234567890 |", Assert.Single(cenario.Ambiente.LinhasLog("Auditoria")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CA05_cpf_ausente_vai_para_falhas_e_sucessos_para_parte_de_Output()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await SemearAsync(cenario, "22222222222", "33333333333");
        var outro = await cenario.Banco.InserirUsuarioAsync("55555555555", 0, cancelamento: ct);
        cenario.Ambiente.Depositar("20261111103000.json", Json3);
        var antes = await cenario.Banco.ObterSnapshotAsync(ct);

        await cenario.CicloAsync(ct);

        Assert.Equal(2, cenario.Ambiente.LinhasLog("Auditoria").Length);
        var falha = Assert.Single(cenario.Ambiente.LinhasLog("Falhas"));
        Assert.EndsWith("| Inter | arquivo=20261111103000.json | cpf=44444444444 | causa=CPF_NAO_ENCONTRADO", falha, StringComparison.Ordinal);

        var parteFalhas = Assert.Single(cenario.Ambiente.Arquivos("Falhas"), a => a.EndsWith(".json", StringComparison.Ordinal));
        Assert.Matches(@"^20261111103000__falhas-[0-9a-f]{8}\.json$", parteFalhas);
        var conteudoFalhas = await File.ReadAllTextAsync(cenario.Ambiente.Pasta("Falhas", parteFalhas), ct);
        Assert.Contains("44444444444", conteudoFalhas, StringComparison.Ordinal);
        Assert.DoesNotContain("22222222222", conteudoFalhas, StringComparison.Ordinal);

        var parteSucesso = Assert.Single(cenario.Ambiente.Arquivos("Output"));
        Assert.Matches(@"^20261111103000__sucesso-[0-9a-f]{8}\.json$", parteSucesso);
        var conteudoSucesso = await File.ReadAllTextAsync(cenario.Ambiente.Pasta("Output", parteSucesso), ct);
        Assert.Contains("22222222222", conteudoSucesso, StringComparison.Ordinal);
        Assert.Contains("33333333333", conteudoSucesso, StringComparison.Ordinal);
        Assert.DoesNotContain("44444444444", conteudoSucesso, StringComparison.Ordinal);

        Assert.Empty(cenario.Ambiente.Arquivos("Imput"));
        Assert.Equal(antes[outro], (await cenario.Banco.ObterSnapshotAsync(ct))[outro]);

        // Não é tentado novamente.
        await cenario.CicloAsync(ct);
        Assert.Single(cenario.Ambiente.LinhasLog("Falhas"));
    }

    [Fact]
    public async Task Usuario_ja_inativo_audita_JA_INATIVO_uma_unica_vez()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await cenario.Banco.InserirUsuarioAsync("22222222222", 1, cancelamento: ct);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");

        await cenario.CicloAsync(ct);
        await cenario.CicloAsync(ct);

        Assert.EndsWith("status=JA_INATIVO", Assert.Single(cenario.Ambiente.LinhasLog("Auditoria")), StringComparison.Ordinal);
        Assert.Equal(0, await cenario.Banco.ContarHistoricoAsync(ct));
    }

    [Theory]
    [InlineData("20261111103500.json", "[ { \"cpf\": ")]
    [InlineData("20261111103500.txt", "")]
    public async Task CA13_arquivo_malformado_vai_para_Falhas_sem_nenhuma_chamada_SQL(string nome, string conteudo)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await SemearAsync(cenario, "22222222222");
        cenario.Ambiente.Depositar(nome, conteudo);

        await cenario.CicloAsync(ct);

        Assert.EndsWith($"| Inter | arquivo={nome} | cpf= | causa=ARQUIVO_INVALIDO", Assert.Single(cenario.Ambiente.LinhasLog("Falhas")), StringComparison.Ordinal);
        Assert.Contains(nome, cenario.Ambiente.Arquivos("Falhas"));
        Assert.Empty(cenario.Ambiente.Arquivos("Imput"));
        Assert.Equal(0, await cenario.Banco.ContarHistoricoAsync(ct));
        Assert.Equal([(short)0], await cenario.Banco.ObterEstadosAsync("22222222222", ct));
    }

    [Fact]
    public async Task CA13_extensoes_nao_suportadas_sao_ignoradas_e_intactas()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        cenario.Ambiente.Depositar("a.csv", "22222222222,1,Maria");
        cenario.Ambiente.Depositar("b.json.tmp", "[");

        await cenario.CicloAsync(ct);

        Assert.Equal(["a.csv", "b.json.tmp"], cenario.Ambiente.Arquivos("Imput"));
        Assert.Empty(cenario.Ambiente.LinhasLog("Falhas"));
    }

    [Fact]
    public async Task Mesmo_arquivo_depositado_de_novo_nao_gera_efeito_e_sai_da_entrada()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await SemearAsync(cenario, "22222222222");
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        await cenario.CicloAsync(ct);

        cenario.Ambiente.Depositar("copia.txt", "22222222222,1,Maria\r\n");
        await cenario.CicloAsync(ct);

        Assert.Single(cenario.Ambiente.LinhasLog("Auditoria"));
        Assert.Empty(cenario.Ambiente.Arquivos("Imput"));
        Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Output"));
    }

    [Fact]
    public async Task CA15_conteudo_malicioso_eh_dado_invalido_e_nao_afeta_os_demais()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await SemearAsync(cenario, "22222222222");
        cenario.Ambiente.Depositar("a.txt", "1'; DROP TABLE CHMain;--,1,x\r\n22222222222,2,Maria \"aspas\"; DELETE\r\n");

        await cenario.CicloAsync(ct);

        Assert.EndsWith("| cpf= | causa=DADO_INVALIDO", Assert.Single(cenario.Ambiente.LinhasLog("Falhas")), StringComparison.Ordinal);
        Assert.Contains("nome_responsavel=Maria \"aspas\"; DELETE | status=INATIVADO", Assert.Single(cenario.Ambiente.LinhasLog("Auditoria")), StringComparison.Ordinal);
        Assert.Equal([(short)1], await cenario.Banco.ObterEstadosAsync("22222222222", ct));
    }

    [Fact]
    public async Task Cpf_com_dois_cadastros_inativa_ambos_com_uma_auditoria_e_aviso_sem_cpf()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, 1, ct);
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, 2, ct);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");

        await cenario.CicloAsync(ct);

        Assert.Equal([(short)1, (short)1], await cenario.Banco.ObterEstadosAsync("22222222222", ct));
        Assert.Single(cenario.Ambiente.LinhasLog("Auditoria"));
        var aviso = Assert.Single(cenario.Ambiente.Logs.Entradas, e => e.Mensagem.Contains("CpfComMultiplosCadastros", StringComparison.Ordinal));
        Assert.DoesNotContain("22222222222", aviso.Mensagem, StringComparison.Ordinal);
    }
}
