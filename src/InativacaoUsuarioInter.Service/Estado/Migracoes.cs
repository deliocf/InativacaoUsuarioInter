namespace InativacaoUsuarioInter.Service.Estado;

/// <summary>Migrações versionadas do diário SQLite (data-model §2). Reaplicar é idempotente.</summary>
internal static class Migracoes
{
    public static readonly IReadOnlyList<string> Scripts =
    [
        """
        CREATE TABLE IF NOT EXISTS arquivo (
            hash TEXT PRIMARY KEY,
            hash_raiz TEXT NOT NULL,
            instancia TEXT NOT NULL,
            nome_original TEXT NOT NULL,
            base_raiz TEXT NOT NULL,
            formato TEXT NOT NULL,
            estado TEXT NOT NULL,
            causa_rejeicao TEXT NULL,
            linha_log TEXT NULL,
            log_gravado INTEGER NOT NULL DEFAULT 0,
            detectado_em TEXT NOT NULL,
            concluido_em TEXT NULL,
            traceparent TEXT NULL
        );
        CREATE TABLE IF NOT EXISTS registro (
            chave_idempotencia TEXT PRIMARY KEY,
            hash_arquivo TEXT NOT NULL REFERENCES arquivo(hash),
            hash_raiz TEXT NOT NULL,
            indice INTEGER NOT NULL,
            estado TEXT NOT NULL,
            estado_anterior TEXT NULL CHECK (estado_anterior IN ('Ativo','Inativo','Inexistente')),
            tentativas INTEGER NOT NULL DEFAULT 0,
            pendente_desde TEXT NULL,
            linha_log TEXT NULL,
            log_gravado INTEGER NOT NULL DEFAULT 0,
            linhas_afetadas INTEGER NULL,
            atualizado_em TEXT NOT NULL,
            CHECK (estado <> 'EmExecucao' OR estado_anterior IS NOT NULL)
        );
        CREATE INDEX IF NOT EXISTS ix_registro_hash_raiz ON registro(hash_raiz);
        CREATE INDEX IF NOT EXISTS ix_registro_pendencia ON registro(estado, pendente_desde);
        CREATE TABLE IF NOT EXISTS parte_gerada (
            hash TEXT PRIMARY KEY,
            hash_origem TEXT NOT NULL REFERENCES arquivo(hash),
            tipo TEXT NOT NULL,
            caminho_final TEXT NOT NULL,
            gravado_em TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS entrega (
            hash TEXT PRIMARY KEY,
            nome_output TEXT NOT NULL,
            nome_destino TEXT NULL,
            estado TEXT NOT NULL CHECK (estado IN ('NaoEntregue','Entregue')),
            tentativas INTEGER NOT NULL DEFAULT 0,
            ultima_falha TEXT NULL,
            entregue_em TEXT NULL,
            traceparent TEXT NULL
        );
        """,
        // v2 (T098): cache de hash dos arquivos de Output para a reconciliação da replicação.
        """
        CREATE TABLE IF NOT EXISTS output_conhecido (
            nome TEXT PRIMARY KEY,
            tamanho INTEGER NOT NULL,
            escrito_em TEXT NOT NULL,
            hash TEXT NOT NULL
        );
        """,
        // v3 (feature 002, data-model §2): instante em que a entrega entrou na fila Output (alerta R-07).
        // Aplicada uma única vez, na mesma transação do registro em versao_esquema.
        """
        ALTER TABLE entrega ADD COLUMN registrada_em TEXT NULL;
        UPDATE entrega SET registrada_em = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE registrada_em IS NULL;
        """,
        // v4 (bug ja-inativo-sem-replicacao): a mesma parte (mesmo conteúdo) pode vir de arquivos de origem diferentes
        // e cada uma é entregue. parte_gerada passa a ser única por (hash, hash_origem) e entrega por (hash, nome_output).
        // Recria as tabelas preservando as linhas e a ordem (rowid) — a fila de entregas segue na mesma ordem.
        """
        CREATE TABLE parte_gerada_v4 (
            hash TEXT NOT NULL,
            hash_origem TEXT NOT NULL REFERENCES arquivo(hash),
            tipo TEXT NOT NULL,
            caminho_final TEXT NOT NULL,
            gravado_em TEXT NOT NULL,
            PRIMARY KEY (hash, hash_origem)
        );
        INSERT INTO parte_gerada_v4 (hash, hash_origem, tipo, caminho_final, gravado_em)
            SELECT hash, hash_origem, tipo, caminho_final, gravado_em FROM parte_gerada ORDER BY rowid;
        DROP TABLE parte_gerada;
        ALTER TABLE parte_gerada_v4 RENAME TO parte_gerada;
        CREATE TABLE entrega_v4 (
            hash TEXT NOT NULL,
            nome_output TEXT NOT NULL,
            nome_destino TEXT NULL,
            estado TEXT NOT NULL CHECK (estado IN ('NaoEntregue','Entregue')),
            tentativas INTEGER NOT NULL DEFAULT 0,
            ultima_falha TEXT NULL,
            entregue_em TEXT NULL,
            traceparent TEXT NULL,
            registrada_em TEXT NULL,
            PRIMARY KEY (hash, nome_output)
        );
        INSERT INTO entrega_v4 (hash, nome_output, nome_destino, estado, tentativas, ultima_falha, entregue_em, traceparent, registrada_em)
            SELECT hash, nome_output, nome_destino, estado, tentativas, ultima_falha, entregue_em, traceparent, registrada_em
            FROM entrega ORDER BY rowid;
        DROP TABLE entrega;
        ALTER TABLE entrega_v4 RENAME TO entrega;
        """,
    ];
}
