using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using InativacaoUsuarioInter.Service.Ciclo;
using InativacaoUsuarioInter.Service.Estado;
using InativacaoUsuarioInter.Tests.Comum;
using Microsoft.Extensions.DependencyInjection;

namespace InativacaoUsuarioInter.Tests.Integration.Replicacao;

/// <summary>
/// T074 + feature 002 T022 — entrega Inter → Condomínio com fila Output (FR-002a, FR-004a, FR-006–FR-011, SC-004,
/// contracts/replicacao.md v2, análise N2).
/// </summary>
public sealed class ReplicacaoTests(SqlServerFixture sql)
{
    private static string[] Entregues(AmbienteTeste ambiente) =>
        ambiente.Arquivos("Input", ambiente.RaizCondominio).Where(a => !a.EndsWith(".traceparent", StringComparison.Ordinal)).ToArray();

    private static async Task<Cenario> CriarAsync(SqlServerFixture sql, Action<IServiceCollection>? servicos = null, Func<BancoTeste, string>? conexao = null)
    {
        var cenario = await Cenario.CriarAsync(sql, ajustarServicos: servicos, conexao: conexao, cancelamento: TestContext.Current.CancellationToken);
        foreach (var cpf in new[] { "22222222222", "33333333333" })
        {
            await cenario.Banco.InserirUsuarioAsync(cpf, 0, cancelamento: TestContext.Current.CancellationToken);
        }

        return cenario;
    }

    [Fact]
    public async Task CA09_sucesso_parcial_eh_entregue_e_parte_pendente_nao_bloqueia_nem_eh_entregue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var proxy = ProxyTcpFalhas.Iniciar(sql.Host, sql.Porta);
        await using var cenario = await CriarAsync(
            sql,
            s => s.AddSingleton<IPontoFalha>(new GatilhoPonto(PontoFalha.AposRegistrarResultado, 1, proxy.Cortar)),
            b => b.StringConexaoVia(proxy.Porta));
        cenario.Ambiente.Depositar("20261111103000.txt", "22222222222,1,Maria\r\n33333333333,2,Carlos\r\n");

        await cenario.CicloAsync(ct);

        var pendente = Assert.Single(cenario.Ambiente.Arquivos("Input"));
        Assert.Contains("__pendente-", pendente, StringComparison.Ordinal);
        var sucesso = Assert.Single(cenario.Ambiente.Arquivos("Processed"));
        Assert.Equal("20261111103000.txt", sucesso);
        Assert.Equal([sucesso], Entregues(cenario.Ambiente));
        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
        Assert.Equal("22222222222,1,Maria\r\n", await File.ReadAllTextAsync(cenario.Ambiente.PastaCondominio("Input", sucesso), ct));
    }

    [Fact]
    public async Task CA10_destino_sem_permissao_preserva_origem_alerta_e_entrega_depois()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync(sql);
        var destino = cenario.Ambiente.PastaCondominio("Input");
        Directory.CreateDirectory(destino);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");

        var restaurar = NegarEscrita(destino);
        try
        {
            await cenario.CicloAsync(ct);

            Assert.Empty(cenario.Ambiente.Arquivos("Input"));
            Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Processed"));
            Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Output"));
            Assert.Empty(Entregues(cenario.Ambiente));
            Assert.Equal(1, await cenario.Obter<IArmazenamentoEstado>().ContarEntregasPendentesAsync(ct));
            using var saude = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(cenario.Ambiente.DiretorioEstado, "saude.json"), ct));
            Assert.Equal("Indisponivel", saude.RootElement.GetProperty("dependencias").GetProperty("replicacao").GetProperty("status").GetString());
            Assert.Contains(
                saude.RootElement.GetProperty("alertas").EnumerateArray(),
                a => a.GetProperty("codigo").GetString() == "PERMISSAO_NEGADA");
        }
        finally
        {
            restaurar();
        }

        // As falhas abriram o circuito de replicação: a entrega ocorre após a pausa (SC-004).
        await Task.Delay(TimeSpan.FromSeconds(5.5), ct);
        await cenario.CicloAsync(ct);

        Assert.Equal(["a.txt"], Entregues(cenario.Ambiente));
        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
        Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Processed"));
        Assert.Equal(0, await cenario.Obter<IArmazenamentoEstado>().ContarEntregasPendentesAsync(ct));
    }

    [Fact]
    public async Task SC005_cem_ciclos_e_reinicio_sem_entrega_duplicada()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync(sql);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        await cenario.CicloAsync(ct);
        var copiado = File.GetLastWriteTimeUtc(cenario.Ambiente.PastaCondominio("Input", "a.txt"));

        for (var i = 0; i < 100; i++)
        {
            await cenario.CicloAsync(ct);
        }

        await cenario.ReiniciarAsync();
        await cenario.CicloAsync(ct);

        Assert.Equal(["a.txt"], Entregues(cenario.Ambiente));
        Assert.Equal(copiado, File.GetLastWriteTimeUtc(cenario.Ambiente.PastaCondominio("Input", "a.txt")));
        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
        Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Processed"));
    }

    [Fact]
    public async Task Falhas_e_rejeitados_nunca_sao_entregues()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync(sql);
        cenario.Ambiente.Depositar("misto.txt", "22222222222,1,Maria\r\n44444444444,2,Inexistente\r\n");
        cenario.Ambiente.Depositar("invalido.json", "[ {");

        await cenario.CicloAsync(ct);

        var entregue = Assert.Single(Entregues(cenario.Ambiente));
        Assert.Equal("misto.txt", entregue);
        Assert.Contains("invalido.json", cenario.Ambiente.Arquivos("Fail"));
    }

    [Fact]
    public async Task Bug_JA_INATIVO_reenviado_em_novo_arquivo_eh_entregue_mesmo_com_parte_identica_a_ja_entregue()
    {
        // Bug ja-inativo-sem-replicacao: os mesmos CPFs, reenviados num novo arquivo, ficam JA_INATIVO no Inter e geram
        // uma parte de sucesso com os mesmos bytes da parte já entregue. No Condomínio o cadastro pode ter voltado a
        // CHState = 0, então a nova parte tem de ser entregue; só o CPF não encontrado fica de fora (FR-011).
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync(sql);
        cenario.Ambiente.Depositar("20261112111122.txt", "22222222222,3,Delio\r\n33333333333,3,Delio\r\n44444444444,3,Delio\r\n");
        await cenario.CicloAsync(ct);
        var primeira = Assert.Single(Entregues(cenario.Ambiente));
        Assert.Equal("20261112111122.txt", primeira);

        // O Condomínio consumiu a primeira entrega.
        File.Delete(cenario.Ambiente.PastaCondominio("Input", primeira));
        cenario.Ambiente.Depositar("20261112111333.txt", "22222222222,3,Delio\r\n33333333333,3,Delio\r\n55555555555,3,Delio\r\n");
        await cenario.CicloAsync(ct);
        await cenario.CicloAsync(ct);

        var segunda = Assert.Single(Entregues(cenario.Ambiente));
        Assert.Equal("20261112111333.txt", segunda);
        Assert.Equal(
            "22222222222,3,Delio\r\n33333333333,3,Delio\r\n",
            await File.ReadAllTextAsync(cenario.Ambiente.PastaCondominio("Input", segunda), ct));
        Assert.Equal(
            await File.ReadAllTextAsync(cenario.Ambiente.Pasta("Processed", primeira), ct),
            await File.ReadAllTextAsync(cenario.Ambiente.PastaCondominio("Input", segunda), ct));
        Assert.Equal(2, cenario.Ambiente.LinhasLog("Logs").Count(l => l.Contains("arquivo=20261112111333.txt", StringComparison.Ordinal) && l.EndsWith("status=JA_INATIVO", StringComparison.Ordinal)));
        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
        Assert.Equal(0, await cenario.Obter<IArmazenamentoEstado>().ContarEntregasPendentesAsync(ct));
    }

    [Fact]
    public async Task Bug_arquivo_integral_com_mesmo_conteudo_e_outro_nome_eh_processado_e_entregue_de_novo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync(sql);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        await cenario.CicloAsync(ct);

        cenario.Ambiente.Depositar("b.txt", "22222222222,1,Maria\r\n");
        await cenario.CicloAsync(ct);

        Assert.Equal(["a.txt", "b.txt"], Entregues(cenario.Ambiente).Order(StringComparer.Ordinal));
        Assert.Equal(["a.txt", "b.txt"], cenario.Ambiente.Arquivos("Processed").Where(a => !a.EndsWith(".traceparent", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.EndsWith("arquivo=b.txt | cpf=22222222222 | id_responsavel=1 | nome_responsavel=Maria | status=JA_INATIVO", cenario.Ambiente.LinhasLog("Logs")[^1], StringComparison.Ordinal);

        // O mesmo arquivo (mesmo nome e conteúdo) continua sendo no-op (Princípio III).
        cenario.Ambiente.Depositar("b.txt", "22222222222,1,Maria\r\n");
        await cenario.CicloAsync(ct);
        Assert.Equal(2, cenario.Ambiente.LinhasLog("Logs").Length);
        Assert.Equal(["a.txt", "b.txt"], Entregues(cenario.Ambiente).Order(StringComparer.Ordinal));
        Assert.Empty(cenario.Ambiente.Arquivos("Input"));
    }

    [Fact]
    public async Task Segunda_parte_do_mesmo_original_eh_mesclada_em_Processed_e_entregue_com_o_nome_original_depois_de_consumida()
    {
        // Bug nome-original-sem-trace: pendências reprocessadas geram uma 2ª parte de sucesso com o mesmo nome. Processed
        // mescla; o Condomínio recebe a 2ª parte com o nome original só depois de consumir a 1ª; nada é renomeado.
        var ct = TestContext.Current.CancellationToken;
        await using var proxy = ProxyTcpFalhas.Iniciar(sql.Host, sql.Porta);
        await using var cenario = await CriarAsync(
            sql,
            s => s.AddSingleton<IPontoFalha>(new GatilhoPonto(PontoFalha.AposRegistrarResultado, 1, proxy.Cortar)),
            b => b.StringConexaoVia(proxy.Porta));
        cenario.Ambiente.Depositar("20261111103000.txt", "22222222222,1,Maria\r\n33333333333,2,Carlos\r\n");
        await cenario.CicloAsync(ct);
        Assert.Equal(["20261111103000.txt"], Entregues(cenario.Ambiente));
        Assert.Equal("22222222222,1,Maria\r\n", await File.ReadAllTextAsync(cenario.Ambiente.PastaCondominio("Input", "20261111103000.txt"), ct));

        proxy.Restaurar();
        await Task.Delay(TimeSpan.FromSeconds(5.5), ct);
        await cenario.CicloAsync(ct);

        Assert.Equal(["20261111103000.txt"], cenario.Ambiente.Arquivos("Processed"));
        Assert.Equal(
            "22222222222,1,Maria\r\n33333333333,2,Carlos\r\n",
            await File.ReadAllTextAsync(cenario.Ambiente.Pasta("Processed", "20261111103000.txt"), ct));
        Assert.Equal(["20261111103000.txt"], cenario.Ambiente.Arquivos("Output"));
        Assert.Equal("22222222222,1,Maria\r\n", await File.ReadAllTextAsync(cenario.Ambiente.PastaCondominio("Input", "20261111103000.txt"), ct));

        // O Condomínio consumiu a 1ª parte.
        File.Delete(cenario.Ambiente.PastaCondominio("Input", "20261111103000.txt"));
        await cenario.CicloAsync(ct);

        Assert.Equal(["20261111103000.txt"], cenario.Ambiente.Arquivos("Input", cenario.Ambiente.RaizCondominio));
        Assert.Equal("33333333333,2,Carlos\r\n", await File.ReadAllTextAsync(cenario.Ambiente.PastaCondominio("Input", "20261111103000.txt"), ct));
        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
        Assert.Equal(["20261111103000.txt"], cenario.Ambiente.Arquivos("Processed"));
        Assert.Empty(cenario.Ambiente.Arquivos("Input"));
    }

    [Fact]
    public async Task Falhas_com_mesmo_nome_sao_mescladas_em_Fail_sem_renomear()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync(sql);
        cenario.Ambiente.Depositar("x.txt", "44444444444,1,Inexistente\r\n");
        await cenario.CicloAsync(ct);
        cenario.Ambiente.Depositar("x.txt", "55555555555,2,Outro\r\n");
        cenario.Ambiente.Depositar("r.json", "[ {");
        await cenario.CicloAsync(ct);
        cenario.Ambiente.Depositar("r.json", "{ x");
        await cenario.CicloAsync(ct);

        Assert.Equal(["log_inativacao.log", "r.json", "x.txt"], cenario.Ambiente.Arquivos("Fail").Order(StringComparer.Ordinal));
        Assert.Equal("44444444444,1,Inexistente\r\n55555555555,2,Outro\r\n", await File.ReadAllTextAsync(cenario.Ambiente.Pasta("Fail", "x.txt"), ct));
        Assert.Equal("[ {\r\n{ x", await File.ReadAllTextAsync(cenario.Ambiente.Pasta("Fail", "r.json"), ct));
        Assert.Empty(Entregues(cenario.Ambiente));
    }

    [Fact]
    public async Task Arquivo_so_com_cpf_nao_encontrado_nao_eh_entregue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync(sql);
        cenario.Ambiente.Depositar("inexistente.txt", "44444444444,2,Inexistente\r\n");

        await cenario.CicloAsync(ct);

        Assert.Empty(Entregues(cenario.Ambiente));
        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
        Assert.Equal(["inexistente.txt"], cenario.Ambiente.Arquivos("Fail").Where(a => a.EndsWith(".txt", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FR004a_Output_herdado_ja_entregue_vai_para_Processed_sem_nova_copia()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync(sql);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        await cenario.CicloAsync(ct);
        var copiado = File.GetLastWriteTimeUtc(cenario.Ambiente.PastaCondominio("Input", "a.txt"));

        // Versão anterior: o histórico vivia em Output (entrega já registrada) e não havia Processed.
        File.Move(cenario.Ambiente.Pasta("Processed", "a.txt"), cenario.Ambiente.Pasta("Output", "a.txt"));
        await cenario.CicloAsync(ct);

        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
        Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Processed"));
        Assert.Equal(["a.txt"], Entregues(cenario.Ambiente));
        Assert.Equal(copiado, File.GetLastWriteTimeUtc(cenario.Ambiente.PastaCondominio("Input", "a.txt")));
    }

    [Fact]
    public async Task FR008_copia_entregue_que_ficou_em_Output_eh_removida_sem_nova_entrega()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync(sql);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        await cenario.CicloAsync(ct);
        var copiado = File.GetLastWriteTimeUtc(cenario.Ambiente.PastaCondominio("Input", "a.txt"));

        // Reinício entre "Entregue" e a remoção: a cópia continua na fila e Processed tem o histórico.
        File.Copy(cenario.Ambiente.Pasta("Processed", "a.txt"), cenario.Ambiente.Pasta("Output", "a.txt"));
        await cenario.ReiniciarAsync();
        await cenario.CicloAsync(ct);

        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
        Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Processed"));
        Assert.Equal(copiado, File.GetLastWriteTimeUtc(cenario.Ambiente.PastaCondominio("Input", "a.txt")));
    }

    [Fact]
    public async Task FR004a_Output_herdado_nao_entregue_eh_entregue_e_movido_para_Processed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync(sql);
        await File.WriteAllTextAsync(cenario.Ambiente.RaizCondominio, "destino indisponível", ct);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        await cenario.CicloAsync(ct);
        Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Output"));

        // Como na versão anterior: só a cópia em Output, sem histórico em Processed.
        File.Delete(cenario.Ambiente.Pasta("Processed", "a.txt"));
        File.Delete(cenario.Ambiente.RaizCondominio);
        await Task.Delay(TimeSpan.FromSeconds(5.5), ct);
        await cenario.CicloAsync(ct);

        Assert.Equal(["a.txt"], Entregues(cenario.Ambiente));
        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
        Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Processed"));
        Assert.Equal(1, await cenario.Banco.ContarHistoricoAsync(ct));
    }

    [Fact]
    public async Task Entrega_pendente_sem_arquivo_na_fila_eh_recriada_a_partir_de_Processed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync(sql);
        await File.WriteAllTextAsync(cenario.Ambiente.RaizCondominio, "destino indisponível", ct);
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");
        await cenario.CicloAsync(ct);

        File.Delete(cenario.Ambiente.Pasta("Output", "a.txt"));
        File.Delete(cenario.Ambiente.RaizCondominio);
        await Task.Delay(TimeSpan.FromSeconds(5.5), ct);
        await cenario.CicloAsync(ct);

        Assert.Equal(["a.txt"], Entregues(cenario.Ambiente));
        Assert.Empty(cenario.Ambiente.Arquivos("Output"));
        Assert.Equal(["a.txt"], cenario.Ambiente.Arquivos("Processed"));
    }

    [Fact]
    public async Task N2_arquivo_desconhecido_em_Output_nao_eh_entregue_e_gera_alerta()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync(sql);
        Directory.CreateDirectory(cenario.Ambiente.Pasta("Output"));
        await File.WriteAllTextAsync(cenario.Ambiente.Pasta("Output", "manual.txt"), "33333333333,9,Copiado a mao\r\n", ct);

        await cenario.CicloAsync(ct);

        Assert.Empty(Entregues(cenario.Ambiente));
        Assert.Equal(["manual.txt"], cenario.Ambiente.Arquivos("Output"));
        using var saude = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(cenario.Ambiente.DiretorioEstado, "saude.json"), ct));
        var alerta = Assert.Single(saude.RootElement.GetProperty("alertas").EnumerateArray());
        Assert.Equal("ARQUIVO_OUTPUT_DESCONHECIDO", alerta.GetProperty("codigo").GetString());
        Assert.Equal(1, alerta.GetProperty("quantidade").GetInt32());
    }

    [Fact]
    public async Task T098_reconciliacao_so_calcula_hash_de_arquivos_novos_ou_alterados()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cenario = await CriarAsync(sql);
        var replicacao = (Service.Replicacao.ServicoReplicacao)cenario.Obter<Service.Replicacao.IServicoReplicacao>();
        cenario.Ambiente.Depositar("a.txt", "22222222222,1,Maria\r\n");

        await cenario.CicloAsync(ct);
        Assert.Equal(1, replicacao.HashesCalculadosNoUltimoCiclo);

        await cenario.CicloAsync(ct);
        await cenario.CicloAsync(ct);
        Assert.Equal(0, replicacao.HashesCalculadosNoUltimoCiclo);

        // Arquivo copiado à mão para Output: novo nome → hash calculado uma vez, depois vem do cache.
        await File.WriteAllTextAsync(cenario.Ambiente.Pasta("Output", "manual.txt"), "33333333333,9,Manual\r\n", ct);
        await cenario.CicloAsync(ct);
        Assert.Equal(1, replicacao.HashesCalculadosNoUltimoCiclo);
        await cenario.CicloAsync(ct);
        Assert.Equal(0, replicacao.HashesCalculadosNoUltimoCiclo);

        Assert.Equal(["a.txt"], Entregues(cenario.Ambiente));
    }

    private static Action NegarEscrita(string pasta) =>
        OperatingSystem.IsWindows() ? NegarEscritaWindows(pasta) : NegarEscritaUnix(pasta);

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
