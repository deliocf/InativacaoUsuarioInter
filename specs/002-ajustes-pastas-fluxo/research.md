# Research: Ajustes de Pastas, Fluxo de Arquivos e Atualização no WAccess

**Feature**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md)

Cada item segue o formato **Decisão / Justificativa / Alternativas consideradas**. A numeração continua independente da feature 001; referências a ela usam o prefixo `001/` (ex.: `001/R-10`).

---

## R-01 — Esquema real de `CHDownloadRequired` (evidência coletada)

Consulta **somente leitura** e agregada ao banco local de testes `W_ACCESS` (2026-11-12):

| Coluna | Tipo | NULL | Default |
| --- | --- | --- | --- |
| `CHState` | `smallint` | não | `0` |
| `CHDownloadRequired` | `bit` | **sim** | — |
| `IdNumber` | `varchar(50)` | sim | — |

Distribuição de `CHDownloadRequired`: `NULL` = 3.412, `0` = 22.326, `1` = 337. Entre as linhas com `CHState = 1`, 1.722 têm `NULL` e 19.176 têm `0`; **nenhuma** tem `1`.

**Decisão**: tratar `NULL` como "sem marca de download". O predicado de alteração é `CHState <> 1 OR CHDownloadRequired IS NULL OR CHDownloadRequired = 0`, e o UPDATE grava `CHState = 1, CHDownloadRequired = 1`.

**Justificativa**: `ISNULL(CHDownloadRequired, 0) <> 1` seria equivalente, mas impede o uso de índice e esconde a regra; a forma explícita documenta os três casos. A distribuição confirma que praticamente todos os cadastros inativados até hoje estão sem a marca, o que reforça a decisão da Clarification 3 (marcar também os já inativos quando reenviados).

**Alternativas**: tratar só `= 0` (deixaria 1.722 linhas `NULL` sem marca para sempre); tornar a coluna `NOT NULL` (alteração de esquema de produto de terceiros, fora de escopo).

## R-02 — Classificação `INATIVADO` × `JA_INATIVO` com a nova coluna

O fluxo atual (001/R-03) faz: passo A (pré-leitura → `Ativo`/`Inativo`/`Inexistente`), grava a intenção no diário, e só executa o passo B (UPDATE) quando o estado anterior é `Ativo`.

**Decisão**:
- O passo A não muda: `Ativo` continua significando "ao menos uma linha com `CHState <> 1`".
- O passo B passa a ser executado quando o estado anterior é `Ativo` **ou** `Inativo` (nunca para `Inexistente`).
- O UPDATE usa o predicado de R-01 e conta, via `OUTPUT deleted.CHState`, quantas linhas **mudaram de estado** (`deleted.CHState <> 1`) e quantas só receberam a marca de download. A telemetria registra as duas contagens (`waccess.linhas_afetadas`, `waccess.linhas_marcadas_download`).
- A classificação continua usando somente o estado anterior gravado no diário: `Inativo` → `JA_INATIVO`, mesmo que o passo B tenha marcado download. Nenhum valor novo de `estado_anterior` é necessário (o `CHECK` do diário não muda).

**Justificativa**: preserva a classificação determinística e retomável de 001/R-03 (o resultado não depende de reler o banco após um crash). O custo extra é um UPDATE sem efeito para CPFs já inativos e já marcados, que é raro e barato (predicado indexado por `IdNumber`).

**Alternativas**: novo estado anterior `InativoSemDownload` (exige migração do `CHECK` no SQLite, que não suporta `ALTER CONSTRAINT`, e não muda o resultado de negócio); ler `CHDownloadRequired` no passo A e pular o passo B quando tudo já estiver marcado (otimização sem medição — Princípio VI desencoraja).

## R-03 — Papéis das pastas no Inter: `Processed` (histórico) e `Output` (fila de saída)

Hoje `Output` é, ao mesmo tempo, histórico permanente e origem da replicação, e a reconciliação calcula o hash de todo o histórico (com cache, 001/T098).

**Decisão**:
- `{FolderPathInter}/Processed` passa a ser o **destino de concluídos** do Inter (o equivalente ao antigo `Output`): o original integral é **movido** para lá; a parte `__sucesso-` é **gravada** lá.
- `{FolderPathInter}/Output` passa a ser **fila de saída**: no mesmo passo, uma **cópia** do mesmo conteúdo é gravada em `Output` (tmp + rename) e registrada como entrega pendente.
- No fim do ciclo (como hoje, depois de todos os arquivos), o serviço de replicação entrega cada pendente de `Output` em `{FolderPathCondominio}/Input` e, **após registrar `Entregue` no diário**, remove o arquivo de `Output`.
- Ordem tolerante a crash por arquivo concluído (mantém 001/R-10 "partes gravadas → diário → original removido"):
  1. gravar a cópia em `Output` (tmp + rename; destino idêntico é no-op);
  2. gravar a parte em `Processed` (partes) — para o integral, este passo é o move do item 4;
  3. registrar no diário, numa transação, a parte (`caminho_final` = caminho em `Processed`) e a entrega pendente (`nome_output` = nome em `Output`);
  4. mover o original para `Processed` (integral) ou removê-lo (segregado); concluir o arquivo no diário.

- A pasta remota `{FolderPathCondominio}/Input` é criada (se ausente) somente dentro da entrega, no pipeline `pasta-replicacao`; ela **não** faz parte de `GarantirEstrutura`, cuja falha interrompe o ciclo. Assim, uma queda do compartilhamento do Condomínio só atrasa entregas (FR-002a).

**Justificativa**: o resultado observável é exatamente o da spec (entregue → `Output` vazio; falha → arquivo em `Output`; sempre cópia em `Processed`), com um único caminho de código para entrega imediata e reentrega. "Tentar entregar direto e só gravar em `Output` se falhar" teria dois caminhos e uma janela de crash em que o arquivo não está nem em `Output` nem entregue.

**Alternativas**: entregar a partir de `Processed` e usar `Output` só como marcador (contraria a spec: `Output` deve conter o arquivo pendente); entregar dentro do processamento de cada arquivo (soma latência de rede ao caminho do WAccess e complica o orçamento do ciclo; o fim de ciclo já atende SC-003).

## R-04 — Limpeza de `Output` após a entrega e arquivos herdados (FR-008, FR-004a)

**Decisão**: regra única aplicada a todo arquivo de `Output` cujo hash consta no diário com entrega `Entregue` — seja logo após a entrega, seja na reconciliação do início da replicação (reinício entre "registrar Entregue" e "remover"; ou arquivo herdado da versão anterior):
- se `Processed` já tem arquivo com o mesmo hash (mesmo nome ou nome de colisão) → remover de `Output` (com o `.traceparent`, se houver);
- senão → **mover** para `Processed` (regra de colisão de nomes de 001/FR-030).

Arquivos herdados em `Output` com entrega `NaoEntregue` seguem o fluxo normal: são entregues e, como `Processed` não tem cópia, a regra acima os move para lá. Arquivos sem registro no diário continuam gerando `ARQUIVO_OUTPUT_DESCONHECIDO` e não são tocados.

**Justificativa**: uma regra idempotente cobre três situações (fluxo normal, crash, migração) e nunca perde o histórico da versão anterior, que vivia em `Output`.

**Alternativas**: tratar herdados num passo separado de migração (duplicaria a lógica de reconciliação por hash).

## R-05 — Nome do arquivo de falhas (FR-010)

**Decisão**: a parte de falhas passa de `<baseRaiz>__falhas-<h8>.<ext>` para `<baseRaiz>.<ext>` — o nome do arquivo original como depositado (para partes `__pendente-` reprocessadas, a base raiz é a do original). Se já existir em `Fail` um arquivo com esse nome e hash diferente, aplica-se a regra de colisão existente (`<base>__<h8>.<ext>`); hash igual é no-op. Arquivos rejeitados (`ARQUIVO_INVALIDO`/`ARQUIVO_EXCEDE_LIMITE`) já usam o nome original e não mudam. As partes `__sucesso-` e `__pendente-` mantêm o nome atual.

**Justificativa**: atende literalmente "mesmo nome do arquivo original" no caso comum e mantém a garantia de nunca sobrescrever. O sufixo de colisão segue rastreável ao original pelo prefixo.

**Alternativas**: sobrescrever (proibido por 001/FR-030); acrescentar ao arquivo existente (misturaria lotes diferentes e quebraria a idempotência por hash).

## R-06 — Migração das pastas com nomes antigos (FR-004)

**Decisão**: novo componente `MigradorLegado` no módulo `Arquivos`, chamado **em todo ciclo**, depois de `GarantirEstrutura` e antes da limpeza de temporários, da recuperação e da leitura de `Input`. Ele só age se existir alguma pasta antiga sob a raiz da instância:

| Origem | Destino | Regra |
| --- | --- | --- |
| `Imput/*.json`, `*.txt` (inclui partes `__pendente-`) | `Input/` | move com o `.traceparent`; colisão → regra de 001/FR-030 |
| `Processados/*` | `Processed/` | move; colisão idem |
| `Falhas/*` exceto o log | `Fail/` | move; colisão idem |
| `Falhas/log_inativacao.log` | `Fail/log_inativacao.log` | mescla (abaixo) |
| `Auditoria/log_inativacao.log` | `Logs/log_inativacao.log` | mescla (abaixo) |
| `Auditoria/*` (outros) | `Logs/` | move; colisão idem |

- `.tmp` **não** são migrados nem apagados: em `Imput` do Condomínio podem ser uma cópia em andamento de um Inter ainda na versão antiga. A pasta antiga só é removida quando fica vazia; por isso a migração roda a cada ciclo e absorve entregas de um Inter antigo durante uma implantação escalonada.
- **Mescla de log** (idempotente sem estado extra): monta `<destino>.tmp` = conteúdo antigo + conteúdo atual do destino (se houver); substitui o destino de forma atômica (`File.Replace`); remove o log antigo. Antes de mesclar, se o destino **já começa** com os bytes exatos do log antigo (crash entre substituir e remover), apenas remove o antigo. A leitura/cópia é em streaming (Princípio VI).
- O diário não precisa ser migrado: arquivos e registros são identificados pelo **hash do conteúdo** (001/R-07), que não muda com a pasta; `parte_gerada.caminho_final` é apenas informativo.
- Toda E/S passa pelo pipeline `pasta-local`; falhas são classificadas e a migração é retomada no ciclo seguinte. Cada item migrado gera log técnico estruturado (nome do arquivo e pastas; sem CPF/nome).

**Justificativa**: mantém o Princípio III (idempotência e retomada) com a mesma técnica já usada (rename atômico + comparação por hash) e cobre implantação fora de ordem entre Inter e Condomínio. A ordem "antigo antes do novo" na mescla é cronologicamente correta, porque tudo o que está no log antigo foi escrito antes da nova versão.

**Alternativas**: migração única na partida (não absorve arquivos que um Inter antigo continue entregando em `Imput`); anexar linha a linha com verificação `ContemLinha` (O(n·m) e não atômico); script manual (rejeitado na Clarification 4).

## R-07 — Alerta de entrega atrasada em `Output`

**Decisão**: novo alerta de saúde `ENTREGA_PENDENTE_ACIMA_LIMITE`, ativo quando a entrega pendente mais antiga foi registrada há mais de `Processamento:AlertaPendenciaHoras` (padrão 24 h). Exige a coluna `entrega.registrada_em` (migração v3 do diário; linhas herdadas recebem o instante da migração).

**Justificativa**: a spec (FR-007a) pede alerta para arquivo parado em `Output`; reaproveitar o limite existente evita configuração nova.

**Alternativas**: usar a data de escrita do arquivo em `Output` (não sobrevive a cópias/restaurações e exige varrer a pasta).

## R-08 — Constantes de nomes de pastas

**Decisão**: centralizar os nomes em uma classe `NomesPastas` (`Input`, `Output`, `Processed`, `Logs`, `Fail`) e os antigos em `NomesPastasLegado` (usada apenas pelo `MigradorLegado`). `IPoliticaFluxo` ganha `PastaSaida` (somente Inter) e `PastaLogs`/`PastaFail` substituem `PastaAuditoria`/`PastaFalhas`. `SondaDependencias`, `IRegistros`, `VerificadorPermissoes` e `ServicoReplicacao` deixam de usar literais.

**Justificativa**: hoje os literais estão espalhados por 8 arquivos (`"Imput"` em três deles), o que causou o próprio problema; SC-002 exige zero referências aos nomes antigos fora da migração, verificável por um teste de contrato que procura os literais antigos no assembly.

**Alternativas**: tornar os nomes configuráveis (não pedido; aumentaria a superfície de validação).
