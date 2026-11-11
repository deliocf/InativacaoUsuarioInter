---

description: "Lista de tarefas — Inativação e Replicação de Usuários WAccess (Inter → Condomínio)"
---

# Tasks: Inativação e Replicação de Usuários WAccess (Inter → Condomínio)

**Input**: Documentos de design em `specs/001-inativacao-usuarios-waccess/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md), [data-model.md](./data-model.md), [contracts/](./contracts/), [quickstart.md](./quickstart.md)

**Tests**: **INCLUÍDOS e obrigatórios**. O documento-base (§12) pede testes unitários, de integração e ponta a ponta, e a constituição (Princípio V) exige testes de contrato e de injeção de falhas, escritos **antes** da implementação (red → green).

**Organization**: as tarefas estão agrupadas por história de usuário (US1–US5 da spec), para que cada uma possa ser implementada e testada de forma independente.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: pode rodar em paralelo (arquivo diferente, sem dependência de tarefa incompleta)
- **[Story]**: história da spec atendida (US1…US5)
- Caminhos relativos à raiz do repositório

## Path Conventions

- Serviço: `src/InativacaoUsuarioInter.Service/` (projeto único, módulos por namespace — ver plan.md)
- Testes: `tests/InativacaoUsuarioInter.Tests.{Unit,Contract,Integration,Comum}/`
- Namespace raiz: `InativacaoUsuarioInter.Service.<Modulo>`; tipos não públicos são `internal`, com `InternalsVisibleTo` para os projetos de teste

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: higiene do repositório (gate de segredo, R-15/R-16) e criação dos projetos.

- [X] T001 Criar `.gitignore` na raiz com `bin/`, `obj/`, `.vs/`, `*.user`, `TestResults/` e `src/InativacaoUsuarioInter.Service/appsettings.json`. Tirar do índice, **sem apagar do disco**: `git rm -r --cached src/.vs src/InativacaoUsuarioInter.Service/bin src/InativacaoUsuarioInter.Service/obj src/InativacaoUsuarioInter.Service/appsettings.json` (research R-15)
- [X] T002 [P] Criar `src/InativacaoUsuarioInter.Service/appsettings.example.json` (versionado) com o exemplo Inter de `contracts/configuracao.md` e todas as chaves técnicas com seus defaults (`Processamento:*`, `Resiliencia:{WAccess,Arquivos,Replicacao}:*`, `Telemetria:OtlpEndpoint` vazio). Usar apenas placeholders `<...>`, nenhuma credencial
- [X] T003 [P] No `src/InativacaoUsuarioInter.Service/appsettings.json` **local** (não versionado): renomear `ConnectionString:WAccessInter` para `ConnectionString:WAccess` e adicionar `"Settings": { "Instancia": "Inter", "IntervalSeconds": 30 }`. Manter a senha explícita e legível (documento-base §4.1; R-16). Marcar o arquivo com `CopyToOutputDirectory=PreserveNewest` no csproj
- [X] T004 Atualizar `src/InativacaoUsuarioInter.Service/InativacaoUsuarioInter.Service.csproj`: `TreatWarningsAsErrors=true`, `EnableNETAnalyzers=true`, `AnalysisLevel=latest`. Adicionar os pacotes `Microsoft.Extensions.Hosting.WindowsServices`, `Microsoft.Data.SqlClient`, `Microsoft.Data.Sqlite`, `Microsoft.Extensions.Resilience`, `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.SqlClient` (versões estáveis compatíveis com `net10.0`) e `InternalsVisibleTo` para os 4 projetos de teste
- [X] T005 [P] Criar a biblioteca de apoio `tests/InativacaoUsuarioInter.Tests.Comum/InativacaoUsuarioInter.Tests.Comum.csproj` (`net10.0`) com `Testcontainers.MsSql`, `Testcontainers.Toxiproxy`, `Microsoft.Data.SqlClient`, `Microsoft.Extensions.TimeProvider.Testing`, `OpenTelemetry.Exporter.InMemory` e referência ao projeto do serviço
- [X] T006 [P] Criar `tests/InativacaoUsuarioInter.Tests.Unit/InativacaoUsuarioInter.Tests.Unit.csproj` (xUnit v3) referenciando o serviço e `Tests.Comum`
- [X] T007 [P] Criar `tests/InativacaoUsuarioInter.Tests.Contract/InativacaoUsuarioInter.Tests.Contract.csproj` (xUnit v3) referenciando o serviço e `Tests.Comum`; incluir `Logs/golden/*` como `Content` copiado para a saída
- [X] T008 [P] Criar `tests/InativacaoUsuarioInter.Tests.Integration/InativacaoUsuarioInter.Tests.Integration.csproj` (xUnit v3) referenciando o serviço e `Tests.Comum`
- [X] T009 Adicionar os 4 projetos de teste a `src/InativacaoUsuarioInter.Service.slnx` (caminhos `../tests/...`) e confirmar que `dotnet build src/InativacaoUsuarioInter.Service.slnx` passa sem warnings. Criar já o CI básico em `.github/workflows/ci.yml` (gates 1–2 desde o primeiro PR): gatilho em `pull_request` e `push` para `main`; `dotnet build` com warnings como erro; `dotnet test` das suítes Unit, Contract e Integration (Docker disponível no runner), publicando os resultados. Uma falha bloqueia o merge, desde que a proteção da branch `main` exija esse check; essa configuração é manual no GitHub e fica documentada em `docs/operacao.md` (T091)
- [X] T010 [P] Criar `.editorconfig` na raiz com convenções C# (file-scoped namespaces, `var` quando o tipo é evidente) e `dotnet_diagnostic.CA2007.severity = none` (Worker Service)

**Checkpoint**: repositório sem segredo versionado; solução compila com 5 projetos; CI básico ativo (build + 3 suítes) a partir do primeiro PR.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: tipos de domínio, configuração (binding), estado durável, resiliência, telemetria base, escrita atômica e infraestrutura de teste, usados por todas as histórias.

**⚠️ CRITICAL**: nenhuma história começa antes do fim desta fase.

### Testes da fundação (escrever primeiro e ver falhar)

- [X] T011 [P] Teste unitário dos classificadores em `tests/InativacaoUsuarioInter.Tests.Unit/Resiliencia/ClassificadorErrosTests.cs`: números SQL `-2, 53, 233, 4060, 10053, 10054, 10060, 11001, 40613, 1205, 18456, 208, 229, 230` → `Transitoria`; `IOException` com HResult 32/33/53/64/67 e `UnauthorizedAccessException` → `Transitoria`; `TimeoutException` → `Transitoria` (research R-05)
- [X] T012 [P] Teste unitário de escrita atômica em `tests/InativacaoUsuarioInter.Tests.Unit/Arquivos/EscritorAtomicoTests.cs`: (a) gravar duas vezes o mesmo conteúdo no mesmo destino é no-op; (b) destino existente com conteúdo diferente nunca é sobrescrito (`IOException`); (c) `.tmp` deixado por interrupção não é visível com o nome final; (d) o SHA-256 é estável
- [X] T013 [P] Teste de integração do diário SQLite em `tests/InativacaoUsuarioInter.Tests.Integration/Estado/ArmazenamentoEstadoTests.cs`: cria o esquema, reabre o arquivo e mantém os dados; transição `Pendente→EmExecucao→Inativado` com `linha_log` gravados na mesma transação; reaplicar a migração é idempotente

### Implementação da fundação

- [X] T014 [P] Criar os tipos de domínio em `src/InativacaoUsuarioInter.Service/Entrada/Modelos/`: `Instancia.cs` (`Inter`, `Condominio`), `FormatoArquivo.cs` (`Json`, `Txt`), `ValidacaoRegistro.cs` (`Valido`, `DadoInvalido`, `Duplicado`), `RegistroEntrada.cs` (record com `Indice`, `TextoBruto`, `CpfOriginal`, `Cpf`, `IdResponsavel`, `NomeResponsavel`, `ChaveIdempotencia`, `Validacao`), `ArquivoEntrada.cs` (record com `Caminho`, `Nome`, `Formato`, `Hash`, `HashRaiz`, `BaseRaiz`, `TamanhoBytes`, `Registros`), conforme `data-model.md §1`
- [X] T015 [P] Criar `src/InativacaoUsuarioInter.Service/Inativacao/ResultadoRegistro.cs` (`Inativado`, `JaInativo`, `CpfNaoEncontrado`, `DadoInvalido`, `PendenteTransitorio`) e `src/InativacaoUsuarioInter.Service/Resiliencia/ClasseErro.cs` (`Transitoria`, `Definitiva`)
- [X] T016 [P] Criar as classes de options **somente com binding** em `src/InativacaoUsuarioInter.Service/Configuracao/`: `SettingsOptions` (seção `Settings`: `Instancia`, `IntervalSeconds`), `SharePointFolderOptions` (`FolderPathInter`, `FolderPathCondominio`), `ConnectionStringOptions` (seção `ConnectionString`, singular: `WAccess`), `ProcessamentoOptions` (`DiretorioEstado` default `%ProgramData%\InativacaoUsuarioInter\<Instancia>`, `EstabilizacaoSegundos=5`, `MaximoRegistros=1000`, `TamanhoMaximoBytes=1048576`, `AlertaPendenciaHoras=24`), `ResilienciaOptions` (por dependência `WAccess`/`Arquivos`/`Replicacao`: `MaxTentativas=3`, `AtrasoBaseMs=1000`, `AtrasoMaximoMs=4000`, `Circuito{TaxaFalha=0.5, JanelaSegundos=30, MinimoChamadas=3, PausaSegundos=60}`, `TimeoutConexaoSegundos=15`, `TimeoutComandoSegundos=30`, `TimeoutOperacaoSegundos=30`), `TelemetriaOptions` (`OtlpEndpoint`)
- [X] T017 [P] Implementar `src/InativacaoUsuarioInter.Service/Inativacao/ClassificadorErrosSql.cs` e `src/InativacaoUsuarioInter.Service/Arquivos/ClassificadorErrosIO.cs` conforme a tabela de research R-05. Erros de infraestrutura nunca são `Definitiva` (faz T011 passar)
- [X] T018 [P] Implementar `src/InativacaoUsuarioInter.Service/Telemetria/Instrumentacao.cs`: `ActivitySource` e `Meter` com o nome `InativacaoUsuarioInter`; constantes de atributo `instancia`, `arquivo`, `idempotency_key`, `resultado` (`sucesso|transitoria|definitiva`), `tentativa`, `waccess.linhas_afetadas`; instrumentos `registros_processados{resultado}`, `duracao_registro_ms`, `duracao_arquivo_ms`, `duracao_ciclo_ms` (histogramas), `retentativas`, `circuito_estado{dependencia}`, `backlog_pendente`, `idade_pendencia_mais_antiga_s`. **Nenhum atributo pode receber CPF ou nome** (research R-14)
- [X] T019 [P] Implementar `src/InativacaoUsuarioInter.Service/Arquivos/HashConteudo.cs` (SHA-256 hex minúsculo de bytes e de arquivo, com leitura em streaming) e `src/InativacaoUsuarioInter.Service/Arquivos/EscritorAtomico.cs` (grava `<destino>.tmp`, faz flush para disco, depois `File.Move(tmp, destino, overwrite:false)`; se o destino existe com o mesmo hash, remove o tmp e retorna no-op; se o hash é diferente, lança `ColisaoArquivoException`). Faz T012 passar
- [X] T020 Criar a interface `src/InativacaoUsuarioInter.Service/Estado/IArmazenamentoEstado.cs` e seus DTOs (`EstadoArquivo`, `EstadoRegistro`, `ParteGerada`, `EstadoEntrega`) com os estados de `data-model.md §2–3`. Operações: `ObterArquivoAsync(hash)`, `ObterParteAsync(hash)` (devolve `tipo`/`hash_origem` de `parte_gerada` ou `null`), `ResolverHashRaizAsync(hash)` (via `parte_gerada.hash` → `hash_origem` → `arquivo.hash_raiz`; devolve o próprio `hash` se não for parte conhecida), `RegistrarArquivoAsync`, `MarcarIntencaoAsync(chave, estadoAnterior)` (grava `EmExecucao` + `estado_anterior` ∈ `Ativo|Inativo|Inexistente` numa transação), `RegistrarResultadoAsync(chave, resultado, linhaLog, linhasAfetadas)` (atômica), `MarcarLogGravadoAsync`, `RegistrarPendenciaAsync(chave, agora)`, `ListarRecuperacaoAsync()`, `RegistrarParteAsync`, `ConcluirArquivoAsync(hash, estado)`, `ObterPendenciaMaisAntigaAsync()`, `ContarBacklogAsync()`, `ListarEntregasPendentesAsync()`, `RegistrarEntregaAsync`. Todos aceitam `CancellationToken`
- [X] T021 Implementar `src/InativacaoUsuarioInter.Service/Estado/ArmazenamentoEstadoSqlite.cs` e `src/InativacaoUsuarioInter.Service/Estado/Migracoes.cs`: arquivo `<DiretorioEstado>/estado.db`, `PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL`, tabelas `versao_esquema`, `arquivo`, `registro`, `parte_gerada` e `entrega` com as colunas exatas de `data-model.md §2` (ex.: `registro.chave_idempotencia TEXT PK`, `registro.estado_anterior TEXT NULL` ∈ `Ativo|Inativo|Inexistente`, obrigatório quando `estado = EmExecucao`, `registro.log_gravado INTEGER NOT NULL DEFAULT 0`, `entrega.estado TEXT NOT NULL` ∈ `NaoEntregue|Entregue`); datas em ISO 8601 com offset vindas de `TimeProvider`. Faz T013 passar (depende de T020)
- [X] T022 Criar `src/InativacaoUsuarioInter.Service/Resiliencia/OrcamentoCiclo.cs`: orçamento de retentativas inicializado com `IntervalSeconds` e medido com `TimeProvider`, com `Consumir(duracao)`, `PodeTentar(espera, timeout)`, `Restante` e a chave estática `ResiliencePropertyKey<OrcamentoCiclo> Chave` (research R-06, análise C1). Em seguida, implementar `src/InativacaoUsuarioInter.Service/Resiliencia/RegistroPipelines.cs`: pipelines nomeados `waccess`, `pasta-local` e `pasta-replicacao` (`AddResiliencePipeline`), cada um com Timeout (config) + Retry (`BackoffType=Exponential`, `UseJitter=true`, `MaxRetryAttempts`, `Delay=AtrasoBaseMs`, `MaxDelay=AtrasoMaximoMs`) + CircuitBreaker (`FailureRatio`, `SamplingDuration`, `MinimumThroughput`, `BreakDuration`). `ShouldHandle` aceita **somente** exceções classificadas como `Transitoria` (T017) **e** só autoriza nova tentativa se o `OrcamentoCiclo` recebido via `ResilienceContext` (`OrcamentoCiclo.Chave`; a instância por ciclo é criada pelo orquestrador na T066) tiver `restante ≥ próximaEspera + timeoutDaTentativa`. A duração das tentativas extras e as esperas são descontadas desse orçamento, que é igual a `IntervalSeconds` (research R-06, Princípio II). Sem orçamento no contexto (testes unitários), não há limite. Os eventos de retry e de mudança de estado do circuito alimentam as métricas `retentativas` e `circuito_estado` (depende de T016–T018)
- [X] T023 Reescrever `src/InativacaoUsuarioInter.Service/Program.cs` como raiz de composição: binding das options de T016; `AddOpenTelemetry()` com tracing (fonte `InativacaoUsuarioInter` + instrumentação SqlClient **sem** parâmetros), métricas e logging, e exportador OTLP **somente** se `Telemetria:OtlpEndpoint` estiver definido (`BatchExportProcessor` com fila limitada); pipelines de T022; `IArmazenamentoEstado` como singleton; `TimeProvider.System`. Ainda sem hosted service (vem em US2)
- [X] T024 [P] Criar `tests/InativacaoUsuarioInter.Tests.Comum/SqlServerFixture.cs`: container `mcr.microsoft.com/mssql/server:2022-latest` que cria a réplica de `dbo.CHMain` **exatamente** como em `contracts/waccess-inativacao.md` (`IdNumber varchar(50) NULL`, `CHState smallint NOT NULL`, índice `(CHType, IdNumber)`, trigger `wtr_Teste_ChangeCHState` **sem** `SET NOCOUNT ON`), com helpers `InserirUsuarioAsync(cpf, chState)`, `ObterSnapshotAsync()` (todas as colunas, para comparar antes e depois) e `ObterEstadoAsync(cpf)`
- [X] T025 [P] Criar `tests/InativacaoUsuarioInter.Tests.Comum/AmbienteTeste.cs`: diretórios temporários reais `Inter/` e `Condominio/` com as subpastas do contrato, diretório de estado temporário, `FakeTimeProvider`, coletor `InMemoryExporter` de spans, métricas e logs, e o método `CriarServicos(instancia, overrides)`, que monta o `ServiceProvider` a partir de `Program` com configuração em memória
- [X] T026 [P] Criar `tests/InativacaoUsuarioInter.Tests.Comum/ToxiproxyFixture.cs`: proxy Toxiproxy na frente do SQL Server da T024, com `InjetarLatenciaAsync(ms)`, `CortarConexaoAsync()`, `RestaurarAsync()` e a connection string via proxy

**Checkpoint**: fundação pronta. Testes T011–T013 verdes e solução compilando.

---

## Phase 3: User Story 1 — Inativar usuários no Inter a partir de um arquivo (Priority: P1) 🎯 MVP

**Goal**: um arquivo `.json`/`.txt` em `{FolderPathInter}/Imput` inativa no WAccess do Inter cada CPF existente, grava Auditoria/Falhas no formato contratual e move ou segrega o arquivo (`Output` e `Falhas`).

**Independent Test**: com SQL Server em container, chamar `IOrquestradorCiclo.ExecutarCicloAsync` (sem hosted service) sobre um `Imput` com arquivos JSON e TXT e verificar o banco, os logs e o destino dos arquivos (CA-01, 02, 03, 04, 05, 13, 14, 15).

### Tests for User Story 1 ⚠️ (escrever primeiro; precisam FALHAR)

- [X] T027 [P] [US1] Teste de contrato de formatos em `tests/InativacaoUsuarioInter.Tests.Contract/ArquivosEntrada/FormatoEntradaTests.cs`: exemplos JSON e TXT de `contracts/arquivos-entrada.md §3–§4` (inclui nome com vírgula `Carlos Renato da Silva, Jr.`), **todas** as linhas da tabela de normalização da §5 (`"012.345.678-90"`→`01234567890`, `"0123456789"`→`DADO_INVALIDO`, `"1'; DROP TABLE CHMain;--"`→`DADO_INVALIDO`, etc.), CPF numérico em JSON → `DADO_INVALIDO` e as rejeições da §6 (UTF-8 inválido, JSON inválido, raiz que não é array, vazio → `ARQUIVO_INVALIDO`; mais de 1000 registros ou mais de 1048576 bytes → `ARQUIVO_EXCEDE_LIMITE`)
- [X] T028 [P] [US1] Teste de contrato de logs em `tests/InativacaoUsuarioInter.Tests.Contract/Logs/LayoutLogTests.cs` com os golden files `tests/InativacaoUsuarioInter.Tests.Contract/Logs/golden/auditoria.log` e `falhas.log`, que contêm as linhas de exemplo exatas de `contracts/logs-auditoria-falhas.md`. Cobre a ordem dos campos, o formato `yyyy-MM-ddTHH:mm:sszzz`, UTF-8 sem BOM com `\r\n` e o saneamento de `|`, `\r` e `\n` para espaço (FR-020a, CA-15)
- [X] T029 [P] [US1] Teste de contrato WAccess em `tests/InativacaoUsuarioInter.Tests.Contract/WAccess/InativacaoWAccessTests.cs` (usa a T024), cobrindo os 8 invariantes de `contracts/waccess-inativacao.md`, com pré-leitura (passo A) e alteração (passo B): só `CHState` muda (comparação de snapshot), nenhuma outra linha é alterada, zeros à esquerda (`01234567890` ≠ `1234567890`), entrada maliciosa vai como parâmetro sem efeito, contagem correta com o trigger sem NOCOUNT, execução dupla → `INATIVADO` e depois `JA_INATIVO`, passo B repetido com `estado_anterior=Ativo` e `Inativados=0` → `INATIVADO`, usuário já inativo nunca recebe UPDATE (sem linha nova em `CHMainHistoricoTeste`) → `JA_INATIVO`. Inclui um CPF com 2 linhas → as 2 inativadas, `Inativados=2` (research R-02)
- [X] T030 [P] [US1] Teste de contrato de diretórios em `tests/InativacaoUsuarioInter.Tests.Contract/EstruturaDiretorios/EstruturaInterTests.cs`: cria `Imput`, `Output`, `Auditoria` e `Falhas` quando faltam (grafia exata `Imput`), não apaga conteúdo existente e não cria `Processados` no Inter
- [X] T031 [P] [US1] Testes unitários de entrada em `tests/InativacaoUsuarioInter.Tests.Unit/Entrada/NormalizadorCpfTests.cs`, `ParserJsonTests.cs`, `ParserTxtTests.cs` e `ValidadorRegistroTests.cs`: remoção apenas de `.`, `-` e espaços internos com `^[0-9]{11}$`; propriedades JSON precisam ser string, extras ignoradas; TXT dividido nas 2 primeiras vírgulas com o restante como nome; linhas vazias ignoradas; espaços nas pontas removidos; cabeçalho `cpf,id_responsavel,nome_responsavel` → `DadoInvalido`; CPF repetido → a 2ª ocorrência fica `Duplicado`; `id_responsavel`/`nome_responsavel` vazios → `DadoInvalido`; chave `SHA-256(instancia|hashRaiz|cpf)` determinística e chave `#indice` para inválidos
- [X] T032 [P] [US1] Testes unitários em `tests/InativacaoUsuarioInter.Tests.Unit/Arquivos/NomeadorPartesTests.cs` e `SerializadorPartesTests.cs`: nomes `<baseRaiz>__sucesso-<h8>.<ext>`, `__falhas-<h8>` e `__pendente-<h8>` (sem acumular sufixos); colisão → `<base>__<h8>.<ext>`; JSON indentado com 2 espaços e ordem `cpf, id_responsavel, nome_responsavel`; TXT com `\r\n`; UTF-8 sem BOM; a parte de falhas preserva `TextoBruto`
- [X] T033 [P] [US1] Teste de integração do fluxo Inter em `tests/InativacaoUsuarioInter.Tests.Integration/FluxoInter/FluxoInterTests.cs`: CA-02 (JSON com 3 CPFs → 3 inativos, 3 linhas `status=INATIVADO`, arquivo movido inteiro para `Output`); CA-03 (TXT equivalente); CA-04; CA-05 (1 CPF ausente → linha `causa=CPF_NAO_ENCONTRADO`, `Falhas/<base>__falhas-<h8>.json` só com esse registro, `Output/<base>__sucesso-<h8>.json` com os outros 2, `Imput` vazio); usuário já inativo → `status=JA_INATIVO` uma vez; arquivo malformado → movido para `Falhas/` com `causa=ARQUIVO_INVALIDO` e nenhuma chamada SQL; `.csv`/`.tmp` ignorados e intactos (CA-13); mesmo arquivo depositado de novo → nenhuma linha nova de log
- [X] T034 [P] [US1] Teste de integração de elegibilidade em `tests/InativacaoUsuarioInter.Tests.Integration/FluxoInter/ElegibilidadeTests.cs`: arquivo mantido aberto para escrita ou com `LastWriteTime` há menos de `EstabilizacaoSegundos` (FakeTimeProvider) não é lido no ciclo e é processado no ciclo seguinte (CA-14)
- [X] T035 [P] [US1] Teste de cobertura de telemetria em `tests/InativacaoUsuarioInter.Tests.Integration/Telemetria/CoberturaTelemetriaUS1Tests.cs`: todo ciclo, arquivo e registro gera span com `instancia`, `arquivo`, `idempotency_key`, `resultado` e `tentativa`; existe span `waccess.inativar`; **nenhum** span, log técnico ou métrica contém um CPF dos dados de teste

### Implementation for User Story 1

- [X] T036 [P] [US1] Implementar `src/InativacaoUsuarioInter.Service/Entrada/NormalizadorCpf.cs`: remover espaços nas pontas, depois **apenas** `.`, `-` e espaços internos; aceitar só `^[0-9]{11}$`; não validar dígito verificador (FR-011)
- [X] T037 [P] [US1] Implementar `src/InativacaoUsuarioInter.Service/Entrada/ParserJson.cs` com `Utf8JsonReader`/`JsonDocument` sobre os bytes limitados: raiz precisa ser array; cada elemento gera um `RegistroEntrada` com `TextoBruto` = JSON bruto do elemento; `cpf`, `id_responsavel` e `nome_responsavel` precisam ser `JsonValueKind.String`, senão → `DadoInvalido`
- [X] T038 [P] [US1] Implementar `src/InativacaoUsuarioInter.Service/Entrada/ParserTxt.cs`: separar linhas por `\n`/`\r\n`; ignorar linhas vazias ou só com espaços; dividir apenas nas 2 primeiras vírgulas, com o restante como `nome_responsavel`; menos de 3 campos → `DadoInvalido`; `TextoBruto` = linha original
- [X] T039 [US1] Implementar `src/InativacaoUsuarioInter.Service/Entrada/ValidadorRegistro.cs`: aplica `NormalizadorCpf`; `id_responsavel` e `nome_responsavel` obrigatórios e não vazios depois de remover espaços das pontas; marca `Duplicado` a partir da 2ª ocorrência do mesmo CPF; calcula `ChaveIdempotencia = SHA-256(instancia + "|" + hashRaiz + "|" + cpf)` ou `SHA-256(instancia|hashRaiz|"#"+indice)` para inválidos (depende de T036)
- [X] T040 [US1] Implementar `src/InativacaoUsuarioInter.Service/Entrada/LeitorArquivo.cs`: recusa antes de ler se `TamanhoBytes > TamanhoMaximoBytes` (`ARQUIVO_EXCEDE_LIMITE`); lê bytes limitados; decodifica com `UTF8Encoding(false, throwOnInvalidBytes:true)` aceitando BOM; escolhe o parser pela extensão; nenhum registro → `ARQUIVO_INVALIDO`; mais de `MaximoRegistros` → `ARQUIVO_EXCEDE_LIMITE`; calcula `Hash`, **resolve `HashRaiz` com `IArmazenamentoEstado.ResolverHashRaizAsync(hash)` antes de validar** e só então aplica `ValidadorRegistro`, para que as partes `__pendente-` mantenham as chaves do original (research R-08, análise H4) (depende de T020, T037–T039)
- [X] T041 [US1] Implementar `src/InativacaoUsuarioInter.Service/Entrada/DetectorElegiveis.cs`: lista `<raiz>/Imput` apenas com extensões `.json`/`.txt` (sem diferenciar maiúsculas); adia arquivos com `LastWriteTimeUtc` mais recente que `agora - EstabilizacaoSegundos` (`TimeProvider`) ou que não abrem com `FileShare.None`; ordena por `LastWriteTimeUtc`; acesso ao disco via pipeline `pasta-local`
- [X] T042 [US1] Implementar `src/InativacaoUsuarioInter.Service/Inativacao/IRepositorioWAccess.cs` e `RepositorioWAccessSql.cs` com dois métodos, **exatamente** como em `contracts/waccess-inativacao.md`: `LerEstadoAsync(cpf)` (passo A: `SELECT COUNT(*) Existentes, SUM(CASE WHEN CHState <> 1 THEN 1 ELSE 0 END) Ativos ... WHERE IdNumber = @Cpf` → `Ativo|Inativo|Inexistente`) e `InativarAsync(cpf)` (passo B: `UPDATE dbo.CHMain SET CHState = 1 OUTPUT inserted.CHID INTO @afetados WHERE IdNumber = @Cpf AND CHState <> 1` + `SELECT Inativados, Existentes`, em transação `ReadCommitted`). Os dois com `@Cpf` em `SqlDbType.VarChar`, `Size = 50`, `ConnectTimeout`/`CommandTimeout` vindos das options e chamados pelo pipeline `waccess`. Função pura `ClassificarResultado(estadoAnterior, resultadoPassoB?)` com a tabela do contrato (`Ativo` + `Existentes ≥ 1` → `INATIVADO` mesmo com `Inativados = 0`); quando `Inativados > 1`, emite um log de aviso `CpfComMultiplosCadastros` **sem CPF** (só `idempotency_key`); abre o span `waccess.inativar` com `waccess.linhas_afetadas`; uma conexão por arquivo
- [X] T043 [P] [US1] Implementar `src/InativacaoUsuarioInter.Service/Registros/FormatadorLinhaLog.cs`: `FormatarAuditoria(dataHora, instancia, arquivo, cpf, id, nome, status)` e `FormatarFalha(dataHora, instancia, arquivo, cpf?, causa)` no layout exato de `contracts/logs-auditoria-falhas.md`, com data `yyyy-MM-ddTHH:mm:sszzz` (horário local e offset) e saneamento de `\r`, `\n` e `|` para espaço em todos os valores
- [X] T044 [P] [US1] Implementar `src/InativacaoUsuarioInter.Service/Registros/EscritorLogAppendOnly.cs` e as interfaces `IRegistroAuditoria`/`IRegistroFalhas` (`Registros/IRegistros.cs`): acréscimo de uma linha UTF-8 sem BOM terminada em `\r\n` em `<raiz>/Auditoria/log_inativacao.log` ou `<raiz>/Falhas/log_inativacao.log`, com `FileShare.Read` e `FileOptions.WriteThrough`; `ContemLinhaAsync(linha)` lê o arquivo em streaming (usado na recuperação); acesso via pipeline `pasta-local`
- [X] T045 [P] [US1] Implementar `src/InativacaoUsuarioInter.Service/Arquivos/NomeadorPartes.cs` e `SerializadorPartes.cs` conforme research R-10 e `contracts/arquivos-entrada.md §7` (faz T032 passar)
- [X] T046 [US1] Implementar `src/InativacaoUsuarioInter.Service/Arquivos/IGerenciadorArquivos.cs` e `GerenciadorArquivos.cs`: `GarantirEstruturaAsync(instancia)` cria só as pastas do contrato, sem apagar nada (FR-031); `FinalizarAsync(arquivo, resultados)` com a ordem **partes gravadas (EscritorAtomico) → `RegistrarParteAsync` no diário → remover o original da entrada**: 100% sucesso → move o original para o destino de concluídos (`Output` no Inter); sucessos e falhas definitivas → parte `__sucesso-` no destino de concluídos + parte `__falhas-` em `Falhas/`; nenhum sucesso → só a parte de falhas; `RejeitarAsync(arquivo, causa)` → move o original para `Falhas/` (colisão → `<base>__<h8>.<ext>`). Hash já presente no destino → no-op (depende de T019, T045)
- [X] T047 [US1] Implementar `src/InativacaoUsuarioInter.Service/Ciclo/IPoliticaFluxo.cs` e `PoliticaFluxoInter.cs`: raiz = `FolderPathInter`, destino de concluídos = `Output`, `Replica = true`, pastas obrigatórias `Imput|Output|Auditoria|Falhas`
- [X] T048 [US1] Implementar `src/InativacaoUsuarioInter.Service/Ciclo/ProcessadorArquivo.cs`, com span `arquivo` e um span `registro` por registro. Fluxo: hash já `Concluido|Segregado|Rejeitado` no diário → no-op e remove o duplicado da entrada; arquivo rejeitado → linha de Falhas (`cpf=` vazio) + `RejeitarAsync`; caso contrário `RegistrarArquivoAsync` e, para cada registro: `DadoInvalido` → linha `causa=DADO_INVALIDO`; `Duplicado` → só log técnico; `Valido` → `LerEstadoAsync` → `MarcarIntencaoAsync(chave, estadoAnterior)` → `InativarAsync` **somente se `Ativo`** → `ClassificarResultado` → `RegistrarResultadoAsync` (resultado + `linha_log` pré-formatada com o horário da confirmação, numa transação). Um retry do pipeline sobre `InativarAsync` reaproveita o mesmo `estado_anterior`, então `Inativados = 0` após timeout continua `INATIVADO` → append no log → `MarcarLogGravadoAsync`. No fim chama `GerenciadorArquivos.FinalizarAsync` (depende de T040–T046)
- [X] T049 [US1] Implementar `src/InativacaoUsuarioInter.Service/Ciclo/IOrquestradorCiclo.cs` e `OrquestradorCiclo.cs`: `ExecutarCicloAsync(ct)` abre o span raiz `ciclo` (atributo `instancia`), chama `GarantirEstruturaAsync`, `DetectorElegiveis` e `ProcessadorArquivo` arquivo a arquivo, e registra `duracao_ciclo_ms`. Erro em um arquivo é classificado e registrado sem interromper os demais (FR-027)
- [X] T050 [US1] Registrar os serviços de US1 em `src/InativacaoUsuarioInter.Service/Program.cs` (Entrada, Inativacao, Registros, Arquivos, Ciclo, com a `IPoliticaFluxo` escolhida por `Settings:Instancia`) e confirmar T027–T035 verdes

**Checkpoint**: US1 funcional e testável sozinha via `ExecutarCicloAsync`. É o MVP de regra de negócio.

---

## Phase 4: User Story 2 — Configurar a instalação e o ciclo de varredura (Priority: P2)

**Goal**: validação fail-fast de toda a configuração e laço de ciclos sem sobreposição (primeiro imediato; os seguintes `IntervalSeconds` após o término do anterior), rodando como Windows Service.

**Independent Test**: iniciar o host com configurações válidas e inválidas; com `FakeTimeProvider`, verificar o instante de cada ciclo (CA-16–CA-19); rodar a suíte opt-in contra o LocalDB (CA-20).

### Tests for User Story 2 ⚠️

- [X] T051 [P] [US2] Testes unitários em `tests/InativacaoUsuarioInter.Tests.Unit/Configuracao/ValidacaoConfiguracaoTests.cs`: `IntervalSeconds` 10 e 21600 aceitos; 9, 21601, ausente e `"abc"` rejeitados com mensagem que cita `Settings:IntervalSeconds` e `10–21600` (CA-18); `Instancia` ausente ou `"Matriz"` rejeitada; Inter exige `FolderPathInter`, `FolderPathCondominio` e `ConnectionString:WAccess`; Condomínio exige `FolderPathCondominio` e `ConnectionString:WAccess`; caminho relativo rejeitado; `WAccessInter` presente sem `WAccess` → mensagem de renomeação; validação cruzada `Σ min(AtrasoBaseMs·2^i, AtrasoMaximoMs) ≤ IntervalSeconds·1000`; **nenhuma mensagem contém o valor da connection string** (usar `Password=SegredoTeste123` e verificar a ausência)
- [X] T052 [P] [US2] Testes unitários em `tests/InativacaoUsuarioInter.Tests.Unit/Ciclo/CicloWorkerTests.cs` com `FakeTimeProvider` e um `IOrquestradorCiclo` falso que leva um tempo controlado: o 1º ciclo ocorre na partida; com `IntervalSeconds=60`, o 2º começa exatamente 60 s **após o fim** do 1º (CA-19); nunca há dois ciclos simultâneos; uma exceção num ciclo não encerra o laço; o cancelamento encerra o laço dentro de `ShutdownTimeout`
- [X] T053 [P] [US2] Teste de contrato em `tests/InativacaoUsuarioInter.Tests.Contract/Configuracao/ConfiguracaoContratoTests.cs`: `appsettings.example.json` faz binding e passa na validação quando os placeholders são trocados por valores válidos; os nomes de chave são exatamente os de `contracts/configuracao.md` (inclui a seção `ConnectionString` no singular)
- [X] T054 [P] [US2] Teste de integração opt-in em `tests/InativacaoUsuarioInter.Tests.Integration/LocalDb/LocalDbOptInTests.cs` (pulado se `WACCESS_TESTE_LOCALDB != "1"`): lê `ConnectionString:WAccess` do `appsettings.json` local; escolhe 2 CPFs existentes com `CHState <> 1`; guarda o estado original; processa um arquivo; confirma `CHState = 1` no banco real; **restaura o `CHState` original no teardown**; verifica que a senha não aparece em nenhum log capturado (CA-20)

### Implementation for User Story 2

- [X] T055 [P] [US2] Implementar validadores `IValidateOptions<>` em `src/InativacaoUsuarioInter.Service/Configuracao/ValidadoresConfiguracao.cs`: `Settings:Instancia` ∈ {`Inter`,`Condominio`} sem diferenciar maiúsculas; `Settings:IntervalSeconds` inteiro em `[10, 21600]`; pastas obrigatórias por instância e absolutas; `ConnectionString:WAccess` não vazia; faixas das chaves técnicas de `contracts/configuracao.md` (ex.: `EstabilizacaoSegundos 1–300`, `MaximoRegistros 1–1000`, `AlertaPendenciaHoras 1–720`, `MaxTentativas 0–10`); validação cruzada do orçamento de retry. As mensagens citam chave e regra, **nunca valores de `ConnectionString:*`**
- [X] T056 [P] [US2] Implementar `src/InativacaoUsuarioInter.Service/Configuracao/DetectorChaveLegada.cs`: se `ConnectionString:WAccessInter` existir e `ConnectionString:WAccess` não, falha com "Renomeie ConnectionString:WAccessInter para ConnectionString:WAccess" (research R-16)
- [X] T057 [US2] Criar `src/InativacaoUsuarioInter.Service/Ciclo/CicloWorker.cs` (`BackgroundService`) substituindo o template: executa `IOrquestradorCiclo.ExecutarCicloAsync` imediatamente e depois `await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), timeProvider, ct)` **após o término**, sem `PeriodicTimer`; captura e classifica exceções de ciclo sem derrubar o laço; não usa `.Result`/`.Wait()`/`Thread.Sleep`. Remover `src/InativacaoUsuarioInter.Service/Worker.cs`
- [X] T058 [US2] Atualizar `src/InativacaoUsuarioInter.Service/Program.cs`: `.ValidateOnStart()` em todas as options com os validadores de T055/T056; `AddWindowsService(o => o.ServiceName = "InativacaoUsuarioInter")`; `AddHostedService<CicloWorker>()`; `HostOptions.ShutdownTimeout` vindo da configuração. Uma `OptionsValidationException` na partida encerra o processo com código ≠ 0 e uma mensagem saneada no log (CA-18)

**Checkpoint**: o serviço roda de verdade (`dotnet run`), US1 executa em ciclos e uma configuração inválida impede a partida.

---

## Phase 5: User Story 3 — Sobreviver a falhas transitórias sem perder nem duplicar registros (Priority: P3)

**Goal**: pendências preservadas e retentadas entre ciclos; parte `__pendente-` na entrada; curto-circuito por ciclo; recuperação após crash com auditoria exatamente uma vez; sinal de saúde e alerta de pendência acima de 24 h.

**Independent Test**: com Toxiproxy e o ponto de injeção `IPontoFalha`, provocar queda do banco, latência, arquivo bloqueado, pasta sem permissão e crash em cada etapa; reiniciar e verificar que cada CPF foi inativado e auditado exatamente uma vez (CA-06, CA-07, CA-08, SC-003, SC-004, SC-011).

### Tests for User Story 3 ⚠️

- [X] T059 [P] [US3] Teste de integração em `tests/InativacaoUsuarioInter.Tests.Integration/InjecaoFalhas/FalhasBancoTests.cs` (T026): (a) banco cortado antes do ciclo → todos os registros `Pendente`, arquivo intacto em `Imput`, nenhuma linha em Auditoria/Falhas. Com o banco restaurado e `FakeTimeProvider`: enquanto `PausaSegundos` não termina, os ciclos não chamam o banco e os registros continuam `Pendente`; no primeiro ciclo iniciado depois do fim da pausa, tudo é concluído (CA-06, SC-004); (b) corte depois do 2º de 3 registros → `Output/<base>__sucesso-<h8>` com 2 e `Imput/<base>__pendente-<h8>` com 1 (CA-07); (c) latência acima de `TimeoutComandoSegundos` → classificada como transitória, com retentativas visíveis na métrica `retentativas`; (d) depois de esgotar as tentativas, os registros seguintes do ciclo não chamam o banco (curto-circuito) e o circuito aparece `Aberto` na métrica; (e) **orçamento do ciclo** (análise C1): com `IntervalSeconds=10` e latência injetada que leva cada tentativa até o timeout, o tempo total de retentativas e esperas do ciclo (medido pelos spans) é ≤ 10 s, e os registros restantes ficam `Pendente`; (f) **linhagem** (análise H4): a parte `__pendente-` relida no ciclo seguinte mantém a mesma `chave_idempotencia` e o mesmo `pendente_desde` do registro original
- [X] T060 [P] [US3] Teste de integração em `tests/InativacaoUsuarioInter.Tests.Integration/InjecaoFalhas/FalhasArquivoTests.cs`: arquivo de entrada bloqueado por outro handle → adiado sem erro; `Auditoria/` sem permissão de escrita (ACL negada no Windows, `File.SetUnixFileMode` no Linux) → registro fica com `log_gravado=0` e a linha é gravada exatamente uma vez quando a permissão volta; `Output/` sem permissão → o original continua em `Imput` e não há perda
- [X] T061 [P] [US3] Teste de integração em `tests/InativacaoUsuarioInter.Tests.Integration/InjecaoFalhas/CrashRetomadaTests.cs`: para **cada** ponto de `IPontoFalha` (`AposIntencao`, `AposComandoWAccess`, `AposRegistrarResultado`, `AposAppendLog`, `AposGravarPartes`, `AntesRemoverOriginal`), abortar o host, iniciar um novo sobre o mesmo diretório de estado e as mesmas pastas, e verificar: cada CPF existente com `CHState=1`; **exatamente uma** linha de Auditoria por CPF, com `status=INATIVADO` para os que estavam ativos **e `status=JA_INATIVO` para um CPF que já estava inativo antes do processamento** (análise H1); nenhum arquivo perdido; nenhuma parte duplicada (CA-08, FR-021). **Variante obrigatória com processo real** (Princípio V, análise C2): publicar o serviço num diretório temporário, iniciá-lo como processo filho com `DOTNET_ENVIRONMENT=Testing` e `INATIVACAO_PONTO_FALHA=<Ponto>` para pelo menos `AposComandoWAccess`, `AposRegistrarResultado` e `AposGravarPartes`, aguardar o término por `FailFast`, iniciar um novo processo e aplicar as mesmas verificações
- [X] T062 [P] [US3] Teste unitário em `tests/InativacaoUsuarioInter.Tests.Unit/Ciclo/RecuperacaoTests.cs`: `EmExecucao` com `estado_anterior=Ativo` + passo B com `Inativados=0` → `Inativado`; `EmExecucao` com `estado_anterior=Inativo` → `JaInativo` **sem chamar `InativarAsync`**; `EmExecucao` com `estado_anterior=Inexistente` → `CpfNaoEncontrado`; `Pendente` (crash antes da intenção) → recomeça pela pré-leitura; `log_gravado=0` com linha já presente no log → só marca, sem acrescentar; com linha ausente → acrescenta uma vez
- [X] T063 [P] [US3] Teste de contrato em `tests/InativacaoUsuarioInter.Tests.Contract/Saude/SaudeContratoTests.cs`: `saude.json` com os campos e valores de `contracts/saude.md`; gravação atômica; sem CPF, nome, connection string nem caminho de pasta
- [X] T064 [P] [US3] Teste de integração em `tests/InativacaoUsuarioInter.Tests.Integration/Saude/AlertaPendenciaTests.cs`: com o banco cortado e `FakeTimeProvider` avançando 24 h + 1 ciclo → `status="Alerta"` e alerta `PENDENCIA_ACIMA_LIMITE` com `quantidade`; banco restaurado → o alerta some no 1º ciclo sem pendências acima do limite (FR-042, SC-011); `dependencias.waccess.status="Indisponivel"` enquanto o banco estiver cortado

### Implementation for User Story 3

- [X] T065 [P] [US3] Criar `src/InativacaoUsuarioInter.Service/Ciclo/IPontoFalha.cs` (enum de pontos de T061 + `void Atingido(PontoFalha p)`) com implementação de produção no-op (`PontoFalhaNulo`); os testes registram uma implementação que lança `CrashSimuladoException`. Adicionar `PontoFalhaProcesso`: lê `INATIVACAO_PONTO_FALHA` e chama `Environment.FailFast` ao atingir o ponto indicado. Ela é registrada **somente** quando `DOTNET_ENVIRONMENT=Testing` e nunca está ativa em produção (análise C2). Chamar `Atingido` em cada ponto dentro de `ProcessadorArquivo` e `GerenciadorArquivos`
- [X] T066 [P] [US3] Criar `src/InativacaoUsuarioInter.Service/Ciclo/EstadoDependenciasCiclo.cs` (escopo de ciclo): marca uma dependência como indisponível pelo resto do ciclo quando uma operação esgota as tentativas por erro `Transitoria` **ou o orçamento do ciclo**; expõe o estado para `ProcessadorArquivo` e `PublicadorSaude`. Ajustar `src/InativacaoUsuarioInter.Service/Ciclo/OrquestradorCiclo.cs` para criar uma instância nova de `OrcamentoCiclo` (tipo criado na T022) no início de cada ciclo e passá-la a toda execução de pipeline via `ResilienceContext` (`OrcamentoCiclo.Chave`) (research R-06, análise C1)
- [X] T067 [US3] Estender `src/InativacaoUsuarioInter.Service/Ciclo/ProcessadorArquivo.cs`: falha transitória (ou dependência indisponível no ciclo) → `RegistrarPendenciaAsync` (`tentativas++`, `pendente_desde` só na 1ª vez), resultado `PendenteTransitorio` e log técnico sem CPF; nenhuma linha em Auditoria/Falhas; o cancelamento do host deixa o registro corrente em `EmExecucao` (sem estado parcial irrecuperável) (depende de T065, T066)
- [X] T068 [US3] Estender `src/InativacaoUsuarioInter.Service/Arquivos/GerenciadorArquivos.cs`: se houver `PendenteTransitorio`, gravar também `Imput/<baseRaiz>__pendente-<h8>.<ext>` e registrá-la no diário como novo `arquivo` com o mesmo `hash_raiz` e os registros pendentes preservados (mesma `chave_idempotencia`); marcar o original como `Segregado`; no início do ciclo, remover `.tmp` órfãos **apenas** dos nomes gerados pelo próprio serviço
- [X] T069 [US3] Implementar `src/InativacaoUsuarioInter.Service/Ciclo/Recuperacao.cs` e chamá-lo no início de `OrquestradorCiclo.ExecutarCicloAsync`: para `ListarRecuperacaoAsync()`, registros `EmExecucao` → aplicam `ClassificarResultado` com o `estado_anterior` **gravado** (`Ativo` → reexecuta `InativarAsync`, resultado `Inativado` se `Existentes ≥ 1`; `Inativo` → `JaInativo` sem UPDATE; `Inexistente` → `CpfNaoEncontrado`). É proibido inferir o estado anterior pela existência da intenção (análise H1); resultado com `log_gravado=0` → `ContemLinhaAsync(linha_log)`, acrescenta se ausente e depois `MarcarLogGravadoAsync`; arquivos com partes gravadas e original ainda na entrada → conclui a remoção (research R-08)
- [X] T070 [US3] Ajustar `src/InativacaoUsuarioInter.Service/Entrada/DetectorElegiveis.cs` para classificar cada elegível como **novo** (hash desconhecido no diário) ou **retentativa** (parte `__pendente-` conhecida, identificada pela mesma `ResolverHashRaizAsync` que o `LeitorArquivo` usa na T040, nunca pelo nome do arquivo) e expor `HaArquivosNovos` para o orquestrador (base do gatilho de replicação em US4)
- [X] T071 [US3] Implementar `src/InativacaoUsuarioInter.Service/Saude/IPublicadorSaude.cs` e `PublicadorSaudeArquivo.cs`: gravação atômica de `<DiretorioEstado>/saude.json` na partida e no início e fim de cada ciclo, conforme `contracts/saude.md` (`status` `Saudavel|Degradado|Alerta`, `dependencias.*.status/circuito/ultimaFalha{em,categoria}`, `backlog`, `alertas[]`); alerta `PENDENCIA_ACIMA_LIMITE` quando `ObterPendenciaMaisAntigaAsync() < agora - AlertaPendenciaHoras`, com aviso no log técnico; `PERMISSAO_NEGADA` e `CONFIGURACAO_WACCESS` a partir das categorias de erro; atualiza as métricas `backlog_pendente` e `idade_pendencia_mais_antiga_s`
- [X] T072 [US3] Registrar `IPontoFalha`, `EstadoDependenciasCiclo` (escopo por ciclo), `Recuperacao` e `IPublicadorSaude` em `src/InativacaoUsuarioInter.Service/Program.cs` e chamar o publicador em `CicloWorker`/`OrquestradorCiclo`; confirmar T059–T064 verdes

**Checkpoint**: US1 + US2 + US3 resistem a falhas e crashes sem perda nem duplicação.

---

## Phase 6: User Story 4 — Replicar ao Condomínio os registros concluídos no Inter (Priority: P4)

**Goal**: depois dos arquivos novos do ciclo, copiar para `{FolderPathCondominio}/Imput` cada arquivo de `Output` ainda não entregue, com visibilidade atômica, hash conferido, sidecar `traceparent` e registro durável da entrega.

**Independent Test**: com arquivos em `Output` e nenhum arquivo novo em `Imput`, rodar ciclos e verificar a entrega única e íntegra, inclusive após falha de destino e reinício (CA-09, CA-10, SC-005).

### Tests for User Story 4 ⚠️

- [X] T073 [P] [US4] Teste de contrato em `tests/InativacaoUsuarioInter.Tests.Contract/Replicacao/ReplicacaoContratoTests.cs`: protocolo de `contracts/replicacao.md` (cópia byte a byte; `.tmp` nunca visível com o nome final; `<nome>.traceparent` no formato W3C `00-<32hex>-<16hex>-<2hex>` gravado antes do rename; colisão com mesmo hash → no-op; colisão com hash diferente → `<base>__<h8>.<ext>`)
- [X] T074 [P] [US4] Teste de integração em `tests/InativacaoUsuarioInter.Tests.Integration/Replicacao/ReplicacaoTests.cs`: CA-09 (arquivo novo em `Imput` → replica só depois de processá-lo; uma parte `__pendente-` em `Imput` não bloqueia a entrega); CA-10 (destino sem permissão → origem intacta, `entrega.estado=NaoEntregue`, alerta de saúde; no ciclo seguinte, com permissão → entregue); reinício do host → nenhuma cópia nova; 100 ciclos consecutivos → 0 entregas duplicadas (SC-005); partes `__falhas-`/`__pendente-` e arquivos de `Falhas/` **nunca** são entregues; arquivo de `Output` continua lá após a entrega; **arquivo em `Output` sem linha em `entrega` mas cujo hash o diário conhece como parte `Integral`/`Sucesso`** (por exemplo, gerado no período do MVP, antes da US4) é entregue exatamente uma vez (análise M2); **arquivo desconhecido do diário** (copiado à mão para `Output`) **não** é entregue, permanece em `Output`, gera um aviso técnico sem CPF e o alerta `ARQUIVO_OUTPUT_DESCONHECIDO` em `saude.json` (FR-035, análise N2)
- [X] T075 [P] [US4] Teste de integração em `tests/InativacaoUsuarioInter.Tests.Integration/Replicacao/CopiaParcialTests.cs`: com `IPontoFalha` entre a cópia do `.tmp` e o rename → o destino tem só o `.tmp`, que não é elegível; no ciclo seguinte o `.tmp` órfão é removido e a entrega é concluída (CA-14 lado receptor)

### Implementation for User Story 4

- [X] T076 [US4] Implementar `src/InativacaoUsuarioInter.Service/Replicacao/IServicoReplicacao.cs` e `ServicoReplicacao.cs`. **Reconciliação antes de entregar** (FR-032, análise M2): varrer `{FolderPathInter}/Output` (só `.json`/`.txt`, ignorando `.tmp`), calcular o SHA-256 de cada arquivo e, **somente quando `ObterParteAsync(hash)` indicar uma parte `Integral` ou `Sucesso` gerada pelo próprio Inter**, criar uma linha `entrega` com `estado=NaoEntregue` se ela ainda não existir. Um arquivo com hash desconhecido não é entregue (FR-035, análise N2): registra um aviso técnico com nome e hash (sem conteúdo) e informa o alerta `ARQUIVO_OUTPUT_DESCONHECIDO` ao `IPublicadorSaude`. A fonte de verdade é a pasta `Output` cruzada com o diário, não só as linhas criadas na gravação (T077). Depois, para cada entrega pendente, seguir os 6 passos de `contracts/replicacao.md`: remover `.tmp` órfão; copiar para `<FolderPathCondominio>/Imput/<nome>.tmp` via pipeline `pasta-replicacao`; reler e comparar o SHA-256 (se diferir, remover o `.tmp` e falhar como transitório); gravar `<nome>.traceparent` com o `Activity.Current` do span `replicacao.copiar`; `File.Move` para o nome final, tratando colisão por hash; `RegistrarEntregaAsync(Entregue, nome_destino, entregue_em, traceparent)`; em falha, registrar `tentativas++` e `ultima_falha` (só a categoria)
- [X] T077 [US4] Estender `src/InativacaoUsuarioInter.Service/Arquivos/GerenciadorArquivos.cs`: quando a política da instância tem `Replica = true` (Inter), toda parte `Integral`/`Sucesso` gravada em `Output` gera uma linha em `entrega` com `estado=NaoEntregue`, na mesma transação do `RegistrarParteAsync`. É uma otimização: a reconciliação da T076 garante a entrega mesmo sem essa linha. A chave da tabela é o hash, então a reconciliação nunca duplica a linha
- [X] T078 [US4] Estender `src/InativacaoUsuarioInter.Service/Ciclo/OrquestradorCiclo.cs`: só quando `IPoliticaFluxo.Replica` é verdadeiro, depois de processar todos os elegíveis **novos** do ciclo (T070), chamar `IServicoReplicacao.EntregarPendentesAsync(ct)`; respeitar `EstadoDependenciasCiclo` para `replicacao`
- [X] T079 [US4] Estender `src/InativacaoUsuarioInter.Service/Saude/PublicadorSaudeArquivo.cs` com `dependencias.replicacao` (Inter), `backlog.entregasPendentes` e o alerta `ARQUIVO_OUTPUT_DESCONHECIDO` (com `quantidade`; sai no 1º ciclo em que não houver arquivo desconhecido em `Output`); registrar `IServicoReplicacao` em `src/InativacaoUsuarioInter.Service/Program.cs`; confirmar T073–T075 verdes

**Checkpoint**: o Inter entrega ao Condomínio de forma íntegra e única.

---

## Phase 7: User Story 5 — Inativar no Condomínio os registros recebidos (Priority: P5)

**Goal**: a instância `Condominio` aplica as mesmas regras sobre `{FolderPathCondominio}/Imput`, no WAccess do condomínio, com destino `Processados`, sem replicar, continuando o trace do Inter.

**Independent Test**: com `Settings:Instancia=Condominio`, processar arquivos em `Condominio/Imput` e verificar o banco, os logs locais, `Processados` e a ausência de `Output` (CA-11, CA-12).

### Tests for User Story 5 ⚠️

- [X] T080 [P] [US5] Teste de contrato em `tests/InativacaoUsuarioInter.Tests.Contract/EstruturaDiretorios/EstruturaCondominioTests.cs`: cria `Imput`, `Processados`, `Auditoria` e `Falhas`; **não** cria nem escreve em `Output` (mesmo que exista, como em `Files/Condominio/Output`)
- [X] T081 [P] [US5] Teste de integração em `tests/InativacaoUsuarioInter.Tests.Integration/FluxoCondominio/FluxoCondominioTests.cs`: CA-11 (lê só `Condominio/Imput`, grava só `Condominio/Auditoria/log_inativacao.log` com `| Condominio |`); CA-12 (concluído → `Processados/`, nenhuma replicação, linha `entrega` nunca criada); CPF inexistente → `Condominio/Falhas/log_inativacao.log` + parte `__falhas-`; parcial com banco cortado → `Processados/<base>__sucesso-<h8>` + `Imput/<base>__pendente-<h8>`; `.traceparent` presente → span `arquivo` com o `parent` do trace do Inter, e o sidecar é movido junto com o arquivo; `.traceparent` ausente → nova raiz de trace, sem erro
- [X] T082 [P] [US5] Teste ponta a ponta em `tests/InativacaoUsuarioInter.Tests.Integration/PontaAPonta/InterCondominioTests.cs`: dois hosts (Inter e Condomínio) sobre pastas temporárias compartilhadas e dois bancos (2 databases no mesmo container); arquivo com 3 CPFs (1 ausente no Inter) → 2 inativados no Inter, parte de sucesso replicada, 2 inativados no Condomínio; mesmo `trace-id` nos dois lados; nenhuma duplicação após reiniciar ambos

### Implementation for User Story 5

- [X] T083 [P] [US5] Implementar `src/InativacaoUsuarioInter.Service/Ciclo/PoliticaFluxoCondominio.cs`: raiz = `FolderPathCondominio`, destino de concluídos = `Processados`, `Replica = false`, pastas obrigatórias `Imput|Processados|Auditoria|Falhas`
- [X] T084 [P] [US5] Implementar `src/InativacaoUsuarioInter.Service/Entrada/LeitorTraceparent.cs`: lê `<arquivo>.traceparent` se existir, valida o formato W3C com `ActivityContext.TryParse` e devolve o `ActivityContext` usado como parent do span `arquivo`; formato inválido ou ausente → `null`, sem falha. O valor é guardado em `arquivo.traceparent` no diário
- [X] T085 [US5] Estender `src/InativacaoUsuarioInter.Service/Ciclo/ProcessadorArquivo.cs` (usar T084 ao abrir o span `arquivo`) e `src/InativacaoUsuarioInter.Service/Arquivos/GerenciadorArquivos.cs` (mover o sidecar `.traceparent` junto com o arquivo para o destino final ou para `Falhas/`)
- [X] T086 [US5] Selecionar `PoliticaFluxoCondominio` em `src/InativacaoUsuarioInter.Service/Program.cs` quando `Settings:Instancia=Condominio`; marcar `dependencias.replicacao.status="NaoAplicavel"` em `PublicadorSaudeArquivo`; confirmar T080–T082 verdes

**Checkpoint**: as 5 histórias funcionam e o fluxo ponta a ponta Inter → Condomínio está validado.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: gates de qualidade da constituição (telemetria, idempotência, performance, CI), operação e documentação.

- [X] T087 [P] Teste de cobertura total de telemetria em `tests/InativacaoUsuarioInter.Tests.Integration/Telemetria/CoberturaTelemetriaTests.cs`: percorre os fluxos US1–US5 com exportador em memória e falha se alguma operação de domínio (ciclo, arquivo, registro, `waccess.inativar`, `arquivo.mover`, `replicacao.copiar`) rodar sem span ou sem os atributos obrigatórios; varre spans, logs técnicos, métricas e `saude.json` em busca de qualquer CPF ou nome dos dados de teste e da senha de teste (SC-008, gate 4)
- [X] T088 [P] Teste de idempotência em `tests/InativacaoUsuarioInter.Tests.Integration/Idempotencia/ExecucaoDuplaTests.cs`: para cada mutação (comando WAccess, append de Auditoria, append de Falhas, gravação de cada tipo de parte, rejeição, entrega), executar duas vezes com a mesma entrada e comparar o estado final (banco, pastas, logs, diário): deve ser idêntico (Princípio III, gate 3)
- [X] T089 [P] Teste de carga em `tests/InativacaoUsuarioInter.Tests.Integration/Performance/CargaTests.cs`: arquivo com 1.000 registros válidos e existentes → concluído em ≤ 5 min; p95 de `duracao_registro_ms` ≤ 500 ms; vazão ≥ 300 registros/min; memória de trabalho ≤ 512 MB; arquivo típico (3 registros) em ≤ 10 s (SC-009). **Bloqueio por regressão** (Princípio VI, análise C3): gravar e comparar com `tests/InativacaoUsuarioInter.Tests.Integration/Performance/linha-base.json` (p95 por registro, vazão, duração do arquivo de 1.000). Se alguma métrica piorar mais de 20%, o teste falha. A linha de base é criada na 1ª execução e só é sobrescrita com `ATUALIZAR_LINHA_BASE=1`, num commit que cita o ADR
- [X] T090 [P] Estender o CI criado na T009 (`.github/workflows/ci.yml`): passo de `dotnet publish` para a variante de crash com processo real (T061), execução do teste de carga com comparação à linha de base (T089) e do teste de cobertura total de telemetria (T087), publicação dos resultados e da linha de base como artefato. O merge fica bloqueado se alguma falhar (gates 3, 4 e 7)
- [X] T091 [P] Criar a documentação operacional `docs/operacao.md`: instalação com `dotnet publish` + `sc.exe` (quickstart §5), conta de serviço e ACL restrita em `Auditoria/`, `Falhas/` e no diretório de estado (FR-038), como interpretar `saude.json` e a regra de liveness, configuração do `Telemetria:OtlpEndpoint`, recomendação de trocar a senha de teste presente no histórico do git (R-15)
- [ ] T092 [P] (Opcional) Ajustar `src/InativacaoUsuarioInter.Setup/InativacaoUsuarioInter.Setup.vdproj` para empacotar a saída de `dotnet publish -c Release` (não mais `obj/Debug/.../apphost.exe`) e registrar o Windows Service por custom action
- [X] T093 Revisar o código de `src/InativacaoUsuarioInter.Service/` contra os gates do plano: nenhuma chamada externa sem pipeline, nenhum `.Result`/`.Wait()`/`Thread.Sleep`/`Console.WriteLine`, nenhum `catch` genérico sem classificação, todos os tipos não públicos `internal`
- [X] T094 Executar a validação manual de `specs/001-inativacao-usuarios-waccess/quickstart.md §4` contra o LocalDB de testes (passos 1–10, incluindo restaurar o `CHState`) e registrar o resultado no fim de `docs/operacao.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Fase 1)**: sem dependências. **T001 vem antes de qualquer commit** (gate de segredo).
- **Foundational (Fase 2)**: depende da Fase 1 e bloqueia todas as histórias.
- **US1 (Fase 3)**: depende da Fase 2.
- **US2 (Fase 4)**: depende da Fase 2; o `CicloWorker` chama `IOrquestradorCiclo` (T049). Para testar sozinha, T052 usa um orquestrador falso.
- **US3 (Fase 5)**: depende de US1 (estende `ProcessadorArquivo`, `GerenciadorArquivos`, `OrquestradorCiclo`).
- **US4 (Fase 6)**: depende de US1 (partes em `Output`) e de T070 (US3), que distingue arquivo novo de retentativa.
- **US5 (Fase 7)**: depende de US1; T081/T082 usam o sidecar produzido em US4 (T082 depende de US4).
- **Polish (Fase 8)**: depende das histórias desejadas.

### User Story Dependencies

```text
Setup → Foundational ─┬─► US1 (P1) ─┬─► US3 (P3) ─► US4 (P4) ─┐
                      │             └─────────────► US5 (P5) ─┼─► Polish
                      └─► US2 (P2) ────────────────────────────┘
```

### Within Each User Story

- Testes (contrato → unitário → integração) **antes** e falhando; implementação; registro em `Program.cs`; testes verdes.
- Modelos e funções puras (normalização, parsers, formatadores) antes dos serviços com E/S; serviços antes do orquestrador.

### Parallel Opportunities

- Fase 1: T002, T003, T005–T008 e T010 em paralelo.
- Fase 2: T011–T013 (testes), T014–T019 e T024–T026 em paralelo; T020→T021 e T022→T023 em sequência.
- **US1 e US2 podem rodar em paralelo** depois da Fase 2 (arquivos diferentes; só `Program.cs` é compartilhado, então integrar T050 e T058 em sequência).
- Dentro de cada história, todas as tarefas de teste [P] em paralelo; em US1, T036–T038 e T043–T045 em paralelo.

---

## Parallel Example: User Story 1

```text
# Testes de US1 (todos [P], escrever primeiro):
Task: "T027 Contrato de formatos em tests/InativacaoUsuarioInter.Tests.Contract/ArquivosEntrada/FormatoEntradaTests.cs"
Task: "T028 Contrato de logs em tests/InativacaoUsuarioInter.Tests.Contract/Logs/LayoutLogTests.cs"
Task: "T029 Contrato WAccess em tests/InativacaoUsuarioInter.Tests.Contract/WAccess/InativacaoWAccessTests.cs"
Task: "T031 Unitários de entrada em tests/InativacaoUsuarioInter.Tests.Unit/Entrada/*"
Task: "T033 Integração FluxoInter em tests/InativacaoUsuarioInter.Tests.Integration/FluxoInter/FluxoInterTests.cs"

# Funções puras de US1 (todas [P]):
Task: "T036 NormalizadorCpf em src/InativacaoUsuarioInter.Service/Entrada/NormalizadorCpf.cs"
Task: "T037 ParserJson em src/InativacaoUsuarioInter.Service/Entrada/ParserJson.cs"
Task: "T038 ParserTxt em src/InativacaoUsuarioInter.Service/Entrada/ParserTxt.cs"
Task: "T043 FormatadorLinhaLog em src/InativacaoUsuarioInter.Service/Registros/FormatadorLinhaLog.cs"
Task: "T045 NomeadorPartes/SerializadorPartes em src/InativacaoUsuarioInter.Service/Arquivos/"
```

## Parallel Example: User Story 3

```text
Task: "T059 FalhasBancoTests"   Task: "T060 FalhasArquivoTests"   Task: "T061 CrashRetomadaTests"
Task: "T062 RecuperacaoTests"   Task: "T063 SaudeContratoTests"   Task: "T064 AlertaPendenciaTests"
Task: "T065 IPontoFalha"        Task: "T066 EstadoDependenciasCiclo"
```

---

## Implementation Strategy

### MVP First

1. Fase 1 (Setup), começando por **T001** (segredo fora do git).
2. Fase 2 (Foundational).
3. Fase 3 (US1): **validar** com T027–T035 (regra de negócio completa no Inter).
4. Fase 4 (US2): torna o MVP **executável como serviço**. O MVP entregável é **US1 + US2**.

### Incremental Delivery

1. MVP (US1 + US2): o Inter inativa em ciclos.
2. \+ US3: robustez (requisito da constituição antes de qualquer produção).
3. \+ US4: entrega ao Condomínio.
4. \+ US5: Condomínio processa; fluxo ponta a ponta completo.
5. Polish: gates de CI, carga e telemetria total.

> Por exigência da constituição (Princípios II, III e V), **nenhuma instalação de produção** deve ocorrer sem US3 e T087–T090 concluídos.

### Parallel Team Strategy

1. Equipe junta: Setup + Foundational.
2. Em seguida: Dev A → US1; Dev B → US2.
3. Depois de US1: Dev A → US3 → US4; Dev B → US5 (T080, T081, T083–T086), com T082 depois de US4.

---

## Notes

- **Revisão pós-análise (2026-11-11)**: as tarefas T020, T021, T022, T029, T040, T042, T048, T059, T061, T062, T065, T066, T069, T070, T089 e T090 foram ampliadas para corrigir C1 (orçamento do ciclo), C2 (crash de processo real), C3 (regressão de performance), H1 (pré-leitura com `estado_anterior`) e H4 (`HashRaiz` resolvido antes da validação). Os IDs foram mantidos.
- **Revisão pós-análise 2 (2026-11-11)**: N1, `OrcamentoCiclo` criado na fundação (T022), com a T066 só criando a instância por ciclo; M2, a replicação reconcilia a pasta `Output` com o diário pelo hash (T074, T076, T077); M4, o CI básico entra no Setup (T009) e a T090 só o estende.
- **Revisão pós-análise 3 (2026-11-11)**: N2, a reconciliação de `Output` só entrega hashes que o diário reconhece como parte `Integral`/`Sucesso`; arquivo desconhecido não é replicado e gera o alerta `ARQUIVO_OUTPUT_DESCONHECIDO` (T020, T074, T076, T079); M1, o SC-004 passa a considerar a pausa do circuito (T059).
- **Decisão pendente (research R-02)**: CPF com mais de um cadastro em `CHMain` hoje inativa todos (regra literal; a spec já reflete o esquema real). Se o negócio decidir bloquear, a mudança fica concentrada em `RepositorioWAccessSql` (T042) e no teste T029. Confirmar antes de T042.
- Os dados de teste usam apenas CPFs demonstrativos (`22222222222`, `33333333333`, `44444444444`, `01234567890`); nada depende de dado real, exceto o T054 opt-in, que restaura o estado.
- Commitar ao fim de cada tarefa ou grupo lógico, sem nunca commitar `appsettings.json`.
- Parar em cada checkpoint para validar a história de forma independente.

---

## Phase 9: Convergence

- [X] T095 CRITICAL: Executar a leitura do sidecar `.traceparent` (`src/InativacaoUsuarioInter.Service/Entrada/LeitorTraceparent.cs`, chamada em `Ciclo/ProcessadorArquivo.cs`) e a verificação `ContemLinhaAsync` da recuperação (`Ciclo/Recuperacao.cs`, `Registros/IRegistros.cs`) dentro do pipeline `pasta-local` (timeout + retry + circuito), e substituir os `catch (IOException)`/`catch (UnauthorizedAccessException)` silenciosos do `LeitorTraceparent` por classificação via `ProvedorPipelines.Classificar` + log técnico estruturado (sem CPF), mantendo o comportamento de "sidecar ausente/inválido → nova raiz de trace" per Constitution II (contradicts)
- [X] T096 CRITICAL: Adicionar a métrica obrigatória `idade_arquivo_entrada_mais_antigo_s` (idade do arquivo `.json`/`.txt` mais antigo em `Imput`, inclusive adiados por instabilidade ou bloqueio) calculada em `src/InativacaoUsuarioInter.Service/Entrada/DetectorElegiveis.cs`, publicada em `Telemetria/Instrumentacao.cs` (`EstadoMetricas`) e em `saude.json` (`backlog.arquivoEntradaMaisAntigoDesde`, atualizar `contracts/saude.md`), com teste em `tests/InativacaoUsuarioInter.Tests.Integration/Telemetria/` usando arquivo mantido bloqueado per Constitution IV, FR-041 (missing)
- [X] T097 Sondar ativamente a readiness das dependências a cada ciclo — `SELECT 1` no WAccess pelo pipeline `waccess` e existência/acesso de `<raiz>/Imput`, `<raiz>/Auditoria` e (Inter) `<FolderPathCondominio>/Imput` pelos pipelines de pasta — em `src/InativacaoUsuarioInter.Service/Saude/PublicadorSaudeArquivo.cs` (ou novo `Saude/SondaDependencias.cs`), refletindo em `dependencias.*.status` mesmo sem arquivos a processar; teste em `tests/InativacaoUsuarioInter.Tests.Integration/Saude/` com banco cortado e entrada vazia per FR-040 (partial)
- [X] T098 Evitar recalcular o SHA-256 de todo o `Output` a cada ciclo na reconciliação de `src/InativacaoUsuarioInter.Service/Replicacao/ServicoReplicacao.cs`: registrar no diário (`entrega` ou nova tabela `output_conhecido`: nome, tamanho, data de escrita, hash) e só calcular o hash de arquivos novos ou alterados, mantendo as regras M2/N2; teste em `tests/InativacaoUsuarioInter.Tests.Integration/Replicacao/ReplicacaoTests.cs` per Constitution VI, plan R-11 (partial)
- [X] T099 Verificar o orçamento de memória do **serviço** (≤ 512 MB) no teste de carga: executar o serviço como processo filho (como em `CrashRetomadaTests`) com o arquivo de 1.000 registros, medir o pico de memória do processo filho e substituir a asserção atual sobre o processo de teste em `tests/InativacaoUsuarioInter.Tests.Integration/Performance/CargaTests.cs` per SC-009 (partial)
- [X] T100 Verificar na partida se `<raiz>/Auditoria`, `<raiz>/Falhas` e `Processamento:DiretorioEstado` concedem leitura a grupos amplos (Windows: `Everyone`/`Users`/`Authenticated Users`; Unix: bits de grupo/outros) e, nesse caso, emitir aviso técnico e o alerta `PERMISSAO_ABERTA_DADOS_PESSOAIS` em `saude.json`, em `src/InativacaoUsuarioInter.Service/Saude/` + `contracts/saude.md` + teste em `tests/InativacaoUsuarioInter.Tests.Integration/Saude/` per FR-038 (partial)
- [X] T101 Tornar configurável o timeout de espera do diário SQLite (`PRAGMA busy_timeout`, hoje fixo em 5000 ms em `src/InativacaoUsuarioInter.Service/Estado/ArmazenamentoEstadoSqlite.cs`) via `Processamento:TimeoutDiarioMs` (1000–60000, padrão 5000) em `Configuracao/Opcoes.cs` + `ValidadoresConfiguracao.cs` + `contracts/configuracao.md` + `appsettings.example.json` per Constitution "Configuração" (partial)
- [X] T102 Tornar `internal` os tipos de implementação de `src/InativacaoUsuarioInter.Service/` que não fazem parte das interfaces públicas dos módulos (ex.: `ArmazenamentoEstadoSqlite` já é; aplicar a `GerenciadorArquivos`, `ProcessadorArquivo`, `RepositorioWAccessSql`, `ServicoReplicacao`, `PublicadorSaudeArquivo`, `EscritorLogAppendOnly`, validadores, parsers), ajustando a acessibilidade das interfaces/DTOs envolvidos e mantendo `InternalsVisibleTo` para os testes per plan "Structure Decision", T093 (contradicts)
- [X] T103 Registrar ADR em `docs/adr/0001-desvios-de-implementacao.md` justificando: (a) Polly.Core direto em vez de `Microsoft.Extensions.Resilience` (relógio do sistema nas esperas independente do `TimeProvider` de domínio), (b) spans manuais de banco em vez de `OpenTelemetry.Instrumentation.SqlClient`, (c) proxy TCP em processo em vez do Toxiproxy (T026), (d) processamento sequencial sem chave de paralelismo — ou criar `Processamento:Paralelismo` (padrão 1) — e referenciar o ADR em `plan.md` (Complexity Tracking) per plan "Primary Dependencies", research R-06/R-14/R-17, Constitution VI (contradicts)
- [X] T104 Atualizar `specs/001-inativacao-usuarios-waccess/data-model.md` §2 (tabela `arquivo`) com as colunas implementadas `linha_log TEXT NULL` e `log_gravado INTEGER NOT NULL DEFAULT 0` (linha de Falhas pré-formatada de arquivo rejeitado, exatamente uma vez) per data-model §2, FR-021 (partial)