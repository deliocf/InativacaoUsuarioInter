# Feature Specification: Ajustes de Pastas, Fluxo de Arquivos e Atualização no WAccess

**Feature Branch**: `main` (nenhum hook de criação de branch registrado; diretório da feature: `specs/002-ajustes-pastas-fluxo`)

**Created**: 2026-11-12

**Status**: Draft

**Input**: User description: "Ajustes e correções InativacaoUsuarioInter — renomear as pastas de `FolderPathInter`/`FolderPathCondominio` (Imput→Input, Auditoria→Logs, Processado→Processed, Falhas→Fail) e criá-las quando não existirem; rever a sequência de armazenamento e movimentação de arquivos nas instâncias Inter e Condomínio; o UPDATE em `CHMain` deve atualizar também `CHDownloadRequired = 1`."

**Relação com a feature anterior**: esta spec **altera** a feature `specs/001-inativacao-usuarios-waccess` (doravante "spec 001"). Tudo o que não for alterado aqui continua valendo como está na spec 001: formatos de entrada, validação, logs contratuais, classificação de erros, idempotência, telemetria, saúde e orçamento de performance. Os requisitos marcados como "substitui FR-0xx" anulam o requisito correspondente da spec 001.

## Clarifications

### Session 2026-11-12

- Q: Na inativação, qual valor deve ser gravado em `CHState`: `1` (como o código grava hoje) ou `0` (como diz o pedido)? → A: Manter `CHState = 1` como estado de inativação e acrescentar `CHDownloadRequired = 1`; o "=0" do pedido foi engano.
- Q: Na instância Condomínio, o arquivo de falhas definitivas vai para a pasta `Fail` do próprio Condomínio ou para a do Inter? → A: `{FolderPathCondominio}/Fail` — arquivo e log de falhas do Condomínio ficam no próprio Condomínio; a menção a `FolderPathInter` no pedido foi engano.
- Q: Quando o cadastro já tem `CHState = 1` mas `CHDownloadRequired = 0`, o serviço grava `CHDownloadRequired = 1` mesmo assim? → A: Sim; a linha é atualizada se `CHState <> 1` ou `CHDownloadRequired <> 1`, e o status de auditoria continua `JA_INATIVO` quando o `CHState` já era 1.
- Q: Na primeira execução da nova versão, o serviço migra sozinho o conteúdo das pastas com nomes antigos ou só avisa? → A: Migra tudo — `Imput`→`Input` (com `.traceparent`), `Processados`→`Processed`, arquivos de `Falhas`→`Fail`, e os logs de `Auditoria`/`Falhas` são anexados aos novos `log_inativacao.log` de `Logs`/`Fail`.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Pastas com nomes padronizados e criadas automaticamente (Priority: P1)

O operador configura as raízes `SharePointFolder:FolderPathInter` e `SharePointFolder:FolderPathCondominio` e encontra, abaixo delas, pastas com os nomes padronizados `Input`, `Output`, `Processed`, `Logs` e `Fail`. Se alguma pasta não existir, o serviço a cria sozinho, sem intervenção manual.

**Why this priority**: todas as outras histórias dependem dos nomes novos. O nome `Imput` é um erro de grafia que confunde quem deposita arquivos, e hoje a operação já criou as pastas com os nomes novos (o arquivo de teste está em `{FolderPathInter}/Input`, que o serviço atual não lê).

**Independent Test**: apontar as raízes para diretórios vazios, iniciar o serviço e verificar que as pastas com os nomes novos são criadas; depositar um arquivo em `{raiz}/Input` e verificar que ele é processado.

**Acceptance Scenarios**:

1. **Given** a instância `Inter` e a raiz `FolderPathInter` sem subpastas, **When** ocorre o primeiro ciclo, **Then** existem `{FolderPathInter}/Input`, `Output`, `Processed`, `Logs` e `Fail`; `{FolderPathCondominio}/Input` é criada na primeira entrega, se estiver acessível (FR-002a).
2. **Given** a instância `Condominio` e a raiz `FolderPathCondominio` sem subpastas, **When** ocorre o primeiro ciclo, **Then** existem `{FolderPathCondominio}/Input`, `Processed`, `Logs` e `Fail`.
3. **Given** um arquivo depositado em `{raiz}/Input`, **When** ocorre o ciclo, **Then** ele é lido e processado.
4. **Given** uma raiz com pastas de nomes antigos contendo arquivos (`Imput` com um arquivo e seu `.traceparent`, `Processados`, `Falhas` com arquivos de falhas e log, `Auditoria` com log), **When** ocorre o primeiro ciclo da nova versão, **Then** o arquivo de entrada e seu `.traceparent` estão em `Input` e são processados nesse ciclo, os arquivos de `Processados` estão em `Processed`, os arquivos de falhas estão em `Fail`, as linhas dos logs antigos estão mescladas (antes das novas) em `Logs/log_inativacao.log` e `Fail/log_inativacao.log`, e as pastas antigas, vazias, foram removidas.
5. **Given** a migração já concluída (ou interrompida no meio e retomada), **When** ocorrem novos ciclos, **Then** nenhum arquivo é duplicado ou perdido e nenhuma linha de log aparece duas vezes.
6. **Given** uma inativação concluída, **When** ela é registrada, **Then** a linha de auditoria vai para `{raiz}/Logs/log_inativacao.log` e uma falha definitiva vai para `{raiz}/Fail/log_inativacao.log`, com o mesmo layout contratual da spec 001.

---

### User Story 2 - Fluxo de arquivos na instância Inter (Priority: P1)

O Inter lê `{FolderPathInter}/Input`, processa os registros e, para os registros concluídos com sucesso, entrega o arquivo diretamente em `{FolderPathCondominio}/Input` e guarda uma cópia em `{FolderPathInter}/Processed`. Se a entrega ao Condomínio falhar, o arquivo fica em `{FolderPathInter}/Output` como fila de saída e é reentregue nos ciclos seguintes até conseguir; uma vez entregue, sai de `Output`. Registros com falha definitiva vão para um arquivo em `{FolderPathInter}/Fail` com o mesmo nome do original.

**Why this priority**: é o fluxo principal de negócio. Hoje o Inter só replica quando a entrada está vazia e deixa uma cópia permanente em `Output`, o que atrasa o Condomínio e mistura "entregue" com "não entregue" na mesma pasta.

**Independent Test**: com as duas raízes acessíveis, depositar um arquivo em `{FolderPathInter}/Input` e verificar, após um ciclo, o arquivo em `{FolderPathCondominio}/Input` e em `{FolderPathInter}/Processed`, com `Output` vazio. Repetir com o destino do Condomínio indisponível e verificar o arquivo em `Output` até a entrega.

**Acceptance Scenarios**:

1. **Given** um arquivo em `{FolderPathInter}/Input` com todos os registros inativados com sucesso e o destino do Condomínio disponível, **When** o ciclo termina, **Then** o arquivo está em `{FolderPathCondominio}/Input` (íntegro), uma cópia idêntica está em `{FolderPathInter}/Processed`, nada fica em `{FolderPathInter}/Output` e o arquivo não está mais em `{FolderPathInter}/Input`.
2. **Given** o mesmo arquivo e o destino do Condomínio indisponível (compartilhamento fora do ar, sem permissão ou `{FolderPathCondominio}/Input` impossível de criar), **When** o ciclo termina, **Then** os arquivos de `{FolderPathInter}/Input` foram processados normalmente, o arquivo está em `{FolderPathInter}/Output` e em `{FolderPathInter}/Processed`, não está em `{FolderPathInter}/Input`, e nenhum registro é inativado de novo nos ciclos seguintes.
3. **Given** um arquivo em `{FolderPathInter}/Output` aguardando entrega, **When** o destino do Condomínio volta a ficar disponível, **Then** no primeiro ciclo seguinte o arquivo é entregue íntegro em `{FolderPathCondominio}/Input` e removido de `{FolderPathInter}/Output`, sem nova inativação.
4. **Given** um arquivo em que um CPF não existe no WAccess e os demais são inativados, **When** o ciclo termina, **Then** `{FolderPathInter}/Fail` recebe um arquivo **com o mesmo nome do original** contendo somente o registro que falhou, e o Condomínio e `Processed` recebem um arquivo contendo somente os registros inativados.
5. **Given** um arquivo em que todos os registros falham definitivamente, **When** o ciclo termina, **Then** só existe o arquivo em `{FolderPathInter}/Fail` (com todos os registros); nada é entregue ao Condomínio nem gravado em `Processed` ou `Output`.
6. **Given** a entrega ao Condomínio concluída e o serviço reiniciado antes de remover o arquivo de `Output`, **When** ocorre o ciclo seguinte, **Then** o arquivo é removido de `Output` sem gerar uma segunda cópia no Condomínio.

---

### User Story 3 - Fluxo de arquivos na instância Condomínio (Priority: P2)

O Condomínio lê `{FolderPathCondominio}/Input`, processa os registros no WAccess do condomínio, move o arquivo processado com sucesso para `{FolderPathCondominio}/Processed` e grava os registros com falha definitiva em um arquivo com o mesmo nome do original em `{FolderPathCondominio}/Fail`.

**Why this priority**: depende de o Inter entregar arquivos em `{FolderPathCondominio}/Input` (US2), mas pode ser testada sozinha depositando arquivos manualmente.

**Independent Test**: com a instância configurada como `Condominio`, depositar arquivos em `{FolderPathCondominio}/Input` e verificar banco, `Logs`, `Processed` e `Fail` do Condomínio.

**Acceptance Scenarios**:

1. **Given** `Settings:Instancia = Condominio` e um arquivo em `{FolderPathCondominio}/Input` com todos os registros inativados, **When** o ciclo termina, **Then** o arquivo está em `{FolderPathCondominio}/Processed` e não está mais em `Input`; nenhuma cópia é enviada a outro ambiente.
2. **Given** um arquivo com um registro de falha definitiva, **When** o ciclo termina, **Then** `{FolderPathCondominio}/Fail` recebe um arquivo com o mesmo nome do original contendo somente esse registro, e `Processed` recebe somente os registros inativados.
3. **Given** o arquivo `.traceparent` que acompanha um arquivo entregue pelo Inter, **When** o arquivo é processado, **Then** o processamento no Condomínio continua correlacionado ao do Inter, como na spec 001.

---

### User Story 4 - Inativação sinaliza download para os equipamentos (Priority: P1)

Ao inativar um usuário no WAccess, o serviço marca também o cadastro como pendente de download (`CHDownloadRequired = 1`), para que o WAccess propague a alteração aos equipamentos de controle de acesso.

**Why this priority**: sem essa marca, o usuário fica inativo no cadastro mas continua com acesso nos equipamentos até uma sincronização manual — o objetivo de revogar o acesso não é atingido.

**Independent Test**: no banco de testes local, inativar um CPF ativo e verificar, em todas as linhas de `CHMain` com aquele `IdNumber`, o estado de inativação e `CHDownloadRequired = 1`, sem alteração em outros campos.

**Acceptance Scenarios**:

1. **Given** um CPF ativo no WAccess, **When** é inativado, **Then** todas as suas linhas em `CHMain` ficam com `CHState = 1` (inativo, como na spec 001) e `CHDownloadRequired = 1`, e nenhum outro campo é alterado.
2. **Given** um CPF já inativo com `CHDownloadRequired = 1`, **When** é processado de novo, **Then** nenhuma linha é alterada, o resultado é `JA_INATIVO` (spec 001, FR-017) e a auditoria não é duplicada.
3. **Given** um CPF inativado antes desta mudança (`CHState = 1`, `CHDownloadRequired = 0`), **When** é processado, **Then** suas linhas passam a ter `CHDownloadRequired = 1`, nenhum outro campo é alterado e o status de auditoria é `JA_INATIVO`.

---

### Edge Cases

- **Falha ao gravar a cópia em `{FolderPathInter}/Processed`**: é tratada como falha transitória de arquivo; o arquivo não sai do fluxo (fica em `Output` ou na entrada como pendente) e a cópia é tentada de novo no ciclo seguinte, sem nova inativação.
- **Arquivo de mesmo nome já presente no destino** (`{FolderPathCondominio}/Input`, `Processed`, `Output`, `Fail`): conteúdo idêntico é tratado como já entregue/gravado (no-op); conteúdo diferente recebe nome único rastreável ao original — nenhum arquivo existente é sobrescrito (spec 001, FR-030).
- **Arquivo de falhas com o mesmo nome do original já existente em `Fail`** com conteúdo diferente (por exemplo, o mesmo nome de arquivo reenviado dias depois): o novo arquivo de falhas recebe nome único rastreável ao original; o anterior é preservado.
- **Arquivo com pendências transitórias**: os registros pendentes permanecem em `{raiz}/Input` para retentativa (spec 001, FR-029); somente os registros concluídos seguem o fluxo de sucesso.
- **Arquivo em `{FolderPathInter}/Output` não gerado pelo serviço**: não é entregue ao Condomínio; gera alerta, como na spec 001.
- **Pastas com nomes antigos contendo arquivos** (`Imput`, `Auditoria`, `Processados`, `Falhas`): o conteúdo é migrado para as pastas novas (FR-004); colisão de nome com conteúdo diferente gera nome único rastreável, sem sobrescrever; arquivos temporários (`.tmp`) de escritas interrompidas não são migrados.
- **Migração interrompida** (reinício, pasta bloqueada, sem permissão): é retomada no ciclo seguinte sem duplicar arquivos nem linhas de log; arquivos de entrada que ainda não foram migrados só são processados depois de chegarem a `Input`.
- **Arquivos já entregues em `{FolderPathInter}/Output` pela versão anterior** (onde `Output` era histórico permanente): não são entregues de novo ao Condomínio; são movidos para `{FolderPathInter}/Processed` (FR-004a).
- **Destino do Condomínio indisponível por muito tempo**: o arquivo permanece em `Output` e a entrega é tentada a cada ciclo, sem limite de ciclos; quando a entrega pendente mais antiga passar do limite de alerta (padrão 24 h, o mesmo de FR-042 da spec 001), o estado de saúde sinaliza `ENTREGA_PENDENTE_ACIMA_LIMITE` (FR-007a).
- **Falta de permissão para criar uma pasta**: é reportada como erro claro, sem derrubar o serviço; a instância não processa arquivos enquanto uma pasta obrigatória da própria raiz não existir (FR-002). A indisponibilidade de `{FolderPathCondominio}/Input` no Inter afeta apenas a entrega (FR-002a).

## Requirements *(mandatory)*

### Functional Requirements

#### Estrutura de pastas

- **FR-001** *(substitui FR-002 da spec 001 quanto ao nome da pasta de entrada)*: As pastas abaixo de `SharePointFolder:FolderPathInter` e `SharePointFolder:FolderPathCondominio` MUST usar os nomes `Input` (antes `Imput`), `Logs` (antes `Auditoria`), `Processed` (antes `Processados`), `Fail` (antes `Falhas`) e `Output`. Os nomes antigos MUST NOT ser usados em nenhum ponto do sistema, da documentação ou dos testes, exceto na migração de FR-004.
- **FR-002** *(substitui FR-031 da spec 001)*: Em cada ciclo, antes de processar arquivos, o sistema MUST criar as pastas obrigatórias **da própria raiz** que estiverem ausentes, sem apagar dados existentes:
  - instância `Inter`: `{FolderPathInter}/Input`, `Output`, `Processed`, `Logs` e `Fail`;
  - instância `Condominio`: `{FolderPathCondominio}/Input`, `Processed`, `Logs` e `Fail`.
  Falha ao criar uma dessas pastas impede o processamento de arquivos naquele ciclo.
- **FR-002a**: Na instância `Inter`, a pasta de destino `{FolderPathCondominio}/Input` MUST ser criada, quando ausente, somente no momento da entrega (FR-007). Falha ao criá-la ou acessá-la MUST ser tratada como falha transitória **da entrega**: MUST NOT impedir o processamento de `{FolderPathInter}/Input`, e os arquivos concluídos aguardam em `Output`.
- **FR-003**: Os logs contratuais MUST ser gravados em `{raiz}/Logs/log_inativacao.log` (auditoria) e `{raiz}/Fail/log_inativacao.log` (falhas), mantendo o layout de FR-019, FR-020 e FR-020a da spec 001.
- **FR-004**: Quando uma pasta com nome antigo existir sob a raiz da instância, o sistema MUST, no início do ciclo e antes de ler `Input`, migrar seu conteúdo:
  - `Imput` → `Input`: arquivos de entrada (inclusive partes pendentes) junto com seus `.traceparent`;
  - `Processados` → `Processed`;
  - `Falhas` → `Fail`: arquivos de falhas; o conteúdo de `Falhas/log_inativacao.log` MUST ser mesclado a `Fail/log_inativacao.log` (linhas antigas antes das novas);
  - `Auditoria/log_inativacao.log` MUST ser mesclado a `Logs/log_inativacao.log` (linhas antigas antes das novas).
  A migração MUST ser idempotente e segura contra interrupção: nenhum arquivo sobrescrito ou perdido, nenhuma linha de log duplicada ou alterada, e o progresso de entradas/partes já registrado no diário de estado MUST continuar válido após a mudança de pasta (nenhum registro reinativado ou reauditado). Cada pasta antiga MUST ser removida somente quando ficar vazia. O sistema MUST registrar no log técnico (sem CPF ou nome em claro) o que foi migrado, e falhas de migração MUST ser tratadas como transitórias e retentadas no ciclo seguinte.
- **FR-004a**: Na instância `Inter`, os arquivos que já estavam em `{FolderPathInter}/Output` antes desta mudança e constam no diário de estado como entregues ao Condomínio MUST ser movidos para `{FolderPathInter}/Processed`, sem nova entrega. Os ainda não entregues permanecem em `Output` e seguem FR-007.

#### Fluxo da instância Inter

- **FR-005**: A instância `Inter` MUST ler e processar somente os arquivos elegíveis de `{FolderPathInter}/Input`, com as regras de elegibilidade, leitura e validação da spec 001.
- **FR-006** *(substitui FR-028 e FR-032 a FR-035 da spec 001 para o Inter)*: Quando um arquivo for concluído, a parte com os registros concluídos com sucesso MUST, ao fim do mesmo ciclo:
  1. estar em `{FolderPathInter}/Processed` (histórico); e
  2. estar entregue em `{FolderPathCondominio}/Input` **e** ausente de `{FolderPathInter}/Output`; **ou**, se a entrega falhar ou não couber no orçamento do ciclo, estar em `{FolderPathInter}/Output`, que funciona como fila de saída.
  A entrega MUST NOT esperar a entrada do Inter ficar vazia, e o arquivo MUST NOT permanecer em `{FolderPathInter}/Input`. A ordem interna dos passos é decisão de design (research R-03), desde que nenhuma interrupção deixe o conteúdo fora de todas essas pastas.
- **FR-007**: Em todo ciclo da instância `Inter`, o sistema MUST tentar entregar em `{FolderPathCondominio}/Input` cada arquivo gerado pelo serviço que esteja em `{FolderPathInter}/Output`, sem limite de ciclos, até a entrega ser confirmada.
- **FR-007a**: Na instância `Inter`, quando a entrega pendente mais antiga tiver sido registrada (momento em que o arquivo entrou na fila `Output`) há mais de `Processamento:AlertaPendenciaHoras` (padrão 24 h), o estado de saúde MUST sinalizar o alerta `ENTREGA_PENDENTE_ACIMA_LIMITE`, mantendo as tentativas; o alerta MUST cessar no primeiro ciclo em que não houver entrega pendente acima do limite.
- **FR-008**: Depois de confirmar a integridade da cópia em `{FolderPathCondominio}/Input`, o sistema MUST remover o arquivo de `{FolderPathInter}/Output`. A confirmação da entrega MUST ser registrada de forma durável antes da remoção, de modo que um reinício entre os dois passos não gere segunda cópia no Condomínio.
- **FR-009**: A entrega ao Condomínio MUST tornar o arquivo visível em `{FolderPathCondominio}/Input` somente quando completo, junto com seu `.traceparent` (spec 001, FR-033 e FR-039), e MUST NOT entregar o mesmo conteúdo mais de uma vez.
- **FR-010**: Registros com falha definitiva MUST ser gravados em um arquivo em `{FolderPathInter}/Fail` com **o mesmo nome e extensão do arquivo original**, no formato original, contendo somente esses registros. Se já existir em `Fail` um arquivo com esse nome e conteúdo diferente, o novo arquivo MUST receber nome único rastreável ao original, sem sobrescrever o existente.
- **FR-011**: Pendências e falhas definitivas MUST NOT ser entregues ao Condomínio nem gravadas em `Processed`.

#### Fluxo da instância Condomínio

- **FR-012**: A instância `Condominio` MUST ler e processar somente os arquivos elegíveis de `{FolderPathCondominio}/Input`; a parte com os registros concluídos com sucesso MUST ser movida para `{FolderPathCondominio}/Processed`; e os registros com falha definitiva MUST ser gravados, com a regra de nome de FR-010, em `{FolderPathCondominio}/Fail`. A instância `Condominio` MUST NOT depender de acesso à raiz `FolderPathInter`.
- **FR-013**: A instância `Condominio` MUST NOT gravar em `Output` nem entregar arquivos a outro ambiente (mantém FR-036 da spec 001).

#### Atualização no WAccess

- **FR-014** *(substitui FR-014 da spec 001 quanto aos campos alterados)*: Ao inativar um CPF, o sistema MUST definir, em todas as linhas de `CHMain` cujo `IdNumber` seja igual ao CPF, `CHState = 1` (estado de inativação, inalterado em relação à spec 001) **e** `CHDownloadRequired = 1`, na mesma operação, sem alterar nenhum outro campo.
- **FR-015** *(complementa FR-017 da spec 001)*: Uma linha de `CHMain` MUST ser alterada somente se `CHState <> 1` ou `CHDownloadRequired <> 1`; linha já com `CHState = 1` e `CHDownloadRequired = 1` MUST NOT ser alterada. O status de auditoria MUST continuar baseado apenas no `CHState`: se todas as linhas do CPF já tinham `CHState = 1`, o resultado é `JA_INATIVO` (mesmo que alguma tenha recebido `CHDownloadRequired = 1` nesse processamento); caso contrário, `INATIVADO`. A auditoria continua sendo registrada uma única vez por CPF e arquivo.

### Key Entities *(include if feature involves data)*

- **Pasta de entrada (`Input`)**: onde chegam os arquivos a processar em cada instância; no Condomínio, também é o destino das entregas do Inter.
- **Fila de saída (`Output`, só Inter)**: arquivos concluídos no Inter que ainda não foram entregues ao Condomínio; fica vazia quando todas as entregas foram confirmadas.
- **Histórico de concluídos (`Processed`)**: histórico dos arquivos concluídos com sucesso em cada instância (no Inter, cópia; no Condomínio, destino final).
- **Arquivo de falhas (`Fail`)**: arquivo com o mesmo nome do original contendo somente os registros com falha definitiva, para correção e reenvio manual; a pasta também guarda o log de falhas.
- **Logs (`Logs`)**: log contratual de auditoria das inativações concluídas.
- **Usuário WAccess**: cadastro em `CHMain` localizado por `IdNumber`; a inativação altera `CHState` e `CHDownloadRequired`.

### Orçamento de Performance *(exigido pelo Princípio VI da constituição)*

Mantém-se o orçamento da spec 001 sem alteração (throughput, latência p95 por registro, duração de ciclo e volume máximo por arquivo). A entrega ao Condomínio no mesmo ciclo do processamento MUST caber no orçamento de tempo do ciclo (`IntervalSeconds`); o que não couber permanece em `Output` para o ciclo seguinte.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Em 100% das partidas com raízes vazias, todas as pastas obrigatórias da instância existem ao fim do primeiro ciclo.
- **SC-002**: 0 referências aos nomes antigos de pastas (`Imput`, `Auditoria`, `Processados`, `Falhas`) no comportamento do serviço, na documentação de operação e nos testes, exceto na migração de FR-004.
- **SC-002a**: Em 100% dos testes de migração (inclusive interrompida e retomada), o total de arquivos e de linhas de log após a migração é igual ao total antes dela, sem duplicatas, e nenhum registro já concluído é reinativado ou reauditado.
- **SC-003**: Com o destino disponível, 100% dos arquivos concluídos no Inter chegam a `{FolderPathCondominio}/Input` no mesmo ciclo em que foram processados, dentro do orçamento de tempo do ciclo (hoje só chegam num ciclo posterior sem novos arquivos de entrada).
- **SC-004**: Após o destino do Condomínio voltar a ficar disponível, 100% dos arquivos em `{FolderPathInter}/Output` são entregues e removidos de `Output` no primeiro ciclo seguinte; em 100 ciclos consecutivos com falhas e reinícios simulados, 0 entregas duplicadas e 0 arquivos perdidos.
- **SC-005**: 100% dos arquivos concluídos com sucesso no Inter têm cópia idêntica em `{FolderPathInter}/Processed`.
- **SC-006**: 100% dos arquivos com falha definitiva geram um arquivo em `Fail` com o mesmo nome do original (ou nome único rastreável, em caso de colisão) contendo exatamente os registros que falharam.
- **SC-007**: 100% dos CPFs inativados ficam com o estado de inativação e `CHDownloadRequired = 1` em todas as suas linhas, e 0 campos além desses dois são alterados, verificado por comparação do banco antes/depois.
- **SC-008**: O arquivo de teste depositado em `{FolderPathInter}/Input` é processado no primeiro ciclo após a implantação da mudança.

## Assumptions

- **Mapeamento dos nomes**: o pedido cita "Processado"; no sistema atual a pasta se chama `Processados`, e é ela que passa a ser `Processed`.
- **Migração das pastas antigas**: feita pelo próprio serviço (FR-004), sem passo manual. As linhas dos logs antigos são mescladas aos novos na ordem original, antes das linhas novas (as antigas são sempre anteriores); não há reordenação linha a linha por data. Na máquina de desenvolvimento, `Files/Condominio/Imput` contém um arquivo de teste e seu `.traceparent`, que serão migrados para `Files/Condominio/Input`.
- **"Processado com sucesso" em arquivo misto**: segue a segregação da spec 001 (FR-029) — só a parte com registros concluídos vai ao Condomínio e a `Processed`; falhas definitivas vão para `Fail`; pendências transitórias ficam em `Input`.
- **Ordem no Inter**: a cópia em `Processed` e a entrega ao Condomínio acontecem no mesmo ciclo; `Output` só contém arquivos com entrega pendente. O histórico permanente do Inter passa a ser `Processed` (antes, `Output`).
- **Nome do arquivo de falhas**: passa a ser o mesmo do original (antes, nome com sufixo); em colisão com conteúdo diferente, aplica-se nome único rastreável ao original, preservando a regra de nunca sobrescrever.
- **`CHDownloadRequired`**: coluna existente em `CHMain`, cujo valor `1` indica ao WAccess que o cadastro deve ser enviado aos equipamentos. Cadastros inativados antes desta mudança só recebem a marca de download se o CPF for reenviado em um arquivo (FR-015); não há varredura retroativa do banco.
- **Banco de testes local**: os testes podem alterar `CHState` e `CHDownloadRequired` no `W_ACCESS` local, como já autorizado na spec 001; a autorização não se estende a produção.
- **Sem novo processo**: as mudanças ficam dentro do mesmo Worker Service (Princípio I); nenhuma fila ou serviço novo é criado.
