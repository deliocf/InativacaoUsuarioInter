<!--
SYNC IMPACT REPORT
==================
Version change: (template não preenchido) → 1.0.0
Bump rationale: MAJOR inicial — primeira ratificação; todos os placeholders substituídos.

Modified principles (template → novo título):
- [PRINCIPLE_1_NAME] → I. Não Distribuir por Padrão (Monólito Modular Primeiro)
- [PRINCIPLE_2_NAME] → II. Tratamento Estrito de Falhas de Rede e E/S
- [PRINCIPLE_3_NAME] → III. Idempotência Obrigatória (NÃO NEGOCIÁVEL)
- [PRINCIPLE_4_NAME] → IV. Telemetria Nativa com OpenTelemetry (NÃO NEGOCIÁVEL)
- [PRINCIPLE_5_NAME] → V. Testes de Contrato e Injeção de Falhas

Added sections:
- VI. Performance Mensurável (sexto princípio, além dos 5 do template)
- Restrições Técnicas e Padrões Operacionais (SECTION_2)
- Fluxo de Desenvolvimento e Quality Gates (SECTION_3)
- Governance (preenchida)

Removed sections: nenhuma.

Dependent templates (lidos em tempo de execução, não alterados por este comando):
- .specify/templates/plan-template.md — "Constitution Check" deve verificar os gates I–VI
- .specify/templates/spec-template.md — specs devem declarar orçamento de performance (VI)
- .specify/templates/tasks-template.md — tarefas de teste de contrato/falha (V) e telemetria (IV)

Follow-up TODOs: nenhum placeholder adiado. Os valores numéricos padrão do Princípio VI
são defaults revisáveis por spec, não metas de negócio aprovadas.
-->

# InativacaoUsuarioInter Constitution

## Core Principles

### I. Não Distribuir por Padrão (Monólito Modular Primeiro)

- O sistema MUST ser entregue como **um único processo implantável** (um Worker Service),
  organizado em módulos internos com fronteiras explícitas (namespaces/projetos por
  capacidade: leitura de arquivos, inativação, auditoria, replicação).
- Módulos MUST se comunicar por interfaces em processo; é PROIBIDO introduzir chamada de
  rede, fila, barramento ou banco compartilhado **entre módulos do mesmo serviço**.
- Diferenças de comportamento entre instalações (Inter/Condomínio) MUST ser resolvidas por
  configuração (`Settings:Instancia`), nunca por bases de código ou serviços separados.
- Um novo processo, serviço, fila ou broker só pode ser criado mediante ADR aprovado que
  demonstre, com dados medidos, ao menos uma necessidade que o monólito não atende:
  escala independente, isolamento de falha exigido, fronteira organizacional/de segurança
  ou requisito regulatório. Sem ADR, o PR MUST ser rejeitado.
- Um módulo MUST NOT acessar tabelas, arquivos ou estado interno de outro módulo
  diretamente; somente via sua interface pública.

**Verificação**: revisão de PR checa novos `PackageReference` de mensageria/RPC, novos
`.csproj` executáveis e novos endpoints de rede; qualquer um exige ADR referenciado.

**Rationale**: cada salto de rede adiciona modos de falha parciais. A forma mais barata de
tolerar falhas distribuídas é não criá-las; fronteiras modulares preservam a opção de
extrair depois, com evidência.

### II. Tratamento Estrito de Falhas de Rede e E/S

Toda operação que cruza fronteira de processo ou dispositivo — banco WAccess (SQL),
diretórios compartilhados/SMB/FTP, HTTP, DNS — é considerada **não confiável** e MUST:

- **Timeout explícito**: toda chamada declara timeout configurável (connect e operação);
  é PROIBIDO depender de default implícito ou de timeout infinito. Todo método assíncrono
  de E/S MUST aceitar e propagar `CancellationToken`.
- **Retry com backoff exponencial e jitter**: retries somente para erros classificados
  como **transitórios** (timeout, deadlock, perda de conexão, compartilhamento indisponível,
  arquivo bloqueado). Número máximo de tentativas e teto de backoff MUST ser finitos e
  configuráveis. Retry sem jitter é PROIBIDO. Retry só é permitido sobre operações
  idempotentes (ver Princípio III).
- **Circuit breaker** por dependência externa (um por banco/destino), com limiar de falha,
  janela de amostragem e tempo de meia-abertura configuráveis. Com o circuito aberto, o
  trabalho MUST ser preservado como pendente para ciclo posterior — nunca descartado.
- **Classificação obrigatória de erros**: toda exceção de E/S MUST ser mapeada para
  `Transitória` (reprocessar) ou `Definitiva` (registrar em Falhas, não repetir). Captura
  genérica que engole exceção sem classificar e registrar é PROIBIDA.
- **Orçamento de tempo por ciclo**: a soma de tentativas MUST caber no intervalo do ciclo;
  ciclos MUST NOT se sobrepor.
- As políticas MUST ser implementadas por biblioteca padrão de resiliência
  (ex.: Polly / `Microsoft.Extensions.Resilience`), centralizadas e configuradas via
  `appsettings.json` — não por laços `try/catch` ad hoc.

**Verificação**: teste de injeção de falha (Princípio V) para cada dependência comprova
timeout, retry com jitter, abertura do circuito e preservação de pendentes.

**Rationale**: a rede falha de forma parcial, lenta e silenciosa. Sem limites explícitos,
uma dependência lenta trava o worker; retries sem jitter e sem breaker amplificam a falha.

### III. Idempotência Obrigatória (NÃO NEGOCIÁVEL)

- **Toda mutação** (UPDATE no WAccess, escrita de auditoria/falha, movimentação ou cópia
  de arquivo, replicação Inter → Condomínio) MUST produzir o mesmo estado final se
  executada 1 ou N vezes com a mesma entrada.
- Cada unidade de trabalho MUST ter uma **chave de idempotência determinística** derivada
  do conteúdo (ex.: hash do arquivo + instância + índice/CPF do registro), nunca de
  horário ou GUID aleatório gerado na execução.
- Mutações no banco MUST ser escritas como transições condicionais
  (ex.: `UPDATE ... WHERE CPF = @cpf AND <estado ainda ativo>`) e tratar "0 linhas
  afetadas por já estar inativo" como sucesso idempotente, não como erro.
- Operações de arquivo MUST usar escrita em arquivo temporário + rename atômico; mover um
  arquivo já presente no destino com o mesmo hash MUST ser no-op.
- Todo worker/consumidor de fila ou diretório MUST assumir entrega **at-least-once**:
  reprocessar um arquivo ou registro já concluído MUST NOT duplicar auditoria,
  reinativar, reenviar replicação ou sobrescrever resultado.
- O progresso por registro MUST ser persistido de forma durável antes de confirmar a
  conclusão do arquivo, permitindo retomada após crash no meio do processamento sem
  refazer efeitos já aplicados.

**Verificação**: para cada mutação existe teste automatizado que executa a operação
duas vezes (e uma vez interrompida no meio + retomada) e verifica estado final idêntico e
ausência de efeitos duplicados.

**Rationale**: retries (Princípio II), reinícios do serviço e replicação entre ambientes
garantem que a mesma entrada será vista mais de uma vez. Sem idempotência, tolerância a
falhas vira corrupção de dados.

### IV. Telemetria Nativa com OpenTelemetry (NÃO NEGOCIÁVEL)

- **Nenhuma execução sem rastreamento**: todo ciclo do worker, arquivo e registro
  processado MUST executar dentro de um `Activity`/span OpenTelemetry. Código que inicia
  trabalho (ciclo, handler, job, consumidor) sem abrir span raiz é considerado defeito e
  MUST ser rejeitado em revisão e bloqueado por teste.
- Spans MUST cobrir cada chamada externa (SQL, sistema de arquivos remoto, HTTP) com
  atributos semânticos padrão OTel e atributos de domínio: `instancia`, `arquivo`,
  `idempotency_key`, `resultado` (`sucesso|transitoria|definitiva`), `tentativa`.
- O contexto de trace MUST ser propagado através de fronteiras assíncronas e na
  replicação Inter → Condomínio (ex.: `traceparent` em metadado/sidecar do arquivo).
- Logs MUST ser estruturados (`ILogger` com templates), correlacionados com
  `TraceId`/`SpanId`, e exportados via OpenTelemetry. `Console.WriteLine` e concatenação
  de strings em logs são PROIBIDOS.
- Métricas mínimas obrigatórias: registros processados por resultado, duração por
  registro/arquivo/ciclo (histograma), tentativas de retry, estado do circuit breaker,
  tamanho do backlog pendente, idade do arquivo mais antigo não processado.
- Dados pessoais (CPF, nomes) MUST NOT aparecer em claro em spans, métricas ou logs
  exportados; usar mascaramento ou hash.
- Falha do coletor/exportador MUST NOT interromper o processamento de negócio
  (exportação assíncrona, com buffer limitado e descarte contabilizado). A
  **instrumentação** é obrigatória; a **disponibilidade do backend** de telemetria não é
  pré-requisito de execução.

**Verificação**: teste com exportador em memória falha se qualquer operação de domínio
executar sem span ativo ou sem os atributos obrigatórios.

**Rationale**: em falhas parciais, o que não é rastreado não pode ser diagnosticado nem
provado. Telemetria acoplada desde o início é a única forma de auditar retries,
duplicidades e latência.

### V. Testes de Contrato e Injeção de Falhas

- Todo contrato de fronteira MUST ter teste de contrato automatizado: formatos de arquivo
  de entrada (JSON/TXT), estrutura de diretórios, layout de logs de Auditoria/Falhas,
  esquema/consulta usados no WAccess e o artefato de replicação Inter → Condomínio.
  Mudança de contrato sem atualização do teste correspondente MUST falhar no CI.
- Testes de integração MUST usar dependências reais ou equivalentes fiéis
  (ex.: SQL Server em container via Testcontainers, sistema de arquivos temporário real);
  mocks de banco são PROIBIDOS para validar comportamento de persistência.
- **Chaos engineering básico** é obrigatório para cada dependência externa. A suíte MUST
  injetar, no mínimo: timeout/latência alta, erro transitório intermitente, indisponibilidade
  total (circuito abre), arquivo bloqueado/parcialmente escrito, diretório sem permissão,
  e crash do processo no meio de um arquivo (retomada). Cada cenário MUST afirmar:
  nenhuma perda de registro, nenhum efeito duplicado, classificação correta do erro e
  telemetria emitida.
- Testes MUST ser escritos antes ou junto da implementação e MUST falhar antes da
  correção (red → green). Testes flaky MUST ser corrigidos ou postos em quarentena com
  issue aberta em até 1 dia útil; nunca ignorados silenciosamente.

**Verificação**: CI executa contratos + integração + injeção de falhas em todo PR; merge
bloqueado se qualquer cenário obrigatório estiver ausente ou falhando.

**Rationale**: os Princípios II e III só são verdadeiros se forem demonstrados sob falha.
Um caminho de erro nunca exercitado é um caminho de erro quebrado.

### VI. Performance Mensurável

- Toda spec MUST declarar orçamento de performance mensurável: throughput alvo
  (registros/min), latência p95 por registro, duração máxima de ciclo e volume máximo
  esperado por arquivo. Na ausência de valor na spec, aplicam-se os defaults da seção
  "Restrições Técnicas".
- Acesso a banco MUST ser parametrizado, indexado no predicado usado (ex.: CPF) e sem
  N+1; operações em lote MUST ter tamanho de lote configurável e limitado.
- Leitura de arquivos MUST ser em streaming ou com limite de tamanho explícito;
  carregar entrada ilimitada em memória é PROIBIDO.
- Todo recurso compartilhado MUST ser limitado: paralelismo máximo, tamanho de buffer,
  conexões do pool e fila interna têm teto configurável (back-pressure em vez de
  crescimento ilimitado).
- Código síncrono bloqueante em caminho de E/S (`.Result`, `.Wait()`, `Thread.Sleep`) é
  PROIBIDO.
- Regressão de performance acima de 20% em benchmark/teste de carga do caminho crítico
  MUST bloquear o merge até ser justificada em ADR ou corrigida.
- Otimização sem medição prévia é desencorajada; toda alegação de ganho MUST vir com
  números antes/depois (métricas do Princípio IV ou benchmark).

**Verificação**: métricas de duração (Princípio IV) comparadas ao orçamento; teste de carga
com volume máximo declarado na spec executado antes de release.

**Rationale**: em sistemas resilientes, lentidão é uma forma de falha: estoura timeouts,
dispara retries e abre circuitos. Orçamentos explícitos tornam a performance verificável.

## Restrições Técnicas e Padrões Operacionais

- **Plataforma**: .NET 10 Worker Service (`Microsoft.NET.Sdk.Worker`), C# com `Nullable`
  habilitado; warnings de nulabilidade tratados como erro em código novo.
- **Configuração**: todo timeout, política de retry, parâmetro de circuit breaker,
  intervalo de ciclo, tamanho de lote e limite de paralelismo MUST vir de
  `appsettings.json`/variáveis de ambiente, validado na inicialização
  (`ValidateOnStart`). Configuração inválida MUST impedir a partida (fail-fast).
  Segredos (connection strings) MUST NOT ser versionados.
- **Defaults revisáveis** (aplicados quando a spec não define):
  - Timeout de comando SQL: 30 s; timeout de conexão: 15 s.
  - Timeout de operação de arquivo remoto: 30 s.
  - Retries: máx. 5 tentativas, backoff exponencial base 1 s, teto 60 s, jitter completo.
  - Circuit breaker: abre com ≥ 50% de falhas em janela de 30 s e mínimo de 10 chamadas;
    meia-abertura após 60 s.
  - Latência p95 por registro (excluindo retries): ≤ 500 ms; memória do processo ≤ 512 MB.
- **Shutdown gracioso**: ao receber cancelamento, o worker MUST concluir ou abandonar de
  forma segura o registro corrente (sem estado parcial não recuperável) dentro do
  `ShutdownTimeout` configurado.
- **Health**: o serviço MUST expor sinal de saúde (liveness do ciclo e readiness das
  dependências) consumível pela operação.
- **Dados pessoais**: CPF e nomes são dados pessoais (LGPD); acesso mínimo necessário e
  proibição de exposição em telemetria (ver Princípio IV).

## Fluxo de Desenvolvimento e Quality Gates

- Todo `plan.md` MUST conter a seção "Constitution Check" avaliando explicitamente os
  Princípios I–VI; qualquer violação MUST constar em "Complexity Tracking" com
  justificativa e alternativa mais simples rejeitada.
- Gates obrigatórios de PR (bloqueiam merge):
  1. Build sem warnings novos; análise estática habilitada.
  2. Testes unitários, de contrato, de integração e de injeção de falhas verdes.
  3. Teste de idempotência (execução dupla + retomada) para cada mutação nova ou alterada.
  4. Teste de cobertura de telemetria (nenhuma operação de domínio sem span).
  5. Nenhuma chamada externa sem timeout, política de resiliência e classificação de erro.
  6. Nenhum novo processo/serviço/fila sem ADR aprovado (Princípio I).
  7. Orçamento de performance respeitado ou regressão justificada em ADR.
- Code review MUST citar o princípio violado ao solicitar mudança; aprovações MUST
  confirmar os gates acima.
- ADRs ficam versionados no repositório e são referenciados no PR que os implementa.

## Governance

- Esta constituição prevalece sobre quaisquer outras práticas, guias ou convenções do
  projeto. Em conflito, a constituição vence até ser formalmente emendada.
- **Emendas**: propostas via PR alterando `.specify/memory/constitution.md`, contendo
  motivação, impacto nos artefatos dependentes (templates de spec/plan/tasks) e plano de
  migração para código existente não conforme. Requerem aprovação de ao menos um
  responsável técnico do projeto.
- **Versionamento semântico** da constituição:
  - MAJOR: remoção ou redefinição incompatível de princípio ou gate.
  - MINOR: novo princípio/seção ou expansão material de regra.
  - PATCH: esclarecimentos, redação e correções sem mudança semântica.
- **Exceções**: só são válidas se registradas em ADR com escopo, prazo de expiração e
  responsável. Exceções expiradas tornam o código não conforme.
- **Revisão de conformidade**: todo PR verifica os gates; a cada release, revisar métricas
  de telemetria contra os orçamentos do Princípio VI e o resultado da suíte de injeção de
  falhas.
- Regras marcadas com MUST/PROIBIDO são não negociáveis; SHOULD/desencorajado exigem
  justificativa escrita quando não seguidas.

**Version**: 1.0.0 | **Ratified**: 2026-11-11 | **Last Amended**: 2026-11-11
