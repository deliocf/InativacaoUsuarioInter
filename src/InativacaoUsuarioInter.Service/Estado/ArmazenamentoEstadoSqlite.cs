using System.Globalization;
using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Inativacao;
using InativacaoUsuarioInter.Service.Saude;
using Microsoft.Data.Sqlite;

namespace InativacaoUsuarioInter.Service.Estado;

/// <summary>
/// Diário em SQLite local (<c>&lt;DiretorioEstado&gt;/estado.db</c>), WAL + <c>synchronous=FULL</c> (research R-07).
/// Uma conexão por operação, sem pool, para não reter o arquivo entre operações.
/// </summary>
internal sealed class ArmazenamentoEstadoSqlite : IArmazenamentoEstado
{
    public const string NomeArquivo = "estado.db";

    private readonly string _stringConexao;
    private readonly string _diretorio;
    private readonly SemaphoreSlim _inicializacao = new(1, 1);
    private readonly string _pragmas;
    private volatile bool _inicializado;

    public ArmazenamentoEstadoSqlite(ConfiguracaoServico configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);
        _diretorio = configuracao.DiretorioEstado;
        _pragmas = FormattableString.Invariant(
            $"PRAGMA synchronous=FULL; PRAGMA busy_timeout={configuracao.Processamento.TimeoutDiarioMs};");
        _stringConexao = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_diretorio, NomeArquivo),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
    }

    public async Task InicializarAsync(CancellationToken cancelamento)
    {
        if (_inicializado)
        {
            return;
        }

        await _inicializacao.WaitAsync(cancelamento);
        try
        {
            if (_inicializado)
            {
                return;
            }

            DiretoriosRestritos.Criar(_diretorio);
            await using var conexao = await AbrirSemInicializarAsync(cancelamento);
            await ExecutarAsync(conexao, "PRAGMA journal_mode=WAL;", cancelamento);
            await ExecutarAsync(conexao, "CREATE TABLE IF NOT EXISTS versao_esquema (versao INTEGER NOT NULL);", cancelamento);
            var versaoAtual = Convert.ToInt32(
                await EscalarAsync(conexao, "SELECT COALESCE(MAX(versao), 0) FROM versao_esquema;", cancelamento),
                CultureInfo.InvariantCulture);

            for (var versao = versaoAtual + 1; versao <= Migracoes.Scripts.Count; versao++)
            {
                await using var transacao = (SqliteTransaction)await conexao.BeginTransactionAsync(cancelamento);
                await ExecutarAsync(conexao, Migracoes.Scripts[versao - 1], cancelamento, transacao);
                await ExecutarAsync(conexao, $"INSERT INTO versao_esquema (versao) VALUES ({versao});", cancelamento, transacao);
                await transacao.CommitAsync(cancelamento);
            }

            _inicializado = true;
        }
        finally
        {
            _inicializacao.Release();
        }
    }

    public async Task<EstadoArquivo?> ObterArquivoAsync(string hash, CancellationToken cancelamento)
    {
        await using var conexao = await AbrirAsync(cancelamento);
        await using var comando = Comando(
            conexao,
            "SELECT hash, hash_raiz, instancia, nome_original, base_raiz, formato, estado, causa_rejeicao, linha_log, log_gravado, traceparent FROM arquivo WHERE hash = $h;",
            ("$h", hash));
        await using var leitor = await comando.ExecuteReaderAsync(cancelamento);
        if (!await leitor.ReadAsync(cancelamento))
        {
            return null;
        }

        return new EstadoArquivo(
            leitor.GetString(0), leitor.GetString(1), leitor.GetString(2), leitor.GetString(3), leitor.GetString(4),
            leitor.GetString(5), leitor.GetString(6), TextoOuNulo(leitor, 7), TextoOuNulo(leitor, 8),
            leitor.GetInt32(9) == 1, TextoOuNulo(leitor, 10));
    }

    public async Task<ParteGerada?> ObterParteAsync(string hash, CancellationToken cancelamento)
    {
        await using var conexao = await AbrirAsync(cancelamento);
        await using var comando = Comando(
            conexao, "SELECT hash, hash_origem, tipo, caminho_final FROM parte_gerada WHERE hash = $h;", ("$h", hash));
        await using var leitor = await comando.ExecuteReaderAsync(cancelamento);
        return await leitor.ReadAsync(cancelamento)
            ? new ParteGerada(leitor.GetString(0), leitor.GetString(1), leitor.GetString(2), leitor.GetString(3))
            : null;
    }

    public async Task<OrigemArquivo?> ResolverHashRaizAsync(string hash, CancellationToken cancelamento)
    {
        await using var conexao = await AbrirAsync(cancelamento);
        await using var comando = Comando(
            conexao,
            """
            SELECT a.hash_raiz, a.base_raiz
            FROM parte_gerada p JOIN arquivo a ON a.hash = p.hash_origem
            WHERE p.hash = $h AND p.tipo = 'Pendente';
            """,
            ("$h", hash));
        await using var leitor = await comando.ExecuteReaderAsync(cancelamento);
        return await leitor.ReadAsync(cancelamento)
            ? new OrigemArquivo(leitor.GetString(0), leitor.GetString(1))
            : null;
    }

    public async Task RegistrarArquivoAsync(EstadoArquivo arquivo, IReadOnlyCollection<NovoRegistro> registros, CancellationToken cancelamento)
    {
        ArgumentNullException.ThrowIfNull(arquivo);
        ArgumentNullException.ThrowIfNull(registros);
        var agora = Agora();

        await using var conexao = await AbrirAsync(cancelamento);
        await using var transacao = (SqliteTransaction)await conexao.BeginTransactionAsync(cancelamento);
        await InserirArquivoAsync(conexao, transacao, arquivo, agora, cancelamento);

        foreach (var registro in registros)
        {
            await using var inserir = Comando(
                conexao,
                """
                INSERT INTO registro (chave_idempotencia, hash_arquivo, hash_raiz, indice, estado, atualizado_em)
                VALUES ($c, $h, $r, $i, 'Pendente', $t)
                ON CONFLICT(chave_idempotencia) DO UPDATE SET hash_arquivo = excluded.hash_arquivo;
                """,
                ("$c", registro.Chave), ("$h", arquivo.Hash), ("$r", arquivo.HashRaiz), ("$i", registro.Indice), ("$t", agora));
            inserir.Transaction = transacao;
            await inserir.ExecuteNonQueryAsync(cancelamento);
        }

        await transacao.CommitAsync(cancelamento);
    }

    public async Task<IReadOnlyDictionary<string, EstadoRegistro>> ObterRegistrosAsync(string hashRaiz, CancellationToken cancelamento)
    {
        await using var conexao = await AbrirAsync(cancelamento);
        await using var comando = Comando(
            conexao,
            """
            SELECT chave_idempotencia, hash_arquivo, hash_raiz, indice, estado, estado_anterior, tentativas,
                   pendente_desde, linha_log, log_gravado
            FROM registro WHERE hash_raiz = $r;
            """,
            ("$r", hashRaiz));
        await using var leitor = await comando.ExecuteReaderAsync(cancelamento);
        var resultado = new Dictionary<string, EstadoRegistro>(StringComparer.Ordinal);
        while (await leitor.ReadAsync(cancelamento))
        {
            var anterior = TextoOuNulo(leitor, 5);
            var pendente = TextoOuNulo(leitor, 7);
            resultado[leitor.GetString(0)] = new EstadoRegistro(
                leitor.GetString(0), leitor.GetString(1), leitor.GetString(2), leitor.GetInt32(3), leitor.GetString(4),
                anterior is null ? null : Enum.Parse<EstadoAnterior>(anterior),
                leitor.GetInt32(6),
                pendente is null ? null : LerData(pendente),
                TextoOuNulo(leitor, 8),
                leitor.GetInt32(9) == 1);
        }

        return resultado;
    }

    public Task MarcarIntencaoAsync(string chave, EstadoAnterior estadoAnterior, DateTimeOffset agora, CancellationToken cancelamento) =>
        ExecutarComandoAsync(
            "UPDATE registro SET estado = 'EmExecucao', estado_anterior = $a, atualizado_em = $t WHERE chave_idempotencia = $c;",
            cancelamento,
            ("$a", estadoAnterior.ToString()), ("$t", Formatar(agora)), ("$c", chave));

    public Task RegistrarResultadoAsync(
        string chave, ResultadoRegistro resultado, string linhaLog, int? linhasAfetadas, DateTimeOffset agora, CancellationToken cancelamento) =>
        ExecutarComandoAsync(
            """
            UPDATE registro SET estado = $e, linha_log = $l, linhas_afetadas = $n, log_gravado = 0, atualizado_em = $t
            WHERE chave_idempotencia = $c;
            """,
            cancelamento,
            ("$e", EstadosRegistro.De(resultado)), ("$l", linhaLog), ("$n", linhasAfetadas), ("$t", Formatar(agora)), ("$c", chave));

    public Task MarcarLogGravadoAsync(string chave, CancellationToken cancelamento) =>
        ExecutarComandoAsync("UPDATE registro SET log_gravado = 1 WHERE chave_idempotencia = $c;", cancelamento, ("$c", chave));

    public Task RegistrarPendenciaAsync(string chave, DateTimeOffset agora, CancellationToken cancelamento) =>
        ExecutarComandoAsync(
            """
            UPDATE registro SET tentativas = tentativas + 1, pendente_desde = COALESCE(pendente_desde, $t), atualizado_em = $t
            WHERE chave_idempotencia = $c;
            """,
            cancelamento,
            ("$t", Formatar(agora)), ("$c", chave));

    public async Task RegistrarRejeicaoAsync(EstadoArquivo arquivo, string causa, string linhaLog, CancellationToken cancelamento)
    {
        ArgumentNullException.ThrowIfNull(arquivo);
        var agora = Agora();
        await using var conexao = await AbrirAsync(cancelamento);
        await using var transacao = (SqliteTransaction)await conexao.BeginTransactionAsync(cancelamento);
        await InserirArquivoAsync(conexao, transacao, arquivo, agora, cancelamento);
        await using var atualizar = Comando(
            conexao,
            "UPDATE arquivo SET causa_rejeicao = $c, linha_log = COALESCE(linha_log, $l) WHERE hash = $h;",
            ("$c", causa), ("$l", linhaLog), ("$h", arquivo.Hash));
        atualizar.Transaction = transacao;
        await atualizar.ExecuteNonQueryAsync(cancelamento);
        await transacao.CommitAsync(cancelamento);
    }

    public Task MarcarLogArquivoGravadoAsync(string hash, CancellationToken cancelamento) =>
        ExecutarComandoAsync("UPDATE arquivo SET log_gravado = 1 WHERE hash = $h;", cancelamento, ("$h", hash));

    public async Task RegistrarParteAsync(ParteGerada parte, bool registrarEntrega, CancellationToken cancelamento)
    {
        ArgumentNullException.ThrowIfNull(parte);
        var agora = Agora();
        await using var conexao = await AbrirAsync(cancelamento);
        await using var transacao = (SqliteTransaction)await conexao.BeginTransactionAsync(cancelamento);
        await using (var inserir = Comando(
            conexao,
            "INSERT OR IGNORE INTO parte_gerada (hash, hash_origem, tipo, caminho_final, gravado_em) VALUES ($h, $o, $t, $c, $g);",
            ("$h", parte.Hash), ("$o", parte.HashOrigem), ("$t", parte.Tipo), ("$c", parte.CaminhoFinal), ("$g", agora)))
        {
            inserir.Transaction = transacao;
            await inserir.ExecuteNonQueryAsync(cancelamento);
        }

        if (registrarEntrega)
        {
            await using var entrega = Comando(
                conexao,
                "INSERT OR IGNORE INTO entrega (hash, nome_output, estado) VALUES ($h, $n, 'NaoEntregue');",
                ("$h", parte.Hash), ("$n", Path.GetFileName(parte.CaminhoFinal)));
            entrega.Transaction = transacao;
            await entrega.ExecuteNonQueryAsync(cancelamento);
        }

        await transacao.CommitAsync(cancelamento);
    }

    public Task ConcluirArquivoAsync(string hash, string estado, DateTimeOffset agora, CancellationToken cancelamento) =>
        ExecutarComandoAsync(
            "UPDATE arquivo SET estado = $e, concluido_em = $t WHERE hash = $h;",
            cancelamento,
            ("$e", estado), ("$t", Formatar(agora)), ("$h", hash));

    public Task RegistrarTraceparentAsync(string hash, string traceparent, CancellationToken cancelamento) =>
        ExecutarComandoAsync(
            "UPDATE arquivo SET traceparent = $p WHERE hash = $h;", cancelamento, ("$p", traceparent), ("$h", hash));

    public async Task<IReadOnlyList<LogPendente>> ListarLogsPendentesAsync(CancellationToken cancelamento)
    {
        var pendentes = new List<LogPendente>();
        await using var conexao = await AbrirAsync(cancelamento);
        await using (var registros = Comando(
            conexao,
            "SELECT chave_idempotencia, estado, linha_log FROM registro WHERE log_gravado = 0 AND linha_log IS NOT NULL ORDER BY atualizado_em;"))
        await using (var leitor = await registros.ExecuteReaderAsync(cancelamento))
        {
            while (await leitor.ReadAsync(cancelamento))
            {
                var tipo = leitor.GetString(1) is EstadosRegistro.Inativado or EstadosRegistro.JaInativo
                    ? TipoLog.Auditoria
                    : TipoLog.Falhas;
                pendentes.Add(new LogPendente(leitor.GetString(0), false, tipo, leitor.GetString(2)));
            }
        }

        await using (var arquivos = Comando(
            conexao, "SELECT hash, linha_log FROM arquivo WHERE log_gravado = 0 AND linha_log IS NOT NULL;"))
        await using (var leitor = await arquivos.ExecuteReaderAsync(cancelamento))
        {
            while (await leitor.ReadAsync(cancelamento))
            {
                pendentes.Add(new LogPendente(leitor.GetString(0), true, TipoLog.Falhas, leitor.GetString(1)));
            }
        }

        return pendentes;
    }

    public async Task<DateTimeOffset?> ObterPendenciaMaisAntigaAsync(CancellationToken cancelamento)
    {
        await using var conexao = await AbrirAsync(cancelamento);
        var valor = await EscalarAsync(
            conexao,
            "SELECT MIN(pendente_desde) FROM registro WHERE estado IN ('Pendente','EmExecucao') AND pendente_desde IS NOT NULL;",
            cancelamento);
        return valor is string texto ? LerData(texto) : null;
    }

    public async Task<int> ContarPendenciasAnterioresAsync(DateTimeOffset limite, CancellationToken cancelamento)
    {
        await using var conexao = await AbrirAsync(cancelamento);
        await using var comando = Comando(
            conexao,
            "SELECT COUNT(*) FROM registro WHERE estado IN ('Pendente','EmExecucao') AND pendente_desde IS NOT NULL AND pendente_desde < $l;",
            ("$l", Formatar(limite)));
        return Convert.ToInt32(await comando.ExecuteScalarAsync(cancelamento), CultureInfo.InvariantCulture);
    }

    public async Task<int> ContarBacklogAsync(CancellationToken cancelamento)
    {
        await using var conexao = await AbrirAsync(cancelamento);
        return Convert.ToInt32(
            await EscalarAsync(
                conexao,
                "SELECT COUNT(*) FROM registro WHERE estado IN ('Pendente','EmExecucao') AND pendente_desde IS NOT NULL;",
                cancelamento),
            CultureInfo.InvariantCulture);
    }

    public Task GarantirEntregaAsync(string hash, string nomeOutput, CancellationToken cancelamento) =>
        ExecutarComandoAsync(
            "INSERT OR IGNORE INTO entrega (hash, nome_output, estado) VALUES ($h, $n, 'NaoEntregue');",
            cancelamento,
            ("$h", hash), ("$n", nomeOutput));

    public async Task<IReadOnlyList<EntregaPendente>> ListarEntregasPendentesAsync(CancellationToken cancelamento)
    {
        await using var conexao = await AbrirAsync(cancelamento);
        await using var comando = Comando(
            conexao, "SELECT hash, nome_output, tentativas FROM entrega WHERE estado = 'NaoEntregue' ORDER BY rowid;");
        await using var leitor = await comando.ExecuteReaderAsync(cancelamento);
        var lista = new List<EntregaPendente>();
        while (await leitor.ReadAsync(cancelamento))
        {
            lista.Add(new EntregaPendente(leitor.GetString(0), leitor.GetString(1), leitor.GetInt32(2)));
        }

        return lista;
    }

    public Task RegistrarEntregaAsync(string hash, string nomeDestino, string? traceparent, DateTimeOffset agora, CancellationToken cancelamento) =>
        ExecutarComandoAsync(
            """
            UPDATE entrega SET estado = 'Entregue', nome_destino = $d, traceparent = $p, entregue_em = $t, ultima_falha = NULL
            WHERE hash = $h;
            """,
            cancelamento,
            ("$d", nomeDestino), ("$p", traceparent), ("$t", Formatar(agora)), ("$h", hash));

    public Task RegistrarFalhaEntregaAsync(string hash, string categoria, CancellationToken cancelamento) =>
        ExecutarComandoAsync(
            "UPDATE entrega SET tentativas = tentativas + 1, ultima_falha = $f WHERE hash = $h;",
            cancelamento,
            ("$f", categoria), ("$h", hash));

    public async Task<int> ContarEntregasPendentesAsync(CancellationToken cancelamento)
    {
        await using var conexao = await AbrirAsync(cancelamento);
        return Convert.ToInt32(
            await EscalarAsync(conexao, "SELECT COUNT(*) FROM entrega WHERE estado = 'NaoEntregue';", cancelamento),
            CultureInfo.InvariantCulture);
    }

    public async Task<string?> ObterHashOutputAsync(string nome, long tamanho, DateTimeOffset escritoEm, CancellationToken cancelamento)
    {
        await using var conexao = await AbrirAsync(cancelamento);
        await using var comando = Comando(
            conexao,
            "SELECT hash FROM output_conhecido WHERE nome = $n AND tamanho = $t AND escrito_em = $e;",
            ("$n", nome), ("$t", tamanho), ("$e", Formatar(escritoEm)));
        return await comando.ExecuteScalarAsync(cancelamento) as string;
    }

    public Task RegistrarHashOutputAsync(string nome, long tamanho, DateTimeOffset escritoEm, string hash, CancellationToken cancelamento) =>
        ExecutarComandoAsync(
            """
            INSERT INTO output_conhecido (nome, tamanho, escrito_em, hash) VALUES ($n, $t, $e, $h)
            ON CONFLICT(nome) DO UPDATE SET tamanho = excluded.tamanho, escrito_em = excluded.escrito_em, hash = excluded.hash;
            """,
            cancelamento,
            ("$n", nome), ("$t", tamanho), ("$e", Formatar(escritoEm)), ("$h", hash));

    private static async Task InserirArquivoAsync(
        SqliteConnection conexao, SqliteTransaction transacao, EstadoArquivo arquivo, string agora, CancellationToken cancelamento)
    {
        await using var comando = Comando(
            conexao,
            """
            INSERT OR IGNORE INTO arquivo (hash, hash_raiz, instancia, nome_original, base_raiz, formato, estado, detectado_em, traceparent)
            VALUES ($h, $r, $i, $n, $b, $f, $e, $t, $p);
            """,
            ("$h", arquivo.Hash), ("$r", arquivo.HashRaiz), ("$i", arquivo.Instancia), ("$n", arquivo.NomeOriginal),
            ("$b", arquivo.BaseRaiz), ("$f", arquivo.Formato), ("$e", arquivo.Estado), ("$t", agora), ("$p", arquivo.Traceparent));
        comando.Transaction = transacao;
        await comando.ExecuteNonQueryAsync(cancelamento);
    }

    private async Task ExecutarComandoAsync(string sql, CancellationToken cancelamento, params (string Nome, object? Valor)[] parametros)
    {
        await using var conexao = await AbrirAsync(cancelamento);
        await using var comando = Comando(conexao, sql, parametros);
        await comando.ExecuteNonQueryAsync(cancelamento);
    }

    private async Task<SqliteConnection> AbrirAsync(CancellationToken cancelamento)
    {
        await InicializarAsync(cancelamento);
        return await AbrirSemInicializarAsync(cancelamento);
    }

    private async Task<SqliteConnection> AbrirSemInicializarAsync(CancellationToken cancelamento)
    {
        var conexao = new SqliteConnection(_stringConexao);
        await conexao.OpenAsync(cancelamento);
        await ExecutarAsync(conexao, _pragmas, cancelamento);
        return conexao;
    }

    private static SqliteCommand Comando(SqliteConnection conexao, string sql, params (string Nome, object? Valor)[] parametros)
    {
        var comando = conexao.CreateCommand();
#pragma warning disable CA2100 // SQL fixo; valores sempre parametrizados.
        comando.CommandText = sql;
#pragma warning restore CA2100
        foreach (var (nome, valor) in parametros)
        {
            comando.Parameters.AddWithValue(nome, valor ?? DBNull.Value);
        }

        return comando;
    }

    private static async Task ExecutarAsync(
        SqliteConnection conexao, string sql, CancellationToken cancelamento, SqliteTransaction? transacao = null)
    {
        await using var comando = Comando(conexao, sql);
        comando.Transaction = transacao;
        await comando.ExecuteNonQueryAsync(cancelamento);
    }

    private static async Task<object?> EscalarAsync(SqliteConnection conexao, string sql, CancellationToken cancelamento)
    {
        await using var comando = Comando(conexao, sql);
        var valor = await comando.ExecuteScalarAsync(cancelamento);
        return valor is DBNull ? null : valor;
    }

    private static string? TextoOuNulo(SqliteDataReader leitor, int indice) =>
        leitor.IsDBNull(indice) ? null : leitor.GetString(indice);

    // Datas em UTC ISO 8601, para que comparações textuais (MIN, <) sejam corretas.
    private static string Agora() => Formatar(DateTimeOffset.UtcNow);

    private static string Formatar(DateTimeOffset data) =>
        data.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset LerData(string texto) =>
        DateTimeOffset.Parse(texto, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
