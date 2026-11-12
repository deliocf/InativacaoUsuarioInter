using InativacaoUsuarioInter.Service.Estado;
using InativacaoUsuarioInter.Service.Inativacao;
using InativacaoUsuarioInter.Tests.Comum;
using Microsoft.Data.Sqlite;

namespace InativacaoUsuarioInter.Tests.Integration.Estado;

/// <summary>T013 — diário SQLite durável (research R-07/R-08).</summary>
public sealed class ArmazenamentoEstadoTests : IDisposable
{
    private readonly string _diretorio = Path.Combine(Path.GetTempPath(), "estado-" + Guid.NewGuid().ToString("N"));

    private static EstadoArquivo Arquivo(string hash, string raiz) =>
        new(hash, raiz, "Inter", "20261111103000.json", "20261111103000", "Json", EstadosArquivo.EmProcessamento, null, null, false, null);

    [Fact]
    public async Task Dados_sobrevivem_a_reabertura_do_arquivo()
    {
        var ct = TestContext.Current.CancellationToken;
        var primeiro = new ArmazenamentoEstadoSqlite(ConfiguracaoTeste.Criar(_diretorio));
        await primeiro.RegistrarArquivoAsync(Arquivo("h1", "h1"), [new NovoRegistro("k1", 0)], ct);

        var reaberto = new ArmazenamentoEstadoSqlite(ConfiguracaoTeste.Criar(_diretorio));
        var arquivo = await reaberto.ObterArquivoAsync("h1", ct);
        var registros = await reaberto.ObterRegistrosAsync("h1", ct);

        Assert.NotNull(arquivo);
        Assert.Equal(EstadosRegistro.Pendente, registros["k1"].Estado);
    }

    [Fact]
    public async Task Transicao_completa_grava_estado_anterior_resultado_e_linha_juntos()
    {
        var ct = TestContext.Current.CancellationToken;
        var estado = new ArmazenamentoEstadoSqlite(ConfiguracaoTeste.Criar(_diretorio));
        var agora = DateTimeOffset.UtcNow;
        await estado.RegistrarArquivoAsync(Arquivo("h1", "h1"), [new NovoRegistro("k1", 0)], ct);

        await estado.MarcarIntencaoAsync("k1", EstadoAnterior.Ativo, agora, ct);
        var emExecucao = (await estado.ObterRegistrosAsync("h1", ct))["k1"];
        await estado.RegistrarResultadoAsync("k1", ResultadoRegistro.Inativado, "linha", 1, agora, ct);
        var concluido = (await estado.ObterRegistrosAsync("h1", ct))["k1"];

        Assert.Equal(EstadosRegistro.EmExecucao, emExecucao.Estado);
        Assert.Equal(EstadoAnterior.Ativo, emExecucao.EstadoAnterior);
        Assert.Equal(EstadosRegistro.Inativado, concluido.Estado);
        Assert.Equal("linha", concluido.LinhaLog);
        Assert.False(concluido.LogGravado);
        Assert.Single(await estado.ListarLogsPendentesAsync(ct));

        await estado.MarcarLogGravadoAsync("k1", ct);
        Assert.Empty(await estado.ListarLogsPendentesAsync(ct));
    }

    [Fact]
    public async Task EmExecucao_sem_estado_anterior_eh_rejeitado_pelo_esquema()
    {
        var ct = TestContext.Current.CancellationToken;
        var estado = new ArmazenamentoEstadoSqlite(ConfiguracaoTeste.Criar(_diretorio));
        await estado.RegistrarArquivoAsync(Arquivo("h1", "h1"), [new NovoRegistro("k1", 0)], ct);

        await using var conexao = new SqliteConnection($"Data Source={Path.Combine(_diretorio, ArmazenamentoEstadoSqlite.NomeArquivo)};Pooling=False");
        await conexao.OpenAsync(ct);
        await using var comando = conexao.CreateCommand();
        comando.CommandText = "UPDATE registro SET estado = 'EmExecucao' WHERE chave_idempotencia = 'k1';";

        await Assert.ThrowsAsync<SqliteException>(() => comando.ExecuteNonQueryAsync(ct));
    }

    [Fact]
    public async Task Reaplicar_migracao_eh_idempotente()
    {
        var ct = TestContext.Current.CancellationToken;
        await new ArmazenamentoEstadoSqlite(ConfiguracaoTeste.Criar(_diretorio)).InicializarAsync(ct);
        await new ArmazenamentoEstadoSqlite(ConfiguracaoTeste.Criar(_diretorio)).InicializarAsync(ct);

        await using var conexao = new SqliteConnection($"Data Source={Path.Combine(_diretorio, ArmazenamentoEstadoSqlite.NomeArquivo)};Pooling=False");
        await conexao.OpenAsync(ct);
        await using var comando = conexao.CreateCommand();
        comando.CommandText = "SELECT COUNT(*) FROM versao_esquema;";
        Assert.Equal((long)Migracoes.Scripts.Count, (long)(await comando.ExecuteScalarAsync(ct))!);
    }

    [Fact]
    public async Task Parte_pendente_resolve_hash_raiz_do_original_e_preserva_pendencia()
    {
        var ct = TestContext.Current.CancellationToken;
        var estado = new ArmazenamentoEstadoSqlite(ConfiguracaoTeste.Criar(_diretorio));
        var primeiraFalha = DateTimeOffset.UtcNow.AddHours(-2);
        await estado.RegistrarArquivoAsync(Arquivo("orig", "orig"), [new NovoRegistro("k1", 0)], ct);
        await estado.RegistrarPendenciaAsync("k1", primeiraFalha, ct);
        await estado.RegistrarParteAsync(new ParteGerada("parte", "orig", TiposParte.Pendente, "x"), null, ct);

        var origem = await estado.ResolverHashRaizAsync("parte", ct);
        await estado.RegistrarArquivoAsync(Arquivo("parte", "orig"), [new NovoRegistro("k1", 0)], ct);
        await estado.RegistrarPendenciaAsync("k1", DateTimeOffset.UtcNow, ct);
        var registro = (await estado.ObterRegistrosAsync("orig", ct))["k1"];

        Assert.Equal(new OrigemArquivo("orig", "20261111103000"), origem);
        Assert.Equal("parte", registro.HashArquivo);
        Assert.Equal(2, registro.Tentativas);
        Assert.Equal(primeiraFalha.ToUnixTimeMilliseconds(), registro.PendenteDesde!.Value.ToUnixTimeMilliseconds());
        Assert.Null(await estado.ResolverHashRaizAsync("orig", ct));
    }

    [Fact]
    public async Task Migracao_v3_preserva_entregas_de_um_diario_v2_e_preenche_registrada_em()
    {
        // Feature 002, T004 (data-model §2): diário criado pela versão anterior (v2) com uma entrega pendente.
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_diretorio);
        var antes = DateTimeOffset.UtcNow.AddSeconds(-1);
        await using (var conexao = new SqliteConnection($"Data Source={Path.Combine(_diretorio, ArmazenamentoEstadoSqlite.NomeArquivo)};Pooling=False"))
        {
            await conexao.OpenAsync(ct);
            await using var comando = conexao.CreateCommand();
            comando.CommandText =
                "CREATE TABLE versao_esquema (versao INTEGER NOT NULL);" + Migracoes.Scripts[0] + Migracoes.Scripts[1] +
                "INSERT INTO versao_esquema VALUES (1),(2);" +
                "INSERT INTO entrega (hash, nome_output, estado) VALUES ('h-antiga', 'a.json', 'NaoEntregue');" +
                "INSERT INTO entrega (hash, nome_output, estado, nome_destino) VALUES ('h-entregue', 'b.json', 'Entregue', 'b.json');";
            await comando.ExecuteNonQueryAsync(ct);
        }

        var estado = new ArmazenamentoEstadoSqlite(ConfiguracaoTeste.Criar(_diretorio));
        await estado.InicializarAsync(ct);
        await new ArmazenamentoEstadoSqlite(ConfiguracaoTeste.Criar(_diretorio)).InicializarAsync(ct);

        var pendente = await estado.ObterEntregaAsync("h-antiga", ct);
        var entregue = await estado.ObterEntregaAsync("h-entregue", ct);
        var maisAntiga = await estado.ObterEntregaPendenteMaisAntigaAsync(ct);

        Assert.Equal(new EntregaRegistrada("h-antiga", EstadosEntrega.NaoEntregue, "a.json", null), pendente);
        Assert.Equal(new EntregaRegistrada("h-entregue", EstadosEntrega.Entregue, "b.json", "b.json"), entregue);
        Assert.Null(await estado.ObterEntregaAsync("inexistente", ct));
        Assert.NotNull(maisAntiga);
        Assert.InRange(maisAntiga.Value, antes, DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Entrega_pendente_mais_antiga_considera_so_NaoEntregue_e_usa_nome_da_fila()
    {
        var ct = TestContext.Current.CancellationToken;
        var estado = new ArmazenamentoEstadoSqlite(ConfiguracaoTeste.Criar(_diretorio));
        Assert.Null(await estado.ObterEntregaPendenteMaisAntigaAsync(ct));

        await estado.RegistrarArquivoAsync(Arquivo("orig", "orig"), [], ct);
        await estado.RegistrarParteAsync(new ParteGerada("p1", "orig", TiposParte.Integral, Path.Combine("Processed", "x.json")), "x__1.json", ct);
        var primeira = await estado.ObterEntregaPendenteMaisAntigaAsync(ct);
        await estado.RegistrarEntregaAsync("p1", "x.json", null, DateTimeOffset.UtcNow, ct);

        Assert.NotNull(primeira);
        Assert.Equal("x__1.json", (await estado.ObterEntregaAsync("p1", ct))!.NomeOutput);
        Assert.Null(await estado.ObterEntregaPendenteMaisAntigaAsync(ct));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_diretorio))
        {
            Directory.Delete(_diretorio, recursive: true);
        }
    }
}
