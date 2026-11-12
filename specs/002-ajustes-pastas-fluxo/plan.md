# Implementation Plan: Ajustes de Pastas, Fluxo de Arquivos e Atualização no WAccess

**Branch**: `main` (nenhum hook de branch; diretório `specs/002-ajustes-pastas-fluxo`) | **Date**: 2026-11-12 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `specs/002-ajustes-pastas-fluxo/spec.md`

## Summary

Ajustes no Worker Service existente (feature 001):

1. **Pastas**: nomes padronizados `Input`, `Output`, `Processed`, `Logs`, `Fail`, centralizados em constantes e criados a cada ciclo; migração automática e idempotente das pastas antigas (`Imput`, `Auditoria`, `Processados`, `Falhas`), inclusive mescla atômica dos logs (research R-06, R-08).
2. **Fluxo Inter**: `Processed` vira o histórico de concluídos e `Output` vira fila de saída. Cada conteúdo concluído é gravado nas duas pastas; a entrega em `{FolderPathCondominio}/Input` ocorre no mesmo ciclo e, após confirmada no diário, o arquivo sai de `Output`. Arquivos herdados de `Output` vão para `Processed` sem reentrega (R-03, R-04).
3. **Fluxo Condomínio**: `Input` → `Processed`; falhas em `{FolderPathCondominio}/Fail`.
4. **Arquivo de falhas** com o nome do original; colisão → nome único (R-05).
5. **WAccess**: UPDATE grava `CHState = 1, CHDownloadRequired = 1`; o passo B passa a rodar também para CPFs já inativos, para marcar download; a classificação `INATIVADO`/`JA_INATIVO` não muda (R-01, R-02).
6. **Saúde**: alerta `ENTREGA_PENDENTE_ACIMA_LIMITE` (R-07); diário SQLite v3 (`entrega.registrada_em`).

## Technical Context

**Language/Version**: C# / .NET 10 (`Microsoft.NET.Sdk.Worker`), `Nullable` habilitado

**Primary Dependencies**: inalteradas — `Microsoft.Data.SqlClient`, `Microsoft.Data.Sqlite`, `Microsoft.Extensions.Resilience` (Polly v8), OpenTelemetry. Nenhum pacote novo.

**Storage**: SQL Server WAccess (`dbo.CHMain`, colunas `CHState smallint NOT NULL`, `CHDownloadRequired bit NULL` — R-01); diário SQLite local (migração v3); sistema de arquivos/compartilhamentos

**Testing**: xUnit (Microsoft.Testing.Platform) — suítes Unit, Contract, Integration (Testcontainers SQL Server) e opt-in `LocalDb`

**Target Platform**: Windows Service (produção); Linux/Windows para CI

**Project Type**: Worker Service único (monólito modular)

**Performance Goals**: orçamento da spec 001 inalterado; entrega ao Condomínio dentro do orçamento do ciclo (`IntervalSeconds`); mescla de logs em streaming

**Constraints**: nada sobrescrito; toda mutação idempotente e retomável; sem CPF/nome em telemetria; `.tmp` de terceiros intocados

**Scale/Scope**: ~10 arquivos de produção alterados, 2 novos (`NomesPastas`, `MigradorLegado`); ~20 arquivos de teste com literais de pastas a atualizar; docs (`README.md`, `docs/operacao.md`)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Princípio | Avaliação | Status |
| --- | --- | --- |
| I. Monólito modular | Mudanças dentro do mesmo processo; `MigradorLegado` no módulo `Arquivos`; entrega continua no módulo `Replicacao`; nenhum serviço, fila ou endpoint novo. O Condomínio deixa de depender de `FolderPathInter` (Clarification 2). | ✅ |
| II. Falhas de rede/E/S | Migração, cópia para `Output`/`Processed`, limpeza de `Output` e passo B passam pelos pipelines existentes (`pasta-local`, `pasta-replicacao`, `waccess`) com timeout, retry com jitter e circuit breaker; erros classificados; entrega pendente preservada em `Output`. | ✅ |
| III. Idempotência | Chaves por hash de conteúdo inalteradas; cópias tmp+rename com no-op por hash; limpeza de `Output` só após `Entregue` durável; mescla de log detecta prefixo já mesclado; UPDATE condicional com predicado que zera na reexecução; testes de dupla execução e crash + retomada planejados para cada nova mutação. | ✅ |
| IV. Telemetria | Spans `arquivo.migrar_legado`, `replicacao.limpar_output`; atributo `waccess.linhas_marcadas_download`; logs estruturados `MigracaoLegado`; sem CPF/nome. | ✅ |
| V. Contrato e injeção de falhas | Contratos novos/alterados: [estrutura-pastas](./contracts/estrutura-pastas.md), [replicacao](./contracts/replicacao.md), [waccess-inativacao](./contracts/waccess-inativacao.md), cada um com teste de contrato. Injeção de falhas: destino indisponível, crash entre `Entregue` e remoção de `Output`, crash em cada passo da migração e da mescla, pasta antiga sem permissão. Integração com SQL Server real (Testcontainers) para `CHDownloadRequired`. | ✅ |
| VI. Performance | Sem mudança no caminho crítico além de 1 UPDATE para CPFs já inativos (raro); cópia extra local em `Output` (mesmo volume máximo de arquivo, 1 MiB); mescla de log em streaming; `output_conhecido` mantém o custo de reconciliação independente do histórico. Teste de carga da 001 reexecutado. | ✅ |

**Pós-design (Phase 1)**: reavaliado após research/data-model/contratos — sem violações; nenhuma entrada em Complexity Tracking.

## Project Structure

### Documentation (this feature)

```text
specs/002-ajustes-pastas-fluxo/
├── plan.md              # Este arquivo
├── research.md          # Phase 0
├── data-model.md        # Phase 1
├── quickstart.md        # Phase 1
├── contracts/
│   ├── estrutura-pastas.md
│   ├── replicacao.md          # substitui o contrato da 001
│   └── waccess-inativacao.md  # altera o passo B do contrato da 001
│   # (saúde: o contrato da 001, contracts/saude.md, ganha o código ENTREGA_PENDENTE_ACIMA_LIMITE — FR-007a)
├── checklists/requirements.md
└── tasks.md             # Phase 2 (/speckit-tasks)
```

### Source Code (repository root)

```text
src/InativacaoUsuarioInter.Service/
├── Arquivos/
│   ├── NomesPastas.cs              # NOVO: Input/Output/Processed/Logs/Fail + NomesPastasLegado
│   ├── MigradorLegado.cs           # NOVO: migração das pastas antigas e mescla de logs (R-06)
│   ├── GerenciadorArquivos.cs      # cópia em Output + Processed (Inter); falhas com nome original (R-03, R-05)
│   └── NomeadorPartes.cs           # nome do arquivo de falhas
├── Ciclo/
│   ├── PoliticasFluxo.cs           # PastaSaida (Inter), PastaLogs, PastaFail, concluídos = Processed
│   └── OrquestradorCiclo.cs        # ordem: estrutura → migração → temporários → recuperação → entrada → entrega
├── Replicacao/ServicoReplicacao.cs # destino Condominio/Input; limpeza de Output após Entregue; herdados (R-04)
├── Inativacao/RepositorioWAccessSql.cs + ProcessadorArquivo (passo B também para Inativo) (R-02)
├── Estado/                         # migração v3; ObterEntregaAsync; ObterEntregaPendenteMaisAntigaAsync
├── Registros/IRegistros.cs         # Logs/ e Fail/
├── Saude/                          # SondaDependencias (Condominio/Input), VerificadorPermissoes (Logs/Fail), alerta R-07
└── Telemetria/Instrumentacao.cs    # atributo waccess.linhas_marcadas_download

tests/
├── InativacaoUsuarioInter.Tests.Contract/{EstruturaDiretorios,Replicacao,WAccess,Logs}/
├── InativacaoUsuarioInter.Tests.Integration/{Migracao (novo),FluxoInter,FluxoCondominio,Replicacao,InjecaoFalhas,Idempotencia,LocalDb,Saude}/
└── InativacaoUsuarioInter.Tests.Unit/{Arquivos,Ciclo}/

docs/operacao.md, README.md          # nomes de pastas, fluxo novo, CHDownloadRequired, implantação
```

**Structure Decision**: mantém a estrutura da feature 001 (um projeto de serviço + quatro projetos de teste); só acrescenta dois arquivos no módulo `Arquivos` e a pasta de testes `Integration/Migracao`.

### Ordem do ciclo (atualizada)

1. `GarantirEstrutura` (pastas novas **da própria raiz**; falha aqui interrompe o ciclo) → 2. `MigradorLegado` → 3. limpeza de `.tmp` próprios → 4. recuperação de logs → 5. arquivos elegíveis de `Input` (inativação; conclusão grava `Processed` + `Output` no Inter) → 6. Inter: reconciliação de `Output` (limpa entregues/herdados, registra pendentes) e entrega a `{FolderPathCondominio}/Input` (a pasta remota é criada aqui, se ausente; falha é transitória **da entrega** e não afeta os passos 1–5 — FR-002a) → 7. sonda, alertas e saúde.

### Riscos e mitigação

| Risco | Mitigação |
| --- | --- |
| Condomínio atualizado antes do Inter | Migração roda a cada ciclo e absorve as entregas que o Inter antigo ainda fizer em `Imput`; `.tmp` em trânsito não são tocados |
| Inter atualizado antes do Condomínio | O Inter novo entrega em `Condominio/Input`, que o Condomínio antigo não lê: entregas ficam paradas até a atualização. **Ordem obrigatória de implantação: Condomínio primeiro (ou na mesma janela), depois Inter** — registrada no quickstart e em `docs/operacao.md` |
| Compartilhamento do Condomínio fora do ar | Criação de `{FolderPathCondominio}/Input` fica no caminho de entrega (FR-002a); o Inter continua processando e acumula em `Output` |
| Histórico do `Output` antigo entregue de novo | Reconciliação por hash: `Entregue` → move para `Processed` sem copiar (R-04) |
| Logs duplicados na mescla | Detecção de prefixo + troca atômica (R-06); teste de crash em cada passo |
| Docs da 001 com nomes antigos | Contratos da 002 declaram o que substituem; README e `docs/operacao.md` atualizados na mesma entrega |

## Complexity Tracking

Sem violações da constituição.
