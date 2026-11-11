# ADR 0001 — Desvios de implementação em relação ao plano

- **Status**: Aceito
- **Data**: 2026-11-11
- **Contexto da feature**: [specs/001-inativacao-usuarios-waccess](../../specs/001-inativacao-usuarios-waccess/)
- **Princípios envolvidos**: II (resiliência), IV (telemetria), V (testes de falha), VI (performance e recursos limitados)

## Contexto

O [plan.md](../../specs/001-inativacao-usuarios-waccess/plan.md) e o [research.md](../../specs/001-inativacao-usuarios-waccess/research.md) (R-06, R-14, R-17) indicavam pacotes e ferramentas específicos. Durante a implementação, quatro escolhas mudaram. Este ADR registra cada mudança, o motivo e como ela continua atendendo à constituição. A convergência (T103) identificou essas mudanças como desvio do plano.

## Decisões

### D1. Polly.Core direto, em vez de `Microsoft.Extensions.Resilience`

- **Decisão**: os pipelines (`Resiliencia/RegistroPipelines.cs`) são montados com `ResiliencePipelineBuilder` do **Polly.Core 8**, sem a integração de DI de `Microsoft.Extensions.Resilience`.
- **Motivo**: a integração de DI usa o `TimeProvider` registrado no container para as esperas e para a pausa do circuito. Os testes registram um `FakeTimeProvider` para o domínio (estabilização, alerta de 24 h, agendamento do ciclo). Se ele também controlasse as esperas do Polly, uma retentativa ficaria parada até alguém avançar o relógio. Montando os pipelines diretamente, as esperas e o circuito usam o relógio do sistema, e o `TimeProvider` de domínio continua independente.
- **Conformidade (Princípio II)**: é a mesma biblioteca padrão (Polly v8), centralizada e configurada por `appsettings.json`. Timeout, retry exponencial com jitter, circuit breaker por dependência e orçamento por ciclo continuam presentes.

### D2. Spans de banco manuais, em vez de `OpenTelemetry.Instrumentation.SqlClient`

- **Decisão**: `RepositorioWAccessSql` e `SondaDependencias` abrem seus próprios spans (`waccess.ler_estado`, `waccess.inativar`, `saude.sonda`), com os atributos semânticos `db.system.name`, `db.operation.name` e `db.collection.name`, além dos atributos de domínio.
- **Motivo**: o pacote de instrumentação ainda é distribuído como pré-release e, conforme a configuração, pode gravar o texto e os parâmetros dos comandos. Os spans manuais garantem que **nenhum CPF** chegue à telemetria (Princípio IV, FR-038) e evitam uma dependência pré-release com avisos tratados como erro.
- **Conformidade (Princípio IV)**: toda chamada externa ao WAccess tem span. O teste `CoberturaTelemetriaTests` falha se faltar span ou atributo, ou se aparecer dado pessoal.

### D3. Proxy TCP em processo, em vez do Toxiproxy (T026)

- **Decisão**: a injeção de falhas de rede usa `tests/InativacaoUsuarioInter.Tests.Comum/ProxyTcpFalhas.cs`, que corta conexões, recusa novas e injeta latência por bloco, entre o serviço e o SQL Server em container.
- **Motivo**: o Toxiproxy exige configurar proxies e toxics pela API HTTP e pôr os containers na mesma rede. O proxy em processo exercita a **mesma** falha de rede real (TCP), com controle determinístico e sem outro container.
- **Conformidade (Princípio V)**: a dependência continua real (SQL Server 2022 em container) e as falhas são de rede de verdade, não mocks. A suíte cobre latência acima do timeout, indisponibilidade total com abertura do circuito, corte no meio do arquivo e recuperação.

### D4. Processamento sequencial, sem chave de paralelismo

- **Decisão**: arquivos e registros são processados **sequencialmente** (paralelismo fixo = 1). Não foi criada a chave `Processamento:Paralelismo`.
- **Motivo**: o volume típico é de 3 registros por arquivo e o máximo é de 1.000. O teste de carga mede p95 de 96 ms por registro e 1.071 registros/min, bem dentro do orçamento (≤ 500 ms; ≥ 300/min; ≤ 5 min por arquivo). Paralelizar traria concorrência no diário SQLite, nos logs append-only e na ordem de auditoria, aumentando o risco de violar o Princípio III, sem ganho necessário.
- **Conformidade (Princípio VI)**: o recurso é limitado (um registro por vez). O pool de conexões do SqlClient tem teto configurável pela própria string de conexão (`Max Pool Size`, padrão 100) e o exportador OTel usa fila limitada. Se o volume crescer, a chave `Processamento:Paralelismo` deverá ser introduzida, medida contra a linha de base (`Performance/linha-base.json`) e registrada em novo ADR.

## Consequências

- O `plan.md` (Complexity Tracking) referencia este ADR.
- Reverter D1 ou D2 exige revisar os testes de agendamento e de cobertura de telemetria. Reverter D3 exige adaptar `FalhasBancoTests` e os testes de saúde que usam o proxy.
- Os quatro desvios são cobertos pelos testes de CI (`.github/workflows/ci.yml`).
