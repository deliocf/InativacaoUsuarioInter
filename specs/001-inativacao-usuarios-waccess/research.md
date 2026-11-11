# Research: Inativação e Replicação de Usuários WAccess

**Feature**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md) | **Data**: 2026-11-11

Cada item segue o formato **Decisão / Justificativa / Alternativas consideradas**. Os itens R-01 e R-02 vêm de inspeção **somente leitura** do banco local de testes `W_ACCESS` (consultas agregadas, sem CPFs) e do código existente em `src/`.

---

## R-01 — Esquema real de `CHMain` (evidência coletada)

| Item | Valor observado |
| --- | --- |
| `IdNumber` | `varchar(50)`, **NULL permitido**, sem restrição de unicidade |
| `CHState` | `smallint NOT NULL`; valores presentes: `0`, `1`, `2`, `8` |
| Chave primária | `PK_CHMain` em **`CHID`** (`int`), não em `IdNumber` |
| Índices envolvendo `IdNumber` | `IX_CHMain_CHType_IdNumber (CHType, IdNumber)` — `IdNumber` é a 2ª coluna |
| Triggers | `wtr_CHMainChangeCHState`, `wtr_CHMainDeleteCHID` (definição não legível pelo usuário de testes) |
| Volume | 26.075 linhas; 20.898 com `CHState = 1` |
| Formato de `IdNumber` | 25.368 com 11 dígitos; 1 pontuado (`000.000.000-00`); 9 em outros formatos; 697 nulos/vazios |
| Duplicidade | **1 `IdNumber` presente em 2 linhas** |

**Decisão**: o parâmetro `@Cpf` é enviado como `VarChar(50)`, para evitar a conversão implícita `nvarchar → varchar`, que impede o uso de índice e quebra o orçamento de latência. A busca é feita por igualdade exata com o CPF normalizado (11 dígitos), conforme o requisito literal `WHERE IdNumber = @CPF`.

**Justificativa**: com o tipo do parâmetro igual ao da coluna, o otimizador pode varrer o índice `IX_CHMain_CHType_IdNumber` (≈26 mil entradas estreitas, poucos ms) em vez de varrer a tabela.

**Alternativas consideradas**:
- Buscar também por `UnformattedIdNumber` ou normalizar `IdNumber` no SQL (`REPLACE`). Rejeitado: muda a regra de negócio recebida e torna o predicado não indexável. Registrado como risco: o único `IdNumber` pontuado do banco de testes resultará em `CPF_NAO_ENCONTRADO`.
- Criar um índice em `IdNumber`. Rejeitado: alterar o esquema do WAccess está fora do escopo (spec, Assumptions). Ver Complexity Tracking.

## R-02 — Divergência: unicidade do CPF (Clarification Q2 × dados reais)

A Clarification de 2026-11-11 registrou que "`CHMain.IdNumber` é chave primária e não se repete". O esquema mostra que **não há restrição de unicidade**, e o banco de testes contém **1 CPF em 2 cadastros**.

**Status**: a spec foi corrigida em 2026-11-11 (Clarifications, FR-014, Key Entities, Edge Cases e Assumptions) para refletir o esquema real. A regra abaixo continua **pendente de confirmação do negócio**.

**Decisão (padrão adotado até confirmação)**: aplicar a regra literal do requisito original (`UPDATE … WHERE IdNumber = @CPF`), que inativa **todas** as linhas com aquele CPF. A auditoria continua com uma única linha por CPF, e o layout do contrato não muda. A quantidade de linhas afetadas vai para a telemetria técnica (`waccess.linhas_afetadas`), e quando for maior que 1 o serviço registra um aviso estruturado `CpfComMultiplosCadastros`.

**Justificativa**: (a) é o comportamento literal do requisito; (b) o objetivo de negócio é revogar o acesso daquela pessoa; (c) não exige nova causa no log de Falhas nem mudança no contrato fixo. A divergência fica destacada para o responsável decidir se prefere bloquear o caso. Basta trocar a regra no `RepositorioWAccess` (ponto único).

**Alternativas consideradas**: rejeitar com nova causa `CPF_DUPLICADO_WACCESS` (mudaria o contrato de log, aprovado na Q4) e reverter a transação quando houver mais de uma linha afetada (deixaria um acesso que devia ser revogado ativo).

## R-03 — Comando de inativação e contagem de linhas com trigger presente

*(Revisado em 2026-11-11 pela análise H1: o estado anterior é lido e gravado no diário **antes** da alteração, para que a classificação nunca dependa de adivinhar se a mudança foi feita pela execução atual.)*

**Decisão**: duas idas ao banco, ambas parametrizadas.

**Passo A — pré-leitura** (sem transação explícita):

```sql
SELECT COUNT(*) AS Existentes,
       SUM(CASE WHEN CHState <> 1 THEN 1 ELSE 0 END) AS Ativos
FROM dbo.CHMain WHERE IdNumber = @Cpf;
```

`estado_anterior` = `Inexistente` (`Existentes = 0`), `Inativo` (`Ativos = 0`) ou `Ativo` (`Ativos > 0`). Esse valor é **gravado no diário junto com a intenção**, numa única transação SQLite, antes do passo B.

**Passo B — alteração** (somente se `estado_anterior = Ativo`; transação `READ COMMITTED`):

```sql
DECLARE @afetados TABLE (CHID int);
UPDATE dbo.CHMain SET CHState = 1
  OUTPUT inserted.CHID INTO @afetados
  WHERE IdNumber = @Cpf AND CHState <> 1;
SELECT (SELECT COUNT(*) FROM @afetados)                      AS Inativados,
       (SELECT COUNT(*) FROM dbo.CHMain WHERE IdNumber = @Cpf) AS Existentes;
```

**Classificação** (vale igualmente para a execução normal, para um retry depois de timeout e para a recuperação após crash):

| `estado_anterior` | Passo B | Resultado |
| --- | --- | --- |
| `Inexistente` | não executado | `CPF_NAO_ENCONTRADO` |
| `Inativo` | não executado | `JA_INATIVO` |
| `Ativo` | `Existentes ≥ 1` (com `Inativados` 0 ou mais) | `INATIVADO` |
| `Ativo` | `Existentes = 0` (cadastro removido entre A e B) | `CPF_NAO_ENCONTRADO` |

Com `estado_anterior = Ativo`, `Inativados = 0` só significa que uma execução anterior desta mesma intenção (interrompida ou com a resposta perdida) já aplicou a mudança. Por isso o resultado continua `INATIVADO`, sem ambiguidade.

**Corrida aceita**: se outro sistema inativar o usuário entre A e B, o resultado é `INATIVADO` e não `JA_INATIVO`. A janela é de milissegundos e o estado final no banco está correto.

**Justificativa**:
- A transição condicional (`AND CHState <> 1`) é exigida pelo Princípio III e evita disparar o trigger de mudança de estado sem necessidade.
- `OUTPUT … INTO` é obrigatório: `OUTPUT` sem `INTO` em tabela com trigger habilitado gera erro 334. Além disso, o contador de linhas do driver pode somar as linhas alteradas pelo trigger, o que tornaria a classificação incorreta.
- A existência é verificada na mesma ida ao banco (FR-016): um comando sem erro não prova que o usuário existia.
- Linhas com `CHState` 2 ou 8 passam a 1. É o efeito da regra literal "SET CHState = 1"; nenhum outro estado é interpretado.

- A pré-leitura com `estado_anterior` gravado no diário elimina a ambiguidade de classificação na recuperação (H1), ao custo de uma ida extra ao banco, que cabe com folga no p95 ≤ 500 ms.

**Alternativas consideradas**: um único lote com UPDATE + contagem, inferindo o estado anterior pela intenção (versão anterior desta decisão; rejeitada porque a intenção existe antes de **toda** execução e, na recuperação, não distingue "já estava inativo" de "inativado pela execução interrompida"); `ExecuteNonQuery` usando o retorno (pode incluir as linhas do trigger); `UPDATE` incondicional (dispara o trigger em usuário já inativo e perde a distinção `JA_INATIVO`).

## R-04 — Acesso a dados

**Decisão**: `Microsoft.Data.SqlClient` direto, com `SqlCommand` parametrizado, sem ORM. `ConnectTimeout` e `CommandTimeout` vêm da configuração. Um `SqlConnection` é aberto por arquivo e reutilizado entre os registros dele, com o pool padrão.

**Justificativa**: é uma única instrução; um ORM não traz ganho e esconde o controle de tipo de parâmetro (R-01).

**Alternativas**: Dapper (dependência extra para um único comando); EF Core (pesado, exige um modelo da tabela de terceiros).

## R-05 — Classificação de erros (Princípio II)

**Decisão**: a falha **definitiva** se restringe a causas atribuíveis ao **dado** (`CPF_NAO_ENCONTRADO`, `DADO_INVALIDO`, `ARQUIVO_INVALIDO`, `ARQUIVO_EXCEDE_LIMITE`). **Todo erro de infraestrutura** (banco, rede, compartilhamento, permissão, login) é **transitório**: o registro continua pendente e o estado de saúde é sinalizado.

| Origem | Exemplos | Classe |
| --- | --- | --- |
| `SqlException` de conectividade/timeout | -2, 53, 233, 4060, 10053, 10054, 10060, 11001, 40613, 1205 (deadlock), 18456 (login) | Transitória |
| `SqlException` de objeto/permissão | 208 (objeto inexistente), 229/230 (permissão) | Transitória + alerta de configuração (nunca culpar o registro) |
| `TimeoutException`, `TaskCanceledException` por timeout | — | Transitória |
| `IOException` de compartilhamento/lock (HResult 32, 33), caminho de rede indisponível (53, 64, 67) | — | Transitória |
| `UnauthorizedAccessException` em pasta | — | Transitória + alerta |
| JSON inválido, raiz que não é array, UTF-8 inválido, arquivo vazio | — | Definitiva (`ARQUIVO_INVALIDO`) |
| Arquivo > 1.000 registros ou > 1 MB | — | Definitiva (`ARQUIVO_EXCEDE_LIMITE`) |

**Justificativa**: um erro de login ou de permissão marcado como falha definitiva faria todos os registros irem para `Falhas` por uma causa que não é deles, o que é perda silenciosa de trabalho. A spec (FR-023/FR-024) e o Princípio II exigem preservar o trabalho pendente.

**Alternativas**: tratar 208/229 como definitivos (rejeitado pelo motivo acima).

## R-06 — Políticas de resiliência

**Decisão**: `Microsoft.Extensions.Resilience` (Polly v8), com um `ResiliencePipeline` nomeado por dependência: `waccess`, `pasta-local`, `pasta-replicacao`. Cada pipeline combina timeout, retry e circuit breaker, todos configurados em `Resiliencia:<dependencia>:*`.

| Parâmetro | Default adotado | Default da constituição | Motivo da diferença |
| --- | --- | --- | --- |
| Timeout de conexão SQL | 15 s | 15 s | — |
| Timeout de comando SQL | 30 s | 30 s | — |
| Timeout de operação de arquivo remoto | 30 s | 30 s | — |
| Tentativas por operação no ciclo | **3** | 5 | O intervalo mínimo é 10 s; a soma das esperas (≤ 1 + 2 + 4 s com jitter completo) precisa caber nele (Princípio II, "orçamento por ciclo") |
| Backoff | exponencial, base 1 s, teto **4 s**, jitter completo | base 1 s, teto 60 s | Idem |
| Circuit breaker | ≥ 50% de falhas em 30 s, **mínimo de 3 chamadas**, meia-abertura após 60 s | mínimo de 10 chamadas | Com média de 3 registros por arquivo, um mínimo de 10 quase nunca seria atingido |

**Orçamento de retentativas por ciclo** (Princípio II, "a soma de tentativas MUST caber no intervalo do ciclo"; revisado pela análise C1): cada ciclo cria um `OrcamentoCiclo` igual a `IntervalSeconds`. Todo o tempo gasto em **retentativas** (a duração de cada nova tentativa e as esperas entre elas) é descontado desse orçamento. O `ShouldHandle` do retry, que recebe o orçamento via `ResilienceContext` (`ResiliencePropertyKey<OrcamentoCiclo>`), só autoriza nova tentativa se `restante ≥ próximaEspera + timeoutDaTentativa`. Esgotado o orçamento, a operação falha como transitória, a dependência é marcada indisponível pelo resto do ciclo e os registros restantes ficam pendentes. Assim a soma das tentativas extras de um ciclo nunca passa de `IntervalSeconds`, para qualquer valor configurado de timeout. A 1ª tentativa de cada operação não consome orçamento, mas tem timeout próprio; depois da 1ª falha transitória esgotada, o curto-circuito abaixo impede que ela se repita para os registros seguintes.

**Efeito no tempo de recuperação** (SC-004, análise M1): o circuito não é reiniciado a cada ciclo. Depois de aberto, ele só deixa passar uma chamada de teste (meia-abertura) quando `PausaSegundos` termina. Por isso as pendências são concluídas no primeiro ciclo iniciado após o fim da pausa, em até `PausaSegundos + IntervalSeconds` + duração do ciclo depois do restabelecimento do banco, e não necessariamente no ciclo imediatamente seguinte.

Além disso, existe uma **curto-circuitagem por ciclo**: quando um registro esgota as tentativas por falha transitória do WAccess, a dependência é considerada indisponível **até o fim do ciclo**. Os registros seguintes ficam pendentes sem nova tentativa, o que evita ciclos de vários minutos durante uma queda. Entre ciclos não há limite (FR-024), e há alerta após 24 h (FR-042).

**Alternativas**: laços `try/catch` próprios (proibidos pelo Princípio II); manter 5 tentativas com teto de 60 s (estoura o ciclo de 10 s).

## R-07 — Armazenamento durável de progresso e de replicação

**Decisão**: **SQLite local** (`Microsoft.Data.Sqlite`) em `Processamento:DiretorioEstado`. O padrão é `%ProgramData%\InativacaoUsuarioInter\<Instancia>\estado.db`, em `journal_mode=WAL` e `synchronous=FULL`. Somente o módulo `Estado` acessa o arquivo, por meio da interface `IArmazenamentoEstado`.

**Justificativa**: o SQLite oferece transações atômicas para gravar "resultado + linha de auditoria" juntos, sobrevive a falhas de energia e permite as consultas que o serviço precisa ("pendência mais antiga", "arquivos de Output não entregues"). Fica em disco local, porque SQLite em compartilhamento de rede não é confiável.

**Alternativas**:
- Arquivos JSON sidecar por arquivo: sem transação; difícil consultar a idade das pendências.
- Tabela no banco WAccess: proibido mudar o esquema.
- SQL Server LocalDB próprio: dependência pesada no servidor de produção.

## R-08 — Idempotência e protocolo de recuperação (Princípio III)

**Decisão**:
- **Hash de conteúdo**: SHA-256 dos bytes do arquivo. Um arquivo derivado (parte pendente) guarda o `HashRaiz` do original.
- **Resolução do `HashRaiz` antes da validação** (revisado pela análise H4): depois de calcular o hash de um arquivo lido em `Imput`, o leitor consulta o diário (`parte_gerada.hash` → `hash_origem` → `arquivo.hash_raiz`). Se o hash pertence a uma parte `__pendente-` conhecida, `HashRaiz` é o do original; caso contrário, `HashRaiz = Hash`. **Só depois** as chaves de idempotência são calculadas. Assim os registros pendentes mantêm a mesma chave, o mesmo `pendente_desde` (base do alerta de 24 h) e o mesmo histórico de tentativas entre partes sucessivas.
- **Chave de idempotência do registro**: `SHA-256(instancia | hashRaiz | cpfNormalizado)`, em hex. É determinística e nunca inclui horário nem GUID.
- **Diário por registro** (cada passo é uma transação SQLite):
  1. `Pendente`, registrado ao validar o arquivo.
  2. Pré-leitura no WAccess (R-03, passo A); depois `EmExecucao` + `estado_anterior`, gravados juntos **antes** de qualquer alteração.
  3. Alteração (passo B, só se `Ativo`); resultado classificado pela tabela de R-03 + **linha de auditoria ou falha pré-formatada** (o carimbo de horário é fixado aqui), gravados juntos.
  4. Linha anexada ao log, depois `AuditoriaGravada = 1`.
- **Retomada após crash**:
  - `Pendente` (crash antes do passo 2): recomeça do passo A, porque nada foi alterado.
  - `EmExecucao` com `estado_anterior` gravado: se `Ativo`, reexecuta o passo B (o UPDATE condicional é idempotente) e classifica pela tabela de R-03, ou seja, `INATIVADO` mesmo que `Inativados = 0`; se `Inativo` ou `Inexistente`, grava diretamente `JA_INATIVO` ou `CPF_NAO_ENCONTRADO`. **Nunca** se infere o estado anterior pela simples existência da intenção.
  - Resultado gravado com `AuditoriaGravada = 0`: o serviço procura a linha **exata** pré-formatada no log. Se ela já está lá, só marca a flag; se não está, anexa a linha e marca. Isso garante exatamente uma linha de auditoria por inativação (FR-021).
- **Arquivo já conhecido** (mesmo hash concluído) que reaparece na entrada: é tratado como no-op e movido para o destino de concluídos; se lá já existe um arquivo com o mesmo hash, o duplicado é removido da entrada. Nada é reinativado, reauditado ou replicado.

**Alternativas**: verificar duplicidade pelo conteúdo do log sem diário (caro e ambíguo); incluir a chave de idempotência na linha de log (mudaria o contrato aprovado).

## R-09 — Detecção de arquivo completo (upload ou cópia em andamento)

**Decisão**: um arquivo da entrada só é elegível quando (a) a extensão é `.json` ou `.txt` (sem diferenciar maiúsculas); (b) `LastWriteTimeUtc` tem mais de `Processamento:EstabilizacaoSegundos` (padrão 5 s); e (c) abre com `FileShare.None`. A instância Inter grava as cópias para o Condomínio como `<nome>.tmp` e as renomeia no fim (R-11), então o receptor nunca vê um arquivo parcial.

**Alternativas**: `FileSystemWatcher` (pouco confiável em SMB e não substitui a varredura exigida); arquivo de marcação `.done` (exigiria mudar o sistema que envia os arquivos).

## R-10 — Segregação e nomes dos arquivos gerados

**Decisão** (todas as gravações usam `<destino>.tmp` + `File.Move` atômico, e nada é sobrescrito):

| Situação | Destino | Nome |
| --- | --- | --- |
| 100% sucesso (`INATIVADO`/`JA_INATIVO`) | `Output` (Inter) / `Processados` (Condomínio) | nome original; se houver colisão com conteúdo diferente: `<base>__<h8>.<ext>` |
| Parte com sucesso | idem | `<baseRaiz>__sucesso-<h8>.<ext>` |
| Parte com falha definitiva | `Falhas/` | `<baseRaiz>__falhas-<h8>.<ext>` |
| Parte pendente | `Imput/` | `<baseRaiz>__pendente-<h8>.<ext>` |
| Arquivo inválido ou acima do limite | `Falhas/` | nome original; se houver colisão: `<base>__<h8>.<ext>` |

`<baseRaiz>` é o nome-base do arquivo original (sem acúmulo de sufixos a cada nova tentativa), e `<h8>` são os 8 primeiros caracteres hex do SHA-256 do conteúdo da parte. Se o destino já tem um arquivo com o mesmo hash, a operação é no-op (Princípio III). Ordem para tolerar crash: gravar as partes, registrar no diário, e só então remover o original da entrada.

**Formato das partes**: o mesmo do original. Em JSON, array de objetos `{cpf, id_responsavel, nome_responsavel}` com o CPF normalizado. Em TXT, `cpf,id_responsavel,nome_responsavel` com CPF normalizado. Ambos em UTF-8 sem BOM e com `\r\n`. A parte de **falhas** preserva o **texto bruto** de cada registro (linha TXT ou elemento JSON original), para que o registro possa ser corrigido e reenviado.

## R-11 — Replicação Inter → Condomínio

**Decisão**:
1. A replicação só roda depois que todos os arquivos **novos** do ciclo foram processados. "Novo" é um hash que o diário ainda não conhece; as partes `__pendente-` são conhecidas e não contam.
2. **Reconciliação**: varrer `Output` (`.json`/`.txt`), calcular o SHA-256 e criar `entrega(NaoEntregue)` para cada hash ainda sem linha **que o diário conheça como parte `Integral` ou `Sucesso` gerada pelo Inter**. Um arquivo desconhecido nunca é entregue, porque não há prova de que seus registros foram concluídos no Inter (FR-035): ele gera um aviso técnico e o alerta de saúde `ARQUIVO_OUTPUT_DESCONHECIDO`. A pasta é a fonte de verdade; as linhas criadas na gravação das partes são só uma otimização. Para cada arquivo de `Output` sem entrega concluída no diário: gravar `<FolderPathCondominio>/Imput/<nome>.tmp`, recalcular o SHA-256 lendo o destino, comparar com a origem, renomear para `<nome>` e registrar `Entregue` (hash, nome no destino, data/hora).
3. Colisão no destino: se o conteúdo é igual, registra como entregue (no-op); se é diferente, o arquivo vai com `<base>__<h8>.<ext>`.
4. Se a cópia falhar, o `.tmp` é removido no próximo ciclo, a origem fica intacta e a entrega é tentada de novo (FR-034).
5. Propagação de trace: antes de renomear o arquivo principal, o serviço grava `<nome>.traceparent` (W3C, uma linha) ao lado dele. O Condomínio lê esse arquivo se existir e o move junto com o principal. Ver [contracts/replicacao.md](./contracts/replicacao.md).

**Alternativas**: robocopy/agente externo (outro processo, viola o Princípio I); ADS do NTFS para o trace (nem todo SMB preserva).

## R-12 — Hospedagem e ciclo

**Decisão**: o serviço roda como Windows Service via `Microsoft.Extensions.Hosting.WindowsServices` (`AddWindowsService`); a solução já tem um instalador MSI (`InativacaoUsuarioInter.Setup.vdproj`). O laço do ciclo faz a varredura e depois `await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), timeProvider, ct)`, ou seja, a espera começa **depois** do término (FR-006). `PeriodicTimer` foi rejeitado porque mede a partir do início. Tudo usa `TimeProvider` para os testes de agendamento. O shutdown respeita `HostOptions.ShutdownTimeout` (padrão 30 s): o registro corrente termina ou fica `EmExecucao`, e a recuperação de R-08 cuida dele.

**Alternativas**: Agendador de Tarefas do Windows (perde o controle de não sobreposição e a telemetria contínua).

## R-13 — Sinal de saúde sem endpoint de rede

**Decisão**: o serviço grava `saude.json` no diretório de estado ao fim de cada ciclo (escrita atômica) e expõe métricas OTel equivalentes. O arquivo informa liveness (fim do último ciclo), readiness por dependência e alertas (pendência acima de 24 h, falha de permissão ou configuração). Ver [contracts/saude.md](./contracts/saude.md).

**Justificativa**: um endpoint HTTP seria um "novo endpoint de rede", que pelo Princípio I exige ADR, e um Windows Service não precisa dele. Ferramentas de monitoramento de arquivo e o coletor OTel atendem a operação.

**Alternativas**: endpoint HTTP `/health` (exige ADR; pode ser adicionado depois); somente o Event Log (difícil consumir de forma estruturada).

## R-14 — Telemetria (Princípio IV)

**Decisão**: `OpenTelemetry.Extensions.Hosting`, com um `ActivitySource` e um `Meter` chamados `InativacaoUsuarioInter`, instrumentação `SqlClient` e exportador OTLP habilitado quando `Telemetria:OtlpEndpoint` está definido. Na falta do endpoint, a instrumentação continua ativa e não há exportação. Os logs vão por `ILogger` com o provedor OTel e, como saída local, Event Log e console.

- Spans: `ciclo`, `arquivo`, `registro`, `waccess.inativar`, `arquivo.mover`, `replicacao.copiar`. Atributos de domínio: `instancia`, `arquivo` (nome do arquivo, que não contém dado pessoal), `idempotency_key`, `resultado`, `tentativa`.
- **CPF e nomes nunca aparecem** em spans, métricas ou logs técnicos. A correlação é feita pela `idempotency_key` (hash). A instrumentação SqlClient roda com `SetDbStatementForText = true`, mas sem parâmetros: o texto do comando não tem dados, porque é parametrizado.
- Métricas: `registros_processados{resultado}`, `duracao_registro_ms`, `duracao_arquivo_ms`, `duracao_ciclo_ms` (histogramas), `retentativas`, `circuito_estado{dependencia}`, `backlog_pendente`, `idade_pendencia_mais_antiga_s`.
- Uma falha do exportador não interrompe o processamento (`BatchExportProcessor` com fila limitada).

## R-15 — Segredo versionado (tensão constituição × documento-base §4.1)

**Evidência**: `src/InativacaoUsuarioInter.Service/appsettings.json` está **rastreado no git**, com senha em texto aberto. `bin/` e `obj/` também estão versionados.

**Decisão**:
- Tirar `appsettings.json` do controle de versão (`git rm --cached`) e incluí-lo no `.gitignore`, junto com `bin/`, `obj/` e `.vs/`.
- Versionar `appsettings.example.json` com placeholders.
- O `appsettings.json` **local** da máquina de testes continua com a senha explícita e legível (documento-base §4.1); nada é mascarado nem movido automaticamente.
- Como a senha já está no histórico do git, **recomenda-se trocar a senha** do usuário `waccess` de testes.

**Justificativa**: atende literalmente às duas regras. A senha fica explícita no arquivo local, e nenhum segredo é versionado a partir de agora.

## R-16 — Renomeação da chave de conexão

**Decisão**: o código lê `ConnectionString:WAccess` (Clarification Q1). A seção se chama `ConnectionString`, no singular, então `GetConnectionString()` não serve; a leitura é por binding de options. O `appsettings.json` local é migrado de `WAccessInter` para `WAccess`. Se o serviço encontrar a chave antiga sem a nova, a partida falha com uma mensagem que indica a renomeação. O valor antigo nunca é lido silenciosamente.

## R-17 — Testes

**Decisão**:
- **xUnit v3**, com `Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`) e `OpenTelemetry.Exporter.InMemory`.
- **Testcontainers.MsSql** (SQL Server 2022) com uma réplica mínima de `CHMain`: `CHID int PK`, `IdNumber varchar(50) NULL`, `CHState smallint NOT NULL`, o índice `(CHType, IdNumber)` e um trigger de teste **sem `SET NOCOUNT ON`**, que reproduz o efeito sobre a contagem de linhas.
- **Testcontainers.Toxiproxy** para latência, timeout e queda de conexão. Pausar o container simula indisponibilidade total.
- Sistema de arquivos temporário real; um compartilhamento sem permissão é simulado com ACL negada em diretório temporário.
- **Crash do processo** (Princípio V; revisado pela análise C2), em duas camadas:
  1. *Em processo* (rápido, cobre todos os pontos): o ponto de injeção `IPontoFalha` lança uma exceção que aborta o host sem shutdown gracioso, e um segundo host é iniciado sobre o mesmo diretório de estado.
  2. *Processo real* (obrigatório): o serviço publicado roda como **processo filho** com a variável `INATIVACAO_PONTO_FALHA=<Ponto>`, honrada **somente** com `DOTNET_ENVIRONMENT=Testing`. Ao atingir o ponto, o serviço chama `Environment.FailFast`, encerrando o processo sem `finally`, sem flush gerenciado e sem dispose. Um novo processo é iniciado sobre o mesmo estado e as mesmas pastas, o que exercita de verdade a durabilidade do WAL, do flush para disco e dos `.tmp`.
- **Regressão de performance** (Princípio VI; revisado pela análise C3): o teste de carga grava e compara uma linha de base versionada (`tests/InativacaoUsuarioInter.Tests.Integration/Performance/linha-base.json`, com p95 por registro, vazão e duração do arquivo de 1.000 registros). Uma piora acima de 20% em qualquer métrica falha o teste e bloqueia o merge no CI. A linha de base só é atualizada explicitamente (`ATUALIZAR_LINHA_BASE=1`), num commit que referencia o ADR de justificativa.
- **Opt-in CA-20**: com `WACCESS_TESTE_LOCALDB=1`, uma suíte roda contra o `W_ACCESS` local da configuração, escolhe CPFs existentes, verifica a alteração real de `CHState` e restaura o valor original no teardown.

**Alternativas**: mocks de banco (proibidos pelo Princípio V); somente LocalDB (não é reprodutível em CI e não permite injetar falhas de rede).
