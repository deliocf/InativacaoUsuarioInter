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
    ];
}
