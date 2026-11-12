# Contrato: Estrutura de pastas e migração de legado

**Origem**: FR-001–FR-004a; [research.md R-04, R-06, R-08](../research.md).
**Substitui**: as seções de estrutura de diretórios de `specs/001-inativacao-usuarios-waccess/contracts/*.md`.
**Teste de contrato**: `tests/InativacaoUsuarioInter.Tests.Contract/EstruturaDiretorios/*`.

## Pastas obrigatórias (criadas no início de cada ciclo, sem apagar nada)

| Instância | Pastas |
| --- | --- |
| `Inter` | `{FolderPathInter}/Input`, `/Output`, `/Processed`, `/Logs`, `/Fail` |
| `Condominio` | `{FolderPathCondominio}/Input`, `/Processed`, `/Logs`, `/Fail` |

- Falha ao criar uma pasta obrigatória da própria raiz interrompe o processamento daquele ciclo (erro classificado, serviço continua).
- `{FolderPathCondominio}/Input` **não** é pasta obrigatória do Inter: é criada no momento da entrega ([replicacao.md](./replicacao.md)); falha nisso é transitória da entrega (FR-002a).

- `Logs` e `Fail` contêm dados pessoais: em Unix são criadas com permissão `0700`; no Windows a verificação de ACL existente (`PERMISSAO_ABERTA_DADOS_PESSOAIS`) passa a olhar essas pastas.
- Os nomes são fixos (não configuráveis) e sensíveis à grafia: `Input`, `Output`, `Processed`, `Logs`, `Fail`.
- Os nomes `Imput`, `Auditoria`, `Processados` e `Falhas` não podem aparecer no código do serviço fora de `NomesPastasLegado` (verificado por teste de contrato).

## Arquivos por pasta

| Pasta | Arquivo | Escrito por |
| --- | --- | --- |
| `Input` | `<nome>.json\|.txt`, `<nome>.traceparent`, `<base>__pendente-<h8>.<ext>` | operador; Inter (no Condomínio); serviço (pendentes) |
| `Output` (Inter) | `<nome>` ou `<base>__sucesso-<h8>.<ext>` (+ `.traceparent` herdado) | serviço; removido após entrega confirmada |
| `Processed` | original integral ou `<base>__sucesso-<h8>.<ext>` (+ `.traceparent`) | serviço |
| `Logs` | `log_inativacao.log` | serviço (somente acréscimo) |
| `Fail` | `log_inativacao.log`; `<baseRaiz>.<ext>` (registros com falha definitiva); arquivos rejeitados com o nome original | serviço |

Colisão em qualquer pasta: mesmo hash → no-op; hash diferente → `<base>__<h8>.<ext>`. Nada é sobrescrito.

## Migração de legado (todo ciclo, antes de ler `Input`)

| Origem | Destino | Como |
| --- | --- | --- |
| `Imput/*.json`, `*.txt` + `.traceparent` | `Input/` | move |
| `Processados/*` | `Processed/` | move |
| `Falhas/*` (exceto log) | `Fail/` | move |
| `Falhas/log_inativacao.log` | `Fail/log_inativacao.log` | mescla: antigo + atual, troca atômica, remove antigo |
| `Auditoria/log_inativacao.log` | `Logs/log_inativacao.log` | mescla idem |
| `Auditoria/*` (outros) | `Logs/` | move |
| Inter: `Output/*` com entrega `Entregue` no diário | `Processed/` | move (ou remove, se `Processed` já tem o mesmo hash) |

Invariantes verificáveis:
1. Após a migração, número de arquivos e de linhas de log = antes (sem duplicatas), mesmo com interrupção em qualquer ponto e retomada.
2. `.tmp` em pastas antigas não são migrados nem apagados; a pasta antiga é removida apenas quando vazia.
3. Nenhum registro já concluído é reinativado ou reauditado depois da migração (o diário é indexado por hash de conteúdo).
4. Cada item migrado gera um log técnico estruturado (`MigracaoLegado`) com pasta de origem, destino e nome do arquivo, sem CPF ou nome.
