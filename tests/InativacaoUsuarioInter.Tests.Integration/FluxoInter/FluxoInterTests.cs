using System.Text.RegularExpressions;
using InativacaoUsuarioInter.Tests.Comum;

namespace InativacaoUsuarioInter.Tests.Integration.FluxoInter;

/// <summary>
/// T033 + feature 002 T021 — fluxo Inter ponta a ponta com SQL Server real e pastas reais: histórico em
/// <c>Processed</c>, entrega ao Condomínio no mesmo ciclo, fila <c>Output</c> vazia, falhas com o nome do original.
/// </summary>
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
    public async Task CA02_CA03_tres_cpfs_existentes_sao_inativados_auditados_e_arquivo_vai_inteiro_para_Processed_e_Condominio(string nome, string conteudo)
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

        var auditoria = cenario.Ambiente.LinhasLog("Logs");
        Assert.Equal(3, auditoria.Length);
        Assert.All(auditoria, l => Assert.Matches(LinhaAuditoria(), l));
        Assert.Contains(auditoria, l => l.Contains("| cpf=33333333333 | id_responsavel=2 | nome_responsavel=Carlos Renato da Silva | status=INATIVADO", StringComparison.Ordinal));
        Assert.All(auditoria, l => Assert.Contains($"arquivo={nome}", l, StringComparison.Ordinal));
        Assert.Empty(cenario.Ambiente.LinhasLog("Fail"));
        Assert.Equal([nome], cenario.Ambiente.Arquivos("Processed"));
        Assert.Equal(conteudo, await File.ReadAllTextAsync(cenario.Ambiente.Pasta("Processed", nome), ct));
        Assert.Equal(conteudo, await File.ReadAllTextAsync(cenario.Ambiente.PastaCondominio("Input", nome), ct));
        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
        Assert.Empty(cenario.Ambiente.Arquivos("Input"));
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
        Assert.Contains("| cpf=01234567890 |", Assert.Single(cenario.Ambiente.LinhasLog("Logs")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CA05_cpf_ausente_vai_para_Fail_com_o_nome_original_e_sucessos_para_Processed_e_Condominio()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await SemearAsync(cenario, "22222222222", "33333333333");
        var outro = await cenario.Banco.InserirUsuarioAsync("55555555555", 0, cancelamento: ct);
        cenario.Ambiente.Depositar("20261111103000.json", Json3);
        var antes = await cenario.Banco.ObterSnapshotAsync(ct);

        await cenario.CicloAsync(ct);

        Assert.Equal(2, cenario.Ambiente.LinhasLog("Logs").Length);
        var falha = Assert.Single(cenario.Ambiente.LinhasLog("Fail"));
        Assert.EndsWith("| Inter | arquivo=20261111103000.json | cpf=44444444444 | causa=CPF_NAO_ENCONTRADO", falha, StringComparison.Ordinal);

        // Feature 002 (FR-010): o arquivo de falhas tem o mesmo nome do original.
        var parteFalhas = Assert.Single(cenario.Ambiente.Arquivos("Fail"), a => a.EndsWith(".json", StringComparison.Ordinal));
        Assert.Equal("20261111103000.json", parteFalhas);
        var conteudoFalhas = await File.ReadAllTextAsync(cenario.Ambiente.Pasta("Fail", parteFalhas), ct);
        Assert.Contains("44444444444", conteudoFalhas, StringComparison.Ordinal);
        Assert.DoesNotContain("22222222222", conteudoFalhas, StringComparison.Ordinal);

        // Bug nome-original-sem-trace: a parte de sucesso mantém o nome do original (Processed e Condomínio).
        var parteSucesso = Assert.Single(cenario.Ambiente.Arquivos("Processed"));
        Assert.Equal("20261111103000.json", parteSucesso);
        var conteudoSucesso = await File.ReadAllTextAsync(cenario.Ambiente.Pasta("Processed", parteSucesso), ct);
        Assert.Equal(conteudoSucesso, await File.ReadAllTextAsync(cenario.Ambiente.PastaCondominio("Input", parteSucesso), ct));
        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
        Assert.Contains("22222222222", conteudoSucesso, StringComparison.Ordinal);
        Assert.Contains("33333333333", conteudoSucesso, StringComparison.Ordinal);
        Assert.DoesNotContain("44444444444", conteudoSucesso, StringComparison.Ordinal);

        Assert.Empty(cenario.Ambiente.Arquivos("Input"));
        Assert.Equal(antes[outro], (await cenario.Banco.ObterSnapshotAsync(ct))[outro]);

        // Não é tentado novamente.
        await cenario.CicloAsync(ct);
        Assert.Single(cenario.Ambiente.LinhasLog("Fail"));
    }

    [Fact]
    public async Task Usuario_ja_inativo_audita_JA_INATIVO_uma_unica_vez_e_recebe_a_marca_de_download()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await cenario.Banco.InserirUsuarioAsync("22222222222", 1, cancelamento: ct);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");

        await cenario.CicloAsync(ct);
        await cenario.CicloAsync(ct);

        Assert.EndsWith("status=JA_INATIVO", Assert.Single(cenario.Ambiente.LinhasLog("Logs")), StringComparison.Ordinal);

        // Feature 002 (FR-015): uma única alteração — só CHDownloadRequired — e nenhuma no segundo ciclo.
        Assert.Equal(1, await cenario.Banco.ContarHistoricoAsync(ct));
        Assert.Equal([((short)1, (bool?)true)], await cenario.Banco.ObterEstadosDownloadAsync("22222222222", ct));
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

        Assert.EndsWith($"| Inter | arquivo={nome} | cpf= | causa=ARQUIVO_INVALIDO", Assert.Single(cenario.Ambiente.LinhasLog("Fail")), StringComparison.Ordinal);
        Assert.Contains(nome, cenario.Ambiente.Arquivos("Fail"));
        Assert.Empty(cenario.Ambiente.Arquivos("Input"));
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

        Assert.Equal(["a.csv", "b.json.tmp"], cenario.Ambiente.Arquivos("Input"));
        Assert.Empty(cenario.Ambiente.LinhasLog("Fail"));
    }

    [Fact]
    public async Task Mesmo_arquivo_depositado_de_novo_nao_gera_efeito_e_sai_da_entrada()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await SemearAsync(cenario, "22222222222");
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        await cenario.CicloAsync(ct);

        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        await cenario.CicloAsync(ct);

        Assert.Single(cenario.Ambiente.LinhasLog("Logs"));
        Assert.Empty(cenario.Ambiente.Arquivos("Input"));
        Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Processed"));
        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
    }

    [Fact]
    public async Task Mesmo_conteudo_com_outro_nome_eh_processado_como_arquivo_novo()
    {
        // Bug ja-inativo-sem-replicacao (FR-009a): o arquivo é identificado por nome + conteúdo; a cópia com outro
        // nome é processada (JA_INATIVO) e entregue, porque no Condomínio o cadastro pode estar ativo.
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await SemearAsync(cenario, "22222222222");
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        await cenario.CicloAsync(ct);

        cenario.Ambiente.Depositar("copia.txt", "22222222222,1,Maria\r\n");
        await cenario.CicloAsync(ct);

        var linhas = cenario.Ambiente.LinhasLog("Logs");
        Assert.Equal(2, linhas.Length);
        Assert.EndsWith("arquivo=copia.txt | cpf=22222222222 | id_responsavel=1 | nome_responsavel=Maria | status=JA_INATIVO", linhas[1], StringComparison.Ordinal);
        Assert.Empty(cenario.Ambiente.Arquivos("Input"));
        Assert.Equal(["a.txt", "copia.txt"], cenario.Ambiente.Arquivos("Processed").Order(StringComparer.Ordinal));
        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
    }

    [Fact]
    public async Task US2_cenario5_tudo_falho_so_gera_o_arquivo_de_Fail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        cenario.Ambiente.Depositar("b.txt", "77777777777,1,Sem cadastro\r\n88888888888,2,Outro\r\n");

        await cenario.CicloAsync(ct);

        Assert.Equal(["b.txt"], cenario.Ambiente.Arquivos("Fail").Where(a => a.EndsWith(".txt", StringComparison.Ordinal)));
        Assert.Equal("77777777777,1,Sem cadastro\r\n88888888888,2,Outro\r\n", await File.ReadAllTextAsync(cenario.Ambiente.Pasta("Fail", "b.txt"), ct));
        Assert.Empty(cenario.Ambiente.Arquivos("Processed"));
        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
        Assert.Empty(cenario.Ambiente.Arquivos("Input", cenario.Ambiente.RaizCondominio));
        Assert.Empty(cenario.Ambiente.Arquivos("Input"));
    }

    [Fact]
    public async Task Arquivo_de_falhas_com_mesmo_nome_e_conteudo_diferente_eh_mesclado_sem_renomear_nem_perder()
    {
        // Bug nome-original-sem-trace (FR-010): nunca renomear; os registros novos são acrescentados ao arquivo existente.
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        cenario.Ambiente.Depositar("c.txt", "77777777777,1,Primeiro\r\n");
        await cenario.CicloAsync(ct);

        // O mesmo nome reenviado dias depois, com outro conteúdo.
        cenario.Ambiente.Depositar("c.txt", "88888888888,1,Segundo\r\n");
        await cenario.CicloAsync(ct);

        Assert.Equal(["c.txt"], cenario.Ambiente.Arquivos("Fail").Where(a => a.EndsWith(".txt", StringComparison.Ordinal)));
        Assert.Equal(
            "77777777777,1,Primeiro\r\n88888888888,1,Segundo\r\n",
            await File.ReadAllTextAsync(cenario.Ambiente.Pasta("Fail", "c.txt"), ct));
    }

    [Fact]
    public async Task CA15_conteudo_malicioso_eh_dado_invalido_e_nao_afeta_os_demais()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await SemearAsync(cenario, "22222222222");
        cenario.Ambiente.Depositar("a.txt", "1'; DROP TABLE CHMain;--,1,x\r\n22222222222,2,Maria \"aspas\"; DELETE\r\n");

        await cenario.CicloAsync(ct);

        Assert.EndsWith("| cpf= | causa=DADO_INVALIDO", Assert.Single(cenario.Ambiente.LinhasLog("Fail")), StringComparison.Ordinal);
        Assert.Contains("nome_responsavel=Maria \"aspas\"; DELETE | status=INATIVADO", Assert.Single(cenario.Ambiente.LinhasLog("Logs")), StringComparison.Ordinal);
        Assert.Equal([(short)1], await cenario.Banco.ObterEstadosAsync("22222222222", ct));
    }

    [Fact]
    public async Task Cpf_com_dois_cadastros_inativa_ambos_com_uma_auditoria_e_aviso_sem_cpf()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, 1, cancelamento: ct);
        await cenario.Banco.InserirUsuarioAsync("22222222222", 0, 2, cancelamento: ct);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");

        await cenario.CicloAsync(ct);

        Assert.Equal([(short)1, (short)1], await cenario.Banco.ObterEstadosAsync("22222222222", ct));
        Assert.Single(cenario.Ambiente.LinhasLog("Logs"));
        var aviso = Assert.Single(cenario.Ambiente.Logs.Entradas, e => e.Mensagem.Contains("CpfComMultiplosCadastros", StringComparison.Ordinal));
        Assert.DoesNotContain("22222222222", aviso.Mensagem, StringComparison.Ordinal);
    }
}
