# Implementation Plan: Inativação e Replicação de Usuários WAccess (Inter → Condomínio)

**Branch**: `main` (feature `001-inativacao-usuarios-waccess`) | **Date**: 2026-11-11 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `specs/001-inativacao-usuarios-waccess/spec.md`

## Summary

Transformar o Worker Service `src/InativacaoUsuarioInter.Service`, hoje um template vazio, em um único Windows Service configurável por `Settings:Instancia`. A cada ciclo (10–21600 s, contados a partir do fim do ciclo anterior), o serviço lê arquivos `.json`/`.txt` estáveis em `<raiz>/Imput`, valida e normaliza os CPFs e inativa cada um no WAccess da instalação. A inativação usa um comando parametrizado e condicional sobre `dbo.CHMain`, que conta as linhas por `OUTPUT … INTO` por causa do trigger existente na tabela. O serviço grava os logs contratuais de Auditoria e Falhas e segrega os arquivos em partes de sucesso, falha e pendência, sempre com escrita atômica.

Na instância Inter, depois dos arquivos novos do ciclo, o serviço replica para `Condominio/Imput` os arquivos de `Output` ainda não entregues, com hash conferido e rename atômico. Um diário local em SQLite garante idempotência e retomada após crash. A resiliência (timeout, retry com jitter, circuit breaker) usa `Microsoft.Extensions.Resilience`, e a telemetria é OpenTelemetry, sem dados pessoais. A saúde é publicada em `saude.json`, sem endpoint de rede. Detalhes em [research.md](./research.md).

## Technical Context

**Language/Version**: C# / .NET 10 (`Microsoft.NET.Sdk.Worker`, `net10.0`, `Nullable` habilitado, warnings tratados como erro em código novo). SDK 10.0.404 instalado.

**Primary Dependencies**: `Microsoft.Extensions.Hosting` + `.WindowsServices`; `Microsoft.Data.SqlClient`; `Microsoft.Data.Sqlite`; `Microsoft.Extensions.Resilience` (Polly v8); `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.SqlClient`. Sem ORM, sem mensageria, sem dependência de RPC.

**Storage**: (1) WAccess SQL Server: somente `dbo.CHMain.CHState` é escrito; (2) SQLite local (`<DiretorioEstado>/estado.db`) para o diário de progresso e de entregas; (3) arquivos em compartilhamento: entrada, saídas, logs contratuais.

**Testing**: xUnit v3; Testcontainers (`MsSql`, `Toxiproxy`); `FakeTimeProvider`; `OpenTelemetry.Exporter.InMemory`; sistema de arquivos temporário real. Suíte opt-in contra o LocalDB `W_ACCESS` (CA-20).

**Target Platform**: Windows Server (Windows Service), uma instalação no Inter e outra no Condomínio. Pastas locais ou compartilhamentos SMB já montados.

**Project Type**: serviço de background (Worker Service), monólito modular, sem interface de usuário nem API.

**Performance Goals**: ≥ 300 registros/min; p95 ≤ 500 ms por registro, sem contar novas tentativas; arquivo típico (3 registros) em ≤ 10 s; arquivo máximo (1.000 registros) em ≤ 5 min.

**Constraints**: memória ≤ 512 MB; arquivo ≤ 1 MB e ≤ 1.000 registros; ciclos sem sobreposição; a soma das esperas de nova tentativa cabe no intervalo mínimo de 10 s; nenhum CPF ou nome na telemetria; nenhuma credencial em log; nenhuma mudança no esquema do WAccess.

**Scale/Scope**: média de 3 registros por arquivo; `CHMain` com ≈26 mil linhas no ambiente de testes; 2 instalações; 1 processo por instalação.

Não restou nenhum item `NEEDS CLARIFICATION`: todos foram resolvidos em [research.md](./research.md) (R-01 a R-17). **Divergência a confirmar com o responsável de negócio** (não bloqueia o plano porque já há um padrão definido): R-02, CPF duplicado em `CHMain`, apesar da Clarification Q2.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Princípio | Exigência-chave | Como o plano atende | Pré-Fase 0 | Pós-Fase 1 |
| --- | --- | --- | --- | --- |
| **I. Monólito modular** | 1 processo; módulos com interfaces; diferença por `Settings:Instancia`; sem rede/fila entre módulos; endpoint novo só com ADR | 1 Worker Service; módulos por namespace com interfaces públicas (estrutura abaixo); o fluxo Inter/Condomínio é escolhido por configuração; o SQLite pertence só ao módulo `Estado`; a saúde fica em arquivo, sem endpoint HTTP (R-13) | ✅ | ✅ |
| **II. Falhas de rede e E/S** | Timeout explícito; retry exponencial + jitter só para transitórios; breaker por dependência; classificação obrigatória; orçamento por ciclo; biblioteca padrão | Pipelines nomeados `waccess`, `pasta-local` e `pasta-replicacao` (R-06); tabela de classificação (R-05); **orçamento de retentativas por ciclo = `IntervalSeconds`**, aplicado em tempo de execução: a duração das tentativas extras e as esperas são descontadas, e ao esgotar o orçamento os registros restantes ficam pendentes (R-06, análise C1); validação estática das esperas como checagem de sanidade ([configuracao.md](./contracts/configuracao.md)); curto-circuito por ciclo; `CancellationToken` em toda E/S | ✅ | ✅ |
| **III. Idempotência** | Mesmo estado final em 1 ou N execuções; chave determinística; `UPDATE` condicional; temp + rename; at-least-once; progresso durável | Chave SHA-256 de instância, hash raiz **resolvido no diário antes da validação** e CPF (análise H4); pré-leitura com `estado_anterior` gravado junto com a intenção, antes do `UPDATE … AND CHState <> 1`, o que garante classificação correta em retry e recuperação (R-03/R-08, análise H1); linha de log pré-formatada; no-op por hash em arquivos e entregas (R-10, R-11) | ✅ | ✅ |
| **IV. Telemetria OTel** | Span em ciclo, arquivo e registro; atributos de domínio; propagação na replicação; logs estruturados; métricas mínimas; sem dado pessoal; exportação não bloqueante | R-14; `traceparent` em sidecar ([replicacao.md](./contracts/replicacao.md)); teste de cobertura com exportador em memória (SC-008) | ✅ | ✅ |
| **V. Contrato + injeção de falhas** | Contratos de arquivos, diretórios, logs, consulta WAccess e replicação; dependências reais; caos mínimo; red → green | 6 contratos em [contracts/](./contracts/) com testes dedicados; SQL Server em container com réplica fiel de `CHMain` e trigger; Toxiproxy; ACL negada; crash em duas camadas: `IPontoFalha` em processo **e processo filho real encerrado por `Environment.FailFast`** (R-17, análise C2) | ✅ | ✅ |
| **VI. Performance** | Orçamento declarado; consulta parametrizada e indexada; sem N+1; leitura limitada; recursos limitados; sem bloqueio síncrono | Orçamento na spec; tipo do parâmetro igual ao da coluna (R-01); leitura limitada a 1 MB; processamento sequencial (paralelismo 1); teste de carga com 1.000 registros **comparado a uma linha de base versionada; piora > 20% falha o CI** (R-17, análise C3). **Ressalva**: `IdNumber` não é a coluna líder de nenhum índice (ver Complexity Tracking) | ⚠️ justificado | ⚠️ justificado |
| **Restrições técnicas** | .NET 10; configuração validada na partida; **segredos não versionados**; shutdown gracioso; health; LGPD | `ValidateOnStart`; `ShutdownTimeout`; `saude.json`; **R-15** tira `appsettings.json` do git e adiciona `appsettings.example.json`; a senha local continua explícita (documento-base §4.1) | ❌ → ✅ (corrigido pela tarefa R-15) | ✅ condicionado à tarefa R-15 |

**Resultado do gate**: APROVADO. O único desvio (VI, índice) está justificado abaixo. A violação de segredo versionado já existia no repositório e é corrigida por uma tarefa obrigatória deste plano (R-15), que precisa ser **a primeira** da implementação.

## Project Structure

### Documentation (this feature)

```text
specs/001-inativacao-usuarios-waccess/
├── spec.md
├── plan.md                 # este arquivo
├── research.md             # Fase 0 (R-01 – R-17)
├── data-model.md           # Fase 1
├── quickstart.md           # Fase 1
├── contracts/              # Fase 1
│   ├── arquivos-entrada.md
│   ├── logs-auditoria-falhas.md
│   ├── configuracao.md
│   ├── waccess-inativacao.md
│   ├── replicacao.md
│   └── saude.md
├── checklists/requirements.md
└── tasks.md                # Fase 2 (/speckit-tasks — ainda não criado)
```

### Source Code (repository root)

```text
.gitignore                                    # novo: bin/, obj/, .vs/, appsettings.json (R-15)
src/
├── InativacaoUsuarioInter.Service.slnx       # + projetos de teste
├── InativacaoUsuarioInter.Service/
│   ├── Program.cs                            # composição: options, módulos, resiliência, OTel, WindowsService
│   ├── appsettings.example.json              # versionado, com placeholders
│   ├── appsettings.json                      # local, NÃO versionado
│   ├── Configuracao/      # options + validadores (Settings, SharePointFolder, ConnectionString, Processamento, Resiliencia, Telemetria)
│   ├── Ciclo/             # CicloWorker (BackgroundService), OrquestradorCiclo, PoliticaFluxo{Inter,Condominio}
│   ├── Entrada/           # DetectorElegiveis, LeitorArquivo, ParserJson, ParserTxt, NormalizadorCpf, ValidadorRegistro
│   ├── Inativacao/        # IRepositorioWAccess, RepositorioWAccessSql, ClassificadorErrosSql
│   ├── Registros/         # IRegistroAuditoria, IRegistroFalhas, FormatadorLinhaLog, EscritorLogAppendOnly
│   ├── Arquivos/          # IGerenciadorArquivos, EscritorAtomico, NomeadorPartes, SerializadorPartes, ClassificadorErrosIO
│   ├── Estado/            # IArmazenamentoEstado, ArmazenamentoEstadoSqlite, Migracoes
│   ├── Replicacao/        # IServicoReplicacao, ServicoReplicacao (somente Inter)
│   ├── Resiliencia/       # registro dos pipelines nomeados
│   ├── Saude/             # IPublicadorSaude, PublicadorSaudeArquivo
│   └── Telemetria/        # Instrumentacao (ActivitySource/Meter), atributos, mascaramento
├── InativacaoUsuarioInter.Setup/             # MSI existente (ver nota de implantação)
tests/
├── InativacaoUsuarioInter.Tests.Unit/        # parse, normalização, validação, classificação, nomes, formatação de log, config, agendamento
├── InativacaoUsuarioInter.Tests.Contract/    # um conjunto por arquivo de contracts/ (golden files + SQL Server em container)
└── InativacaoUsuarioInter.Tests.Integration/ # fluxos Inter/Condomínio, replicação, injeção de falhas, crash/retomada, telemetria, performance, opt-in LocalDB
```

**Structure Decision**: um único projeto executável, com módulos por namespace (Princípio I: "namespaces/projetos por capacidade"). Cada módulo expõe só interfaces públicas, e o resto é `internal`. Separar em vários `.csproj` agora não se justifica para ≈15 tipos por módulo. Os testes ficam em três projetos, para o CI rodar e bloquear cada categoria separadamente (gates 2 a 4). O `Worker.cs` atual vira `Ciclo/CicloWorker.cs`.

**Nota de implantação**: o `InativacaoUsuarioInter.Setup.vdproj` aponta para `obj/Debug/net10.0/apphost.exe` e não registra o serviço. A instalação documentada usa `dotnet publish` + `sc.exe` ([quickstart.md §5](./quickstart.md)). Ajustar o MSI (saída de publish Release + custom action de registro) fica como tarefa opcional, fora do caminho crítico.

## Fluxo do ciclo (referência para tasks)

1. Gravar `saude.json` (início do ciclo) e limpar `.tmp` órfãos das próprias saídas.
2. **Recuperação**: registros `EmExecucao` ou com `log_gravado = 0` (R-08).
3. Listar os elegíveis em `Imput` (R-09). Arquivos com hash desconhecido são **novos**; partes `__pendente-` são **retentativas**.
4. Para cada arquivo, em ordem de `LastWriteTime`: ler → **resolver `HashRaiz` no diário** → validar e calcular as chaves → registrar no diário → para cada registro válido e não terminal: pré-leitura WAccess → intenção + `estado_anterior` (transação) → UPDATE se `Ativo` (pipeline `waccess`, retentativas limitadas ao orçamento do ciclo) → resultado + linha de log (transação) → anexar ao log → marcar. Uma falha transitória que esgota as tentativas marca o WAccess como indisponível pelo resto do ciclo.
5. Segregar ou mover o arquivo (R-10).
6. **Somente Inter**: replicar as entregas pendentes (R-11).
7. Avaliar alertas (pendência acima de 24 h), gravar `saude.json` (fim do ciclo) e esperar `IntervalSeconds`.

## Ordem de implementação sugerida (para `/speckit-tasks`)

0. **R-15 e R-16** (higiene do repositório e chave `WAccess`): bloqueante.
1. Configuração + agendamento (US2): base para todos os testes.
2. Entrada, Inativação, Registros, Estado, Arquivos → US1 (MVP Inter).
3. Resiliência + recuperação → US3.
4. Replicação → US4.
5. Fluxo Condomínio → US5.
6. Telemetria e saúde transversais, com o teste de cobertura ligado desde a etapa 2.

## Riscos e pendências

| Item | Impacto | Tratamento |
| --- | --- | --- |
| R-02: CPF duplicado em `CHMain` (spec corrigida em 2026-11-11 para refletir o esquema real) | Inativação de mais de um cadastro | Padrão: regra literal (inativa todos) + aviso; **confirmar com o negócio** |
| Pré-leitura + UPDATE em duas idas ao banco (R-03) | Corrida de milissegundos com outro sistema que inative o mesmo usuário | Aceita: o estado final fica correto; pior caso é auditar `INATIVADO` em vez de `JA_INATIVO` |
| 1 `IdNumber` pontuado no banco de testes | Esse usuário não é encontrado | Regra literal mantida; aparece como `CPF_NAO_ENCONTRADO` |
| Codificação dos TXT gerados no Windows (ANSI/1252) | Arquivo rejeitado como `ARQUIVO_INVALIDO` | Contrato UTF-8 (spec, Assumptions); confirmar com o sistema gerador |
| Senha de testes já presente no histórico do git | Exposição da credencial de teste | R-15 + recomendação de trocar a senha |
| Definição do trigger `wtr_CHMainChangeCHState` ilegível | Efeitos colaterais desconhecidos | Fora do controle do serviço; contagem robusta via `OUTPUT INTO` |

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
| --- | --- | --- |
| VI: predicado `IdNumber = @Cpf` sem índice com `IdNumber` como coluna líder (existe só `(CHType, IdNumber)`) | O esquema do WAccess é de terceiros e mudá-lo está fora do escopo (spec, Assumptions) | Criar um índice em `IdNumber` exige mudar o esquema do WAccess. Medido: ≈26 mil linhas; com o parâmetro `VarChar(50)` o plano varre o índice estreito em poucos ms, dentro de p95 ≤ 500 ms. O teste de performance (SC-009) confirma o orçamento |
| Desvios de implementação: Polly.Core direto, spans de banco manuais, proxy TCP em vez do Toxiproxy, processamento sequencial sem chave de paralelismo | Ver [ADR 0001](../../docs/adr/0001-desvios-de-implementacao.md) (convergência T103) | As alternativas do plano acoplavam o relógio de domínio ao do Polly, traziam dependência pré-release que pode gravar parâmetros SQL, exigiam outro container para injetar falhas e traziam concorrência sem ganho medido |
| Configurações técnicas novas (`Processamento:*`, `Resiliencia:*`, `Telemetria:*`) além das chaves do requisito | A constituição exige que timeout, retry, breaker e limites sejam configuráveis | Fixar valores no código viola a constituição ("Configuração") |
