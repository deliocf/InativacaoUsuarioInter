---

description: "Task list — Ajustes de Pastas, Fluxo de Arquivos e Atualização no WAccess"
---

# Tasks: Ajustes de Pastas, Fluxo de Arquivos e Atualização no WAccess

**Input**: Design documents from `specs/002-ajustes-pastas-fluxo/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md), [data-model.md](./data-model.md), [contracts/](./contracts/), [quickstart.md](./quickstart.md)

**Tests**: obrigatórios. A constituição (Princípios III e V) exige teste de contrato, de idempotência (dupla execução + crash/retomada) e de injeção de falhas para cada mutação nova ou alterada, escritos **antes** da implementação (red → green).

**Organization**: tarefas agrupadas por user story da spec. Caminhos relativos à raiz do repositório. Abreviações: `Svc/` = `src/InativacaoUsuarioInter.Service/`, `TContract/` = `tests/InativacaoUsuarioInter.Tests.Contract/`, `TInteg/` = `tests/InativacaoUsuarioInter.Tests.Integration/`, `TUnit/` = `tests/InativacaoUsuarioInter.Tests.Unit/`, `TComum/` = `tests/InativacaoUsuarioInter.Tests.Comum/`.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: pode rodar em paralelo (arquivos diferentes, sem dependência de tarefa incompleta)
- **[Story]**: user story da spec (US1–US4)

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: constantes de nomes e ajustes nos utilitários de teste usados por todas as histórias.

- [X] T001 Criar `Svc/Arquivos/NomesPastas.cs` com `internal static class NomesPastas` (constantes `Input = "Input"`, `Output = "Output"`, `Processed = "Processed"`, `Logs = "Logs"`, `Fail = "Fail"`, `NomeLog = "log_inativacao.log"`) e `internal static class NomesPastasLegado` (`Imput`, `Auditoria`, `Processados`, `Falhas`) — research R-08; únicos lugares do serviço onde esses literais podem aparecer.
- [X] T002 [P] Atualizar `TComum/AmbienteTeste.cs`: `Depositar…` passa a gravar em `{raiz}/Input`; adicionar helpers `PastaInput`, `PastaOutput`, `PastaProcessed`, `PastaLogs`, `PastaFail` (por raiz) e `CriarLegado(raiz, pastaAntiga, nome, conteudo)` para montar pastas antigas nos testes de migração.
- [X] T003 [P] Atualizar a réplica de `CHMain` em `TComum/SqlServerFixture.cs`: adicionar coluna `CHDownloadRequired bit NULL` (sem default, como no esquema real — data-model §5) e helper para semear linhas com `CHState`/`CHDownloadRequired` arbitrários (incluindo `NULL`); manter o trigger de teste.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: diário SQLite v3 e novas consultas de entrega, usados pela migração (US1) e pelo fluxo Inter (US2).

**⚠️ CRITICAL**: nenhuma user story começa antes desta fase.

- [X] T004 Teste (red) em `TInteg/Estado/ArmazenamentoEstadoTests.cs`: migração v3 aplicada sobre um diário v2 existente preserva as linhas de `entrega` e preenche `registrada_em` com o instante da migração; reaplicar é idempotente; `ObterEntregaAsync(hash)` devolve `{Estado, NomeOutput, NomeDestino}` ou `null`; `ObterEntregaPendenteMaisAntigaAsync()` devolve o menor `registrada_em` com `estado = 'NaoEntregue'` ou `null`.
- [X] T005 Adicionar o script v3 em `Svc/Estado/Migracoes.cs`: `ALTER TABLE entrega ADD COLUMN registrada_em TEXT NULL;` + `UPDATE entrega SET registrada_em = <agora> WHERE registrada_em IS NULL;` (data-model §2), garantindo que o `ALTER` não seja reexecutado (checar a coluna via `pragma_table_info` ou a versão já aplicada).
- [X] T006 Em `Svc/Estado/IArmazenamentoEstado.cs` e `Svc/Estado/ArmazenamentoEstadoSqlite.cs`: adicionar o record `EntregaRegistrada(string Hash, string Estado, string NomeOutput, string? NomeDestino)`, `ObterEntregaAsync`, `ObterEntregaPendenteMaisAntigaAsync`; `RegistrarParteAsync(..., registrarEntrega: true)` e `GarantirEntregaAsync` passam a gravar `registrada_em`.

**Checkpoint**: diário v3 pronto — T004 verde.

---

## Phase 3: User Story 1 — Pastas padronizadas, criadas e migradas automaticamente (Priority: P1) 🎯 MVP

**Goal**: o serviço usa `Input`/`Output`/`Processed`/`Logs`/`Fail`, cria as pastas a cada ciclo e migra o conteúdo das pastas antigas sem perda nem duplicação (FR-001–FR-004, SC-001, SC-002, SC-002a).

**Independent Test**: raízes vazias → pastas criadas no 1º ciclo; arquivo em `{raiz}/Input` processado; raiz com `Imput`/`Auditoria`/`Processados`/`Falhas` populadas → conteúdo migrado, logs mesclados, pastas antigas removidas quando vazias; interrupção em qualquer passo + retomada → mesmo estado final.

### Tests for User Story 1 (red first) ⚠️

- [X] T007 [P] [US1] Reescrever `TContract/EstruturaDiretorios/EstruturaTests.cs` conforme [contracts/estrutura-pastas.md](./contracts/estrutura-pastas.md): Inter cria `Input`, `Output`, `Processed`, `Logs`, `Fail` e **não** cria `{FolderPathCondominio}/Input` em `GarantirEstrutura` (FR-002a); Condomínio cria `Input`, `Processed`, `Logs`, `Fail` e **não** cria `Output`; `Logs`/`Fail` com `0700` em Unix; e um teste de varredura (SC-002) que falha se encontrar os nomes `Imput`, `Auditoria`, `Processados` ou `Falhas` como nome de pasta em: `src/InativacaoUsuarioInter.Service/**/*.cs` (exceto `Arquivos/NomesPastas.cs` e `Arquivos/MigradorLegado.cs`), `tests/**/*.cs` (exceto `TInteg/Migracao/**`, `TUnit/Arquivos/MesclaLogTests.cs` e o helper `CriarLegado` de `TComum/AmbienteTeste.cs`), `README.md` e `docs/**/*.md` (exceto a seção de migração/implantação). Use regex de nome de pasta (`[\\/"]Imput[\\/"]`, `"Auditoria"`, `/Auditoria/`, etc.) para não pegar `TipoLog.Auditoria`/`TipoLog.Falhas` nem textos de negócio.
- [X] T008 [P] [US1] Atualizar `TContract/Logs/LayoutLogTests.cs`: auditoria em `{raiz}/Logs/log_inativacao.log` e falhas em `{raiz}/Fail/log_inativacao.log`, layout inalterado (FR-003).
- [X] T009 [P] [US1] Criar `TUnit/Arquivos/MesclaLogTests.cs` para a mescla de logs (research R-06): destino inexistente → destino = antigo; destino existente → antigo + atual, nesta ordem, byte a byte; destino já começa com os bytes do antigo → nenhuma escrita e o antigo é removido; antigo vazio → só removido; arquivo maior que o buffer é processado em streaming.
- [X] T010 [P] [US1] Criar `TInteg/Migracao/MigracaoLegadoTests.cs` (US1 cenários 4–5): para Inter e Condomínio, raiz com `Imput` (arquivo + `.traceparent` + parte `__pendente-`), `Processados`, `Falhas` (arquivo de falhas + log) e `Auditoria` (log) → após 1 ciclo: arquivos nas pastas novas, entrada migrada processada **no mesmo ciclo**, logs mesclados, pastas antigas removidas; colisão de nome com hash igual → no-op e com hash diferente → `<base>__<h8>.<ext>`; `.tmp` em `Imput` permanece e a pasta não é removida; contagem de arquivos e de linhas antes = depois (SC-002a); registro já concluído antes da migração **não** é reinativado nem reauditado.
- [X] T011 [P] [US1] Acrescentar a `TInteg/InjecaoFalhas/CrashRetomadaTests.cs` cenários de crash da migração usando os novos pontos de falha (T013): após mover cada arquivo, após substituir o log de destino e antes de remover o log antigo → retomada no ciclo seguinte sem duplicar linhas nem arquivos; e pasta antiga sem permissão → erro classificado como transitório, serviço continua, migração retomada quando a permissão volta.

### Implementation for User Story 1

- [X] T012 [US1] Atualizar `Svc/Ciclo/PoliticasFluxo.cs`: `PastaEntrada` → `NomesPastas.Input`; substituir `PastaAuditoria`/`PastaFalhas` por `PastaLogs`/`PastaFail`; adicionar `string? PastaSaida` (Inter: `{Raiz}/Output`; Condomínio: `null`); `NomeConcluidos` = `NomesPastas.Processed` nas duas instâncias; `PastasObrigatorias` Inter = `[Input, Output, Processed, Logs, Fail]`, Condomínio = `[Input, Processed, Logs, Fail]`. Ajustar todos os usos de `PastaFalhas`/`PastaAuditoria` (`Svc/Arquivos/GerenciadorArquivos.cs`, `Svc/Saude/VerificadorPermissoes.cs`).
- [X] T013 [P] [US1] Em `Svc/Ciclo/IPontoFalha.cs`, adicionar `PontoFalha.AposMoverLegado`, `AposSubstituirLogLegado`, `AposRegistrarEntregue` (este último usado em US2).
- [X] T014 [US1] Em `Svc/Arquivos/GerenciadorArquivos.cs`: `GarantirEstruturaAsync` cria somente `politica.PastasObrigatorias` (pastas da própria raiz) usando `DiretoriosRestritos.Criar` para `Logs` e `Fail` — **não** criar `{RaizCondominio}/Input` aqui, porque falha neste método interrompe o ciclo (FR-002a; a criação remota fica em T028); `LimparTemporariosAsync` passa a limpar `.tmp` próprios em `Processed`, `Fail` e `Output` (quando houver), e as partes `__pendente-*.tmp` em `Input`.
- [X] T015 [P] [US1] Em `Svc/Registros/IRegistros.cs`, trocar o caminho dos logs para `Path.Combine(raiz, tipo == TipoLog.Auditoria ? NomesPastas.Logs : NomesPastas.Fail, NomesPastas.NomeLog)`.
- [X] T016 [P] [US1] Em `Svc/Saude/SondaDependencias.cs` e `Svc/Replicacao/ServicoReplicacao.cs`, trocar `"Imput"` por `NomesPastas.Input` (a sonda continua apenas **sondando** a disponibilidade de `{RaizCondominio}/Input` para o readiness, sem que a falha bloqueie o ciclo); em `Svc/Saude/VerificadorPermissoes.cs`, verificar `Logs` e `Fail`; atualizar comentários/mensagens com nomes antigos em `Svc/Entrada/DetectorElegiveis.cs`, `Svc/Ciclo/EstadoDependenciasCiclo.cs`, `Svc/Telemetria/Instrumentacao.cs` e `Svc/Registros/FormatadorLinhaLog.cs`.
- [X] T017 [US1] Criar `Svc/Arquivos/MigradorLegado.cs` (`internal sealed partial class MigradorLegado(IPoliticaFluxo, IArmazenamentoEstado, ProvedorPipelines, EstadoDependenciasCiclo, IPontoFalha, TimeProvider, ILogger<MigradorLegado>)`, método `MigrarAsync(OrcamentoCiclo?, CancellationToken)`) conforme research R-06 e a tabela de [contracts/estrutura-pastas.md](./contracts/estrutura-pastas.md): só age se existir pasta de `NomesPastasLegado` sob `politica.Raiz`; move `*.json`/`*.txt` com o `.traceparent` (`Imput`→`Input`), `Processados/*`→`Processed`, `Falhas/*`→`Fail` e `Auditoria/*`→`Logs`, aplicando a regra de colisão (`HashConteudo` + `NomeadorPartes.Colisao`; mesmo hash → remove a origem); mescla `log_inativacao.log` com detecção de prefixo, `<destino>.tmp` em streaming e `File.Replace`; **não** move nem apaga `.tmp`; remove a pasta antiga só se vazia; toda E/S via `pipelines.ExecutarAsync(Dependencias.PastaLocal, …)`; erro → `dependencias.MarcarIndisponivel` e retomada no próximo ciclo; span `arquivo.migrar_legado`; log estruturado `MigracaoLegado` (pasta de origem, destino, nome do arquivo; sem CPF/nome); chamar `pontoFalha.Atingido` nos pontos de T013.
- [X] T018 [US1] Registrar `MigradorLegado` em `Svc/ComposicaoServico.cs` e chamá-lo em `Svc/Ciclo/OrquestradorCiclo.cs` logo após `GarantirEstruturaAsync` e antes de `LimparTemporariosAsync`/recuperação/leitura de `Input` (plan.md, "Ordem do ciclo"); atualizar o comentário de ordem do ciclo.
- [X] T019 [P] [US1] Atualizar os literais de pastas nos testes existentes para os nomes novos (sem mudar o que cada teste afirma, exceto onde US2/US3 mudam o fluxo): `TInteg/FluxoCondominio/FluxoCondominioTests.cs`, `TInteg/FluxoInter/ElegibilidadeTests.cs`, `TInteg/Idempotencia/ExecucaoDuplaTests.cs`, `TInteg/InjecaoFalhas/FalhasArquivoTests.cs`, `TInteg/InjecaoFalhas/FalhasBancoTests.cs`, `TInteg/LocalDb/LocalDbOptInTests.cs`, `TInteg/Performance/CargaTests.cs`, `TInteg/Saude/AlertaPendenciaTests.cs`, `TInteg/Saude/ConvergenciaSaudeTests.cs`, `TInteg/Telemetria/CoberturaTelemetriaTests.cs`, `TInteg/Telemetria/CoberturaTelemetriaUS1Tests.cs`, `TUnit/Ciclo/RecuperacaoTests.cs`.

**Checkpoint**: T007–T011 verdes; o arquivo de teste em `Files/Inter/Input` é lido pelo serviço (SC-008 parcial).

---

## Phase 4: User Story 2 — Fluxo de arquivos na instância Inter (Priority: P1)

**Goal**: concluído no Inter → cópia em `Processed` + fila `Output` → entrega em `{FolderPathCondominio}/Input` no mesmo ciclo → removido de `Output` após `Entregue` durável; falhas em `Fail` com o nome do original; `Output` herdado vai para `Processed` sem reentrega (FR-004a, FR-005–FR-011, SC-003–SC-006).

**Independent Test**: com as duas raízes acessíveis, um arquivo em `{FolderPathInter}/Input` termina o ciclo em `{FolderPathCondominio}/Input` e `{FolderPathInter}/Processed`, com `Output` vazio; com o destino indisponível, fica em `Output` + `Processed` e é entregue/removido no primeiro ciclo após o destino voltar.

**Depends on**: US1 (nomes de pastas e `PastaSaida`).

### Tests for User Story 2 (red first) ⚠️

- [X] T020 [P] [US2] Reescrever `TContract/Replicacao/ReplicacaoContratoTests.cs` conforme [contracts/replicacao.md](./contracts/replicacao.md): destino `{FolderPathCondominio}/Input`; protocolo tmp → hash → `.traceparent` → rename → `Entregue` → remoção de `Output`; mesmo hash no destino → no-op + remoção de `Output`; hash diferente → `<base>__<h8>.<ext>`; nunca entrega `__pendente-`, arquivos de falhas, rejeitados ou desconhecidos.
- [X] T021 [P] [US2] Atualizar `TInteg/FluxoInter/FluxoInterTests.cs` (US2 cenários 1, 4, 5): integral → `Processed/<nome>` + `Condominio/Input/<nome>` idênticos e `Output` vazio no **mesmo** ciclo; misto → `Fail/<nome original>` só com o registro falho e `Processed`/Condomínio só com `__sucesso-`; tudo falho → somente `Fail/<nome original>`, nada em `Processed`, `Output` ou Condomínio.
- [X] T022 [P] [US2] Atualizar `TInteg/Replicacao/ReplicacaoTests.cs` e `TInteg/Replicacao/CopiaParcialTests.cs` (US2 cenários 2–3, FR-002a, FR-004a): destino indisponível — raiz do Condomínio inexistente/sem permissão e `{FolderPathCondominio}/Input` impossível de criar — → os arquivos de `{FolderPathInter}/Input` são processados no mesmo ciclo e o concluído fica em `Output` + `Processed`, nenhuma reinativação; destino volta → entregue e removido de `Output` no 1º ciclo; `Output` herdado com entrega `Entregue` → movido para `Processed` sem nova cópia; herdado `NaoEntregue` → entregue e movido para `Processed`; arquivo desconhecido em `Output` → intocado + `ARQUIVO_OUTPUT_DESCONHECIDO`; 100 ciclos com falhas intermitentes → 0 entregas duplicadas (SC-004).
- [X] T023 [P] [US2] Acrescentar a `TInteg/InjecaoFalhas/CrashRetomadaTests.cs`: crash em `AposRegistrarEntregue` (antes de remover de `Output`) → ciclo seguinte remove sem segunda cópia (US2 cenário 6); crash após gravar a cópia em `Output` e antes do diário → reprocessamento não duplica e não gera alerta persistente; falha ao gravar em `Processed` → arquivo permanece no fluxo (Input ou Output) e é retomado sem nova inativação.
- [X] T024 [P] [US2] Atualizar `TUnit/Arquivos/PartesTests.cs`: nome do arquivo de falhas = `<baseRaiz><ext>` (inclusive quando a origem é uma parte `__pendente-`); colisão → `NomeadorPartes.Colisao`; partes `__sucesso-`/`__pendente-` inalteradas (research R-05).
- [X] T025 [P] [US2] Atualizar `TContract/Saude/SaudeContratoTests.cs` e `TInteg/Saude/AlertaPendenciaTests.cs`: alerta `ENTREGA_PENDENTE_ACIMA_LIMITE` (FR-007a) ativo quando a entrega pendente mais antiga tem `registrada_em` anterior a `agora − Processamento:AlertaPendenciaHoras`, e inativo no 1º ciclo após a entrega (research R-07).

### Implementation for User Story 2

- [X] T026 [P] [US2] Em `Svc/Arquivos/NomeadorPartes.cs`, adicionar `ArquivoFalhas(string baseRaiz, string extensao) => baseRaiz + extensao` e remover o uso de `NomeadorPartes.Falhas` para o nome da parte de falhas.
- [X] T027 [US2] Em `Svc/Arquivos/GerenciadorArquivos.cs`, aplicar research R-03/R-05: `MoverIntegralAsync` (Inter) → (1) copia o original para `PastaSaida` via tmp+rename com `DestinoLivreAsync` (mesmo hash → no-op), (2) registra parte (`caminho_final` = destino em `Processed`) + entrega (`nome_output` = nome em `Output`) numa transação, (3) move o original e o `.traceparent` para `Processed`, (4) conclui no diário; no Condomínio (`PastaSaida == null`) mantém o move simples para `Processed`. `SegregarAsync` → parte `__sucesso-` gravada em `Processed` e, no Inter, também em `Output`, registrada com entrega; parte de falhas gravada em `Fail/<NomeadorPartes.ArquivoFalhas>` via `DestinoLivreAsync`. Manter os pontos `AposGravarPartes`/`AntesRemoverOriginal`.
- [X] T028 [US2] Em `Svc/Replicacao/ServicoReplicacao.cs`: origem = `politica.PastaSaida`; destino = `{RaizCondominio}/Input`, criado em `CopiarAsync` se ausente, dentro do pipeline `Replicacao` (falha → transitória da entrega, arquivo permanece em `Output` — FR-002a); após `RegistrarEntregaAsync`, chamar `pontoFalha.Atingido(PontoFalha.AposRegistrarEntregue)` e limpar `Output` (research R-04: se `Processed` tem arquivo com o mesmo hash — mesmo nome ou nome de colisão — remove `Output/<nome>` e `.traceparent`; senão move para `Processed` com regra de colisão); `ReconciliarAsync` passa a usar `ObterEntregaAsync`: `Entregue` → mesma limpeza; parte `Integral`/`Sucesso` sem entrega ou `NaoEntregue` → `GarantirEntregaAsync`; sem registro → desconhecido. Span `replicacao.limpar_output`. Toda E/S via pipelines `PastaLocal`/`Replicacao`.
- [X] T029 [P] [US2] Em `Svc/Saude/PublicadorSaudeArquivo.cs`, adicionar `CodigosAlerta.EntregaPendenteAcimaLimite = "ENTREGA_PENDENTE_ACIMA_LIMITE"` (somente Inter), calculado com `ObterEntregaPendenteMaisAntigaAsync` e `Processamento:AlertaPendenciaHoras`; atualizar `specs/001-inativacao-usuarios-waccess/contracts/saude.md` com o novo código (nota "adicionado pela feature 002", FR-007a; referenciado em plan.md › Documentation).
- [X] T030 [US2] Em `Svc/Ciclo/OrquestradorCiclo.cs`, ajustar o comentário/referência da replicação (entrega no mesmo ciclo, sem a condição de "nenhum arquivo novo"; contrato v2) e confirmar que a entrega roda sempre após o laço de arquivos e respeita o orçamento do ciclo.

**Checkpoint**: T020–T025 verdes; US1 e US2 funcionam juntas.

---

## Phase 5: User Story 4 — Inativação marca download para os equipamentos (Priority: P1)

**Goal**: o UPDATE grava `CHState = 1` e `CHDownloadRequired = 1`; CPFs já inativos sem a marca recebem a marca e continuam `JA_INATIVO` (FR-014, FR-015, SC-007).

**Independent Test**: no SQL Server de teste (e no `W_ACCESS` local, opt-in), inativar CPF ativo → todas as linhas com `CHState = 1` e `CHDownloadRequired = 1`, nenhuma outra coluna alterada; CPF com `CHState = 1` e `CHDownloadRequired` `NULL`/`0` → marca gravada, auditoria `JA_INATIVO`.

**Independente de US1–US3** (pode ser feita em paralelo após a Phase 2).

### Tests for User Story 4 (red first) ⚠️

- [X] T031 [P] [US4] Atualizar `TContract/WAccess/InativacaoWAccessTests.cs` conforme [contracts/waccess-inativacao.md](./contracts/waccess-inativacao.md): texto SQL do passo B com `SET CHState = 1, CHDownloadRequired = 1`, predicado `CHState <> 1 OR CHDownloadRequired IS NULL OR CHDownloadRequired = 0`, `OUTPUT inserted.CHID, deleted.CHState INTO @afetados`, três colunas de resultado (`Inativados`, `SomenteDownload`, `Existentes`); `@Cpf` `VarChar(50)`; contra a réplica do `SqlServerFixture`: linhas com `CHDownloadRequired` `NULL`, `0` e `1` × `CHState` `0`, `1`, `2`, `8` → só as do predicado mudam e só nas duas colunas (comparação antes/depois de todas as colunas).
- [X] T032 [P] [US4] Atualizar `TInteg/Idempotencia/ExecucaoDuplaTests.cs` e `TInteg/InjecaoFalhas/CrashRetomadaTests.cs`: passo B executado duas vezes → segunda afeta 0 linhas e o resultado é igual; crash em `AposIntencao` com estado anterior `Inativo` → retomada executa o passo B, grava a marca e audita `JA_INATIVO` uma única vez.
- [X] T033 [P] [US4] Atualizar `TInteg/LocalDb/LocalDbOptInTests.cs` (CA-20, opt-in `WACCESS_TESTE_LOCALDB=1`): salvar `CHState` **e** `CHDownloadRequired` originais, inativar, conferir as duas colunas e restaurar ambas ao final.
- [X] T034 [P] [US4] Atualizar `TInteg/Telemetria/CoberturaTelemetriaTests.cs`: span `waccess.inativar` com `waccess.linhas_afetadas` e `waccess.linhas_marcadas_download`, sem CPF em claro.

### Implementation for User Story 4

- [X] T035 [US4] Em `Svc/Inativacao/RepositorioWAccessSql.cs`: novo `SqlInativacao` idêntico ao do contrato; `ResultadoInativacao(int Inativados, int SomenteDownload, int Existentes)`; leitura das três colunas; tag `waccess.linhas_marcadas_download` no span; atualizar o comentário de `InativarAsync` ("executado para `Ativo` ou `Inativo`").
- [X] T036 [P] [US4] Em `Svc/Telemetria/Instrumentacao.cs`, adicionar `Atributos.LinhasMarcadasDownload = "waccess.linhas_marcadas_download"`.
- [X] T037 [US4] Em `Svc/Ciclo/ProcessadorArquivo.cs` (≈ linha 206): executar `repositorio.InativarAsync` quando `anterior is EstadoAnterior.Ativo or EstadoAnterior.Inativo`; manter `ClassificacaoWAccess.Classificar` (`Inativo` → `JaInativo` independentemente do passo B); garantir que o aviso `CpfComMultiplosCadastros` continue usando `Inativados` (não `SomenteDownload`).

**Checkpoint**: T031–T034 verdes; SC-007 verificável.

---

## Phase 6: User Story 3 — Fluxo de arquivos na instância Condomínio (Priority: P2)

**Goal**: Condomínio lê `Input`, move concluídos para `Processed`, grava falhas em `{FolderPathCondominio}/Fail` com o nome do original e não depende de `FolderPathInter` (FR-012, FR-013).

**Independent Test**: instância `Condominio` com `FolderPathInter` ausente; arquivos depositados em `{FolderPathCondominio}/Input` → banco, `Logs`, `Processed` e `Fail` do Condomínio conferidos; nada em `Output`.

**Depends on**: US1 (pastas) e T026/T027 de US2 (nome do arquivo de falhas, ramo `PastaSaida == null`).

### Tests for User Story 3 (red first) ⚠️

- [X] T038 [P] [US3] Atualizar `TInteg/FluxoCondominio/FluxoCondominioTests.cs` (US3 cenários 1–3): concluído → `Processed`; misto → `{FolderPathCondominio}/Fail/<nome original>` só com os registros falhos e `Processed` só com `__sucesso-`; `.traceparent` movido junto e span correlacionado; configuração sem `SharePointFolder:FolderPathInter` funciona e nenhuma pasta é criada fora de `FolderPathCondominio`; `Output` não é criado.
- [X] T039 [P] [US3] Atualizar `TInteg/PontaAPonta/InterCondominioTests.cs`: Inter e Condomínio em sequência sobre as mesmas raízes → arquivo do Inter entregue em `Condominio/Input`, processado e movido para `Condominio/Processed`; trace correlacionado; falhas de cada instância na própria `Fail`.

### Implementation for User Story 3

- [X] T040 [US3] Garantir que a instância `Condominio` não dependa de `FolderPathInter` (FR-012): (a) em `Svc/Configuracao/ValidadoresConfiguracao.cs`, `SharePointFolder:FolderPathInter` só é obrigatório quando `Settings:Instancia = Inter` (se hoje for exigido para o Condomínio, remover a exigência e cobrir em `TUnit/Configuracao/ValidacaoConfiguracaoTests.cs`); (b) em `Svc/Ciclo/PoliticasFluxo.cs`, `PoliticaFluxoCondominio` usa apenas `RaizCondominio` e `PastaSaida = null`; (c) buscar usos de `RaizInter` em `Svc/**` (`Saude/SondaDependencias.cs`, `Saude/VerificadorPermissoes.cs`, `Registros/IRegistros.cs`, `Configuracao/ConfiguracaoServico.cs`) e confirmar que nenhum é executado quando a instância é `Condominio`; (d) T038 verde.

**Checkpoint**: todas as user stories verdes de forma independente.

---

## Phase 7: Polish & Cross-Cutting Concerns

- [X] T041 [P] Atualizar `README.md` e `docs/operacao.md`: nomes de pastas, papel de `Output` (fila) × `Processed` (histórico), arquivo de falhas com nome original, `CHDownloadRequired = 1`, alerta `ENTREGA_PENDENTE_ACIMA_LIMITE`, migração automática e a **ordem obrigatória de implantação: Condomínio primeiro (ou na mesma janela), depois Inter** — com a explicação de que Inter novo + Condomínio antigo deixa entregas paradas em `Condominio/Input` (plan.md › Riscos).
- [X] T042 [P] Inserir no topo de `specs/001-inativacao-usuarios-waccess/contracts/replicacao.md`, `arquivos-entrada.md`, `logs-auditoria-falhas.md` e `waccess-inativacao.md` uma nota "Alterado/substituído pela feature 002 — ver `specs/002-ajustes-pastas-fluxo/contracts/`".
- [X] T043 Rodar `TInteg/Performance/CargaTests.cs` com o volume máximo da 001 e comparar com o orçamento (Princípio VI; regressão > 20% bloqueia).
- [X] T044 Executar as três suítes (`dotnet test` Unit, Contract, Integration) e o opt-in `LocalDb`; confirmar zero warnings novos no build e que o teste de varredura de nomes antigos da T007 (código, testes e docs — SC-002) está verde após T041.
- [X] T045 Executar o cenário manual de [quickstart.md](./quickstart.md) §2 com `Files/Inter/Input/20261112111111.txt` e o legado `Files/Condominio/Imput` (SC-008), registrando o resultado no PR.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)** → **Foundational (Phase 2)** → user stories.
- **US1 (Phase 3)**: depende da Phase 2. Base de nomes de pastas para US2 e US3.
- **US2 (Phase 4)**: depende de US1.
- **US4 (Phase 5)**: depende só da Phase 2 (e de T003); pode correr em paralelo com US1/US2.
- **US3 (Phase 6)**: depende de US1 e de T026/T027 (US2).
- **Polish (Phase 7)**: depois de todas as histórias.

```text
Setup ─► Foundational ─┬─► US1 ─► US2 ─► US3 ─┐
                       └─► US4 ───────────────┴─► Polish
```

### Within Each User Story

- Testes primeiro e falhando (red) → implementação → testes verdes.
- `PoliticasFluxo` (T012) antes de `GerenciadorArquivos` (T014) e `MigradorLegado` (T017); T017 antes de T018.
- T026 antes de T027; T027 antes de T028.
- T035/T036 antes de T037.

### Parallel Opportunities

- Setup: T002 ∥ T003.
- US1: T007 ∥ T008 ∥ T009 ∥ T010 ∥ T011; depois T013 ∥ T015 ∥ T016 (após T012); T019 ∥ qualquer tarefa de implementação de US1.
- US2: T020 ∥ T021 ∥ T022 ∥ T023 ∥ T024 ∥ T025; T026 ∥ T029.
- US4 inteira em paralelo com US1/US2 (arquivos distintos, exceto `CrashRetomadaTests.cs` e `CoberturaTelemetriaTests.cs` — coordenar as edições nesses dois arquivos).
- Polish: T041 ∥ T042.

### Parallel Example: User Story 1

```text
Task: "T007 Reescrever TContract/EstruturaDiretorios/EstruturaTests.cs"
Task: "T008 Atualizar TContract/Logs/LayoutLogTests.cs"
Task: "T009 Criar TUnit/Arquivos/MesclaLogTests.cs"
Task: "T010 Criar TInteg/Migracao/MigracaoLegadoTests.cs"
```

### Parallel Example: User Story 4 (ao lado de US1)

```text
Task: "T031 Atualizar TContract/WAccess/InativacaoWAccessTests.cs"
Task: "T033 Atualizar TInteg/LocalDb/LocalDbOptInTests.cs"
Task: "T036 Adicionar Atributos.LinhasMarcadasDownload em Svc/Telemetria/Instrumentacao.cs"
```

---

## Implementation Strategy

### MVP First

1. Phase 1 + Phase 2.
2. **US1** (pastas novas + migração) → o arquivo de teste em `Files/Inter/Input` passa a ser lido. Validar e parar se necessário.
3. **US4** (pode vir antes de US2, por ser independente e de alto valor: sem a marca, o acesso não é revogado nos equipamentos).

### Incremental Delivery

1. US1 → valida → entrega (serviço já usa os nomes novos e migra o legado).
2. US4 → valida → entrega (marca de download).
3. US2 → valida → entrega (novo fluxo Inter, `Output` como fila).
4. US3 → valida → entrega (Condomínio).
5. Polish.

**Atenção**: US1 sem US2 mantém o Inter replicando com a lógica antiga, porém com os nomes novos — `Processed` passa a ser o histórico e `Output` ainda não é esvaziado até US2. Não implantar em produção entre US1 e US2; entregar US1+US2 juntos.

---

## Notes

- [P] = arquivos diferentes, sem dependência pendente.
- Toda mutação nova (migração, limpeza de `Output`, passo B para `Inativo`) tem teste de dupla execução e de crash/retomada (Princípio III).
- Nenhum CPF ou nome em logs técnicos, spans ou métricas (FR-038 da 001).
- Commit após cada tarefa ou grupo lógico.
