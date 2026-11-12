# Data Model: Ajustes de Pastas, Fluxo de Arquivos e Atualização no WAccess

**Base**: [`specs/001-inativacao-usuarios-waccess/data-model.md`](../001-inativacao-usuarios-waccess/data-model.md). Este documento descreve **apenas as diferenças**.

## 1. Estrutura de pastas por instância

| Pasta | Inter (`FolderPathInter`) | Condomínio (`FolderPathCondominio`) | Conteúdo | Acesso restrito (FR-038 da 001) |
| --- | --- | --- | --- | --- |
| `Input` | entrada | entrada (inclui entregas do Inter) | `.json`/`.txt`, `.traceparent`, partes `__pendente-` | não |
| `Output` | fila de saída | — (não criada) | cópias aguardando entrega ao Condomínio | não |
| `Processed` | histórico de concluídos | histórico de concluídos | original integral ou parte `__sucesso-` | não |
| `Logs` | sim | sim | `log_inativacao.log` (auditoria) | **sim** |
| `Fail` | sim | sim | `log_inativacao.log` (falhas), arquivos de falhas e rejeitados | **sim** |

O Inter cria `{FolderPathCondominio}/Input` somente no momento da entrega (FR-002a); falha nisso não interrompe o ciclo. Nomes antigos (só para migração): `Imput`→`Input`, `Auditoria`→`Logs`, `Processados`→`Processed`, `Falhas`→`Fail`; no Inter, o histórico que estava em `Output` passa para `Processed` (research R-04).

## 2. Diário SQLite — migração v3

```sql
ALTER TABLE entrega ADD COLUMN registrada_em TEXT NULL;
UPDATE entrega SET registrada_em = <agora> WHERE registrada_em IS NULL;
```

- `registrada_em`: instante em que a entrega pendente foi criada; alimenta `ENTREGA_PENDENTE_ACIMA_LIMITE` (research R-07).
- Nenhuma outra tabela muda. `estado_anterior` continua `Ativo|Inativo|Inexistente` (research R-02).
- Significados atualizados:
  - `parte_gerada.caminho_final`: caminho em `Processed` (Inter e Condomínio) ou em `Fail`; informativo.
  - `entrega.nome_output`: nome do arquivo na **fila de saída** `{FolderPathInter}/Output`.
  - `output_conhecido`: cache de hash dos arquivos de `Output` (continua válido; a pasta tende a ficar quase vazia).

Novas operações em `IArmazenamentoEstado`:

| Operação | Uso |
| --- | --- |
| `ObterEntregaAsync(hash, nomeOutput)` → `{Estado, NomeOutput, NomeDestino}?` | reconciliação de `Output`: decidir entre entregar, limpar ou alertar (research R-04) |
| `ObterEntregaPendenteMaisAntigaAsync()` → `DateTimeOffset?` | alerta R-07 |
| `ResolverChaveArquivoAsync(nome, hashConteudo)` → `string` | identidade do arquivo de entrada (FR-009a) |

`RegistrarParteAsync(..., registrarEntrega: true)` passa a gravar `registrada_em`.

### Migração v4 (bug `ja-inativo-sem-replicacao`, FR-009/FR-009a)

- `arquivo.hash` passa a ser a **identidade** do arquivo de entrada: `SHA-256(nome|hashConteudo)`. Linhas da versão anterior (identidade = hash do conteúdo) continuam válidas: `ResolverChaveArquivoAsync` devolve o hash do conteúdo quando o diário já tem esse conteúdo com o mesmo `nome_original`. As chaves de registro (`instancia|hashRaiz|cpf`) derivam dessa identidade.
- *(bug `nome-original-sem-trace`, sem migração)* `entrega.nome_destino` passa a ser preenchido já no registro da entrega com o **nome original** com que o arquivo deve aparecer no Condomínio (a fila `Output` pode usar nome único quando duas partes do mesmo original coexistem). Entregas antigas com `nome_destino` nulo usam `nome_output`. `entrega.ultima_falha = 'DestinoOcupado'` indica que o Condomínio ainda tem outro arquivo com esse nome. `parte_gerada.caminho_final` de partes de sucesso/falhas aponta para o arquivo do histórico com o nome original, que pode conter outras partes mescladas.
- `parte_gerada.hash` e `entrega.hash` continuam sendo o hash do **conteúdo** da parte (comparado com os arquivos em disco). As tabelas são recriadas com chave `(hash, hash_origem)` e `(hash, nome_output)`, preservando linhas e ordem: a mesma parte gerada por outro arquivo de entrada é outra entrega.

## 3. Ciclo de vida de um conteúdo concluído no Inter

```text
Input/<nome>
   │ concluído (integral ou parte de sucesso)
   ▼
Output/<nome'> (cópia) + Processed/<nome'>      diário: parte + entrega NaoEntregue
   │ fim do ciclo: entrega
   ├── falha ──► permanece em Output; tentativas++ ; nova tentativa no próximo ciclo
   ▼ sucesso
Condominio/Input/<nome''>  (tmp → hash → .traceparent → rename)
   │ diário: entrega Entregue (durável)
   ▼
remove Output/<nome'>   (ou move para Processed se lá não houver cópia — herdado)
```

`<nome'>` = nome original (integral) ou `<base>__sucesso-<h8>.<ext>`; `<nome''>` = `<nome'>` ou nome de colisão no destino.

## 4. Arquivo de falhas

| Campo | Antes (001) | Agora |
| --- | --- | --- |
| Nome | `<baseRaiz>__falhas-<h8>.<ext>` | `<baseRaiz>.<ext>`; colisão com conteúdo diferente → `<baseRaiz>__<h8>.<ext>` |
| Pasta | `{raiz}/Falhas` | `{raiz}/Fail` (cada instância na própria raiz) |
| Conteúdo | inalterado: texto bruto dos registros com falha definitiva, formato do original | — |

## 5. Usuário WAccess (`dbo.CHMain`)

| Campo | Tipo | Regra |
| --- | --- | --- |
| `IdNumber` | `varchar(50) NULL` | busca por CPF (inalterado) |
| `CHState` | `smallint NOT NULL` | `1` = inativo (inalterado) |
| `CHDownloadRequired` | `bit NULL` | **novo**: gravado `1` junto com a inativação; `NULL`/`0` = sem marca |

Linha alterada se, e somente se, `CHState <> 1 OR CHDownloadRequired IS NULL OR CHDownloadRequired = 0`. Status de auditoria por CPF: `INATIVADO` se o estado anterior gravado era `Ativo`; `JA_INATIVO` se era `Inativo` (mesmo que alguma linha tenha recebido só a marca de download).

## 6. Migração de legado (estado implícito, sem tabela)

| Situação observada no início do ciclo | Ação |
| --- | --- |
| Pasta antiga com arquivos (exceto `.tmp`) | move cada arquivo para a pasta nova (colisão: no-op se mesmo hash; senão nome de colisão) |
| Log antigo e destino **não** começa com seus bytes | mescla atômica (antigo + atual) e remove o antigo |
| Log antigo e destino **já** começa com seus bytes | remove o antigo (retomada após crash) |
| Pasta antiga vazia | remove a pasta |
| Pasta antiga só com `.tmp` | mantém (cópia em andamento de um Inter antigo); tenta de novo no próximo ciclo |
