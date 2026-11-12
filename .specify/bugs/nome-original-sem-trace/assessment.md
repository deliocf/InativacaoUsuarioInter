# Bug Assessment: Parte de sucesso renomeada e `.traceparent` copiado para o Condomínio

- **Slug**: nome-original-sem-trace
- **Created**: 2026-11-12
- **Source**: pasted text
- **Verdict**: valid
- **Severity**: medium

## Report (verbatim or summarized)

> FIX: Nunca renomear o arquivo original. Ao copiá-lo para Success, sempre manter o nome original. Preservar o nome
> original ao copiar para FolderPathCondominio/Input também.
> Não copiar arquivos de trace para o Condominio

Não existe pasta `Success` no serviço. "Success" foi lido como **a parte de sucesso** gravada em `Processed` (Inter e
Condomínio) e enfileirada em `Output` para entrega — hoje com o nome `<base>__sucesso-<h8>.<ext>`.

## Symptom

1. Quando um arquivo tem registros com falha ou pendentes (segregação), a parte com os registros de sucesso é gravada em
   `Processed` e entregue em `{FolderPathCondominio}/Input` com nome **diferente do original**
   (`20261112111333.txt` → `20261112111333__sucesso-95990873.txt`). Esperado: `20261112111333.txt`.
2. Em colisão de nome com conteúdo diferente (em `Processed`, `Output`, `Fail` ou no `Input` do Condomínio), o serviço
   gera `<base>__<h8>.<ext>`. Esperado: nunca renomear.
3. Cada entrega grava `<nome>.traceparent` ao lado do arquivo em `{FolderPathCondominio}/Input`. Esperado: nenhum
   arquivo de trace no Condomínio.

O caso integral (arquivo 100 % sucesso) já preserva o nome; o arquivo de falhas também (FR-010), salvo colisão.

## Reproduction

1. Instância `Inter`. Depositar em `{FolderPathInter}/Input` um arquivo `X.txt` com um CPF existente e um inexistente.
2. Aguardar o ciclo.
3. Observar `{FolderPathInter}/Processed/X__sucesso-<h8>.txt`, `{FolderPathCondominio}/Input/X__sucesso-<h8>.txt` e
   `{FolderPathCondominio}/Input/X__sucesso-<h8>.txt.traceparent`. Confirmado pelo próprio diário local na avaliação
   anterior (`20261112111333__sucesso-95990873.txt`) e pelos testes de integração existentes
   (`ReplicacaoTests.CA09…`, `Falhas_e_rejeitados_nunca_sao_entregues`, que esperam o prefixo `__sucesso-`).

## Suspected Code Paths

- `src/InativacaoUsuarioInter.Service/Arquivos/GerenciadorArquivos.cs:216-219` — `SegregarAsync` nomeia a parte de sucesso com `NomeadorPartes.Parte(base, "sucesso", h, ext)`.
- `src/InativacaoUsuarioInter.Service/Arquivos/NomeadorPartes.cs:17-18` — formato `<base>__<tipo>-<h8>.<ext>`.
- `src/InativacaoUsuarioInter.Service/Arquivos/GerenciadorArquivos.cs:262-268, 301-308` — `GravarParteAsync`/`DestinoLivreAsync`: em colisão com conteúdo diferente, `NomeadorPartes.Colisao` → `<base>__<h8>.<ext>` (em `Processed`, `Fail` e `Output`).
- `src/InativacaoUsuarioInter.Service/Replicacao/ServicoReplicacao.cs:238-249` — `CopiarAsync`: colisão no `Input` do Condomínio também renomeia com `Colisao`.
- `src/InativacaoUsuarioInter.Service/Replicacao/ServicoReplicacao.cs:274-278` — `CopiarAsync` grava `final + ".traceparent"` no `Input` do Condomínio.
- `src/InativacaoUsuarioInter.Service/Replicacao/ServicoReplicacao.cs:176-193` — `LimparSaidaAsync` compara a cópia de `Output` com `Processed/<mesmo nome>` e, se diferente, move com nome de colisão.
- `src/InativacaoUsuarioInter.Service/Entrada/LeitorTraceparent.cs:20-30` — o Condomínio lê o contexto de trace do sidecar.
- `specs/002-ajustes-pastas-fluxo/spec.md` — FR-009 ("junto com seu `.traceparent`"), FR-010/SC-006 (nome único em colisão), Edge Cases; `specs/001-…/spec.md:136` (colisão → nome único).
- `.specify/memory/constitution.md:126-127` (Princípio IV) — "O contexto de trace MUST ser propagado … na replicação Inter → Condomínio (ex.: `traceparent` em metadado/sidecar do arquivo)".

## Root Cause Hypothesis

Não é defeito de implementação: o código cumpre a regra de nomes da spec (R-10: partes "únicas, rastreáveis ao original";
colisão → nome único; nunca sobrescrever) e a regra de propagação de trace por sidecar (FR-009, Princípio IV). O pedido
muda essas regras. Confiança: **alta**.

A dificuldade real é que manter o nome original faz **duas partes diferentes do mesmo arquivo de origem colidirem**, e a
regra "nunca sobrescrever" (Princípio III) continua valendo. Isso acontece sempre que um arquivo é segregado com
pendências: a 1ª passada grava a parte de sucesso A em `Processed/X.txt`; quando as pendências são reprocessadas, a parte
de sucesso B também se chamaria `X.txt`, com conteúdo diferente. O mesmo vale para `Output` (A ainda na fila quando B
chega), para o `Input` do Condomínio (A ainda não consumida) e para `Fail` (falhas da 1ª e da 2ª passada).

## Proposed Remediation

**Preferred**:

1. **Nome da parte de sucesso = nome do original** (`<baseRaiz><ext>`, como já é feito para `Fail` em FR-010), em
   `Processed` das duas instâncias e na entrega ao Condomínio. Partes `__pendente-` continuam com o nome atual: são
   arquivos de trabalho na própria entrada do Inter, não são copiadas nem entregues (ver Open Questions).
2. **Entrega ao Condomínio sem renomear**: se `{FolderPathCondominio}/Input/<nome>` existir com conteúdo diferente, a
   entrega **não** renomeia nem sobrescreve — fica pendente em `Output` e é tentada de novo no próximo ciclo, depois que o
   Condomínio consumir o arquivo anterior. A fila já é processada em ordem (`rowid`), então A é entregue antes de B. O
   alerta `ENTREGA_PENDENTE_ACIMA_LIMITE` (FR-007a) já cobre uma espera longa. O nome de destino passa a ser guardado na
   entrega (nome do original), independente do nome do arquivo em `Output`.
3. **Fila `Output`** (pasta interna): se A e B coexistirem, o segundo arquivo precisa de um nome único na fila
   (`DestinoLivreAsync` atual), mas é entregue com o nome original (item 2). Ver Open Questions se nem na fila pode haver
   sufixo.
4. **Colisão em `Processed`** (histórico): depende da decisão em Open Questions. Recomendação: **mesclar** — a parte B é
   acrescentada ao `Processed/X.txt` existente (mesmo formato; JSON vira um único array), gravando por `tmp` + rename. O
   histórico fica com um arquivo por original contendo todos os registros concluídos com sucesso, sem renomear e sem perder
   conteúdo. A limpeza de `Output` (`LimparSaidaAsync`) e a restauração da fila (`RestaurarNaFilaAsync`) deixam de
   depender de `Processed` ter exatamente os mesmos bytes: basta a parte estar registrada no diário.
5. **Sem `.traceparent` no Condomínio**: `CopiarAsync` para de gravar o sidecar. Para não violar o Princípio IV, o
   contexto passa a ir num **metadado do arquivo** — fluxo alternativo NTFS `<arquivo>:traceparent`, invisível na pasta e
   previsto na própria constituição ("metadado/sidecar"). É *best-effort*: se o sistema de arquivos do destino não
   suportar (ou a sincronização do SharePoint/OneDrive descartar o fluxo), o Condomínio abre uma raiz nova, comportamento
   que já existe e é testado (`Sem_traceparent_cria_nova_raiz_sem_erro`). `LeitorTraceparent` passa a ler o fluxo
   alternativo e, por compatibilidade, o sidecar. O `traceparent` continua registrado na tabela `entrega` do Inter.
6. **Spec/constituição**: atualizar FR-006/FR-009/FR-010/FR-012/SC-006 e os Edge Cases da spec 002 (e a regra de colisão
   da spec 001, linha 136) com a nova regra de nomes e de trace.

**Alternatives**:

- *Colisão em `Processed` com nome único só no histórico* (comportamento atual, restrito a `Processed`/`Fail`): muito mais
  simples e sem risco de reescrever arquivos; a entrega ao Condomínio continua sempre com o nome original. Contraria o
  "nunca renomear" literal, mas só no histórico interno e só em colisão.
- *Sobrescrever em `Processed`*: cumpre o "nunca renomear", mas perde o histórico da parte A e viola o Princípio III.
- *Trace sem propagação* (só deixar de gravar o sidecar): simples, mas viola o Princípio IV — exige emenda da
  constituição (`/speckit-constitution`).

**Files likely to change**:

- `src/InativacaoUsuarioInter.Service/Arquivos/GerenciadorArquivos.cs`
- `src/InativacaoUsuarioInter.Service/Arquivos/NomeadorPartes.cs`
- `src/InativacaoUsuarioInter.Service/Replicacao/ServicoReplicacao.cs`
- `src/InativacaoUsuarioInter.Service/Entrada/LeitorTraceparent.cs`
- `src/InativacaoUsuarioInter.Service/Estado/*` (nome de destino da entrega; possivelmente migração v5)
- `specs/002-ajustes-pastas-fluxo/spec.md`, `data-model.md`, `contracts/replicacao.md`; `specs/001-…/spec.md`
- `tests/InativacaoUsuarioInter.Tests.Integration/Replicacao/ReplicacaoTests.cs`, `…/FluxoInter/FluxoInterTests.cs`,
  `…/FluxoCondominio/FluxoCondominioTests.cs`, `…/Idempotencia/ExecucaoDuplaTests.cs`, `…/InjecaoFalhas/*`,
  `…/Telemetria/*`, `tests/InativacaoUsuarioInter.Tests.Unit/Arquivos/*`

**Tests to add or update**:

- Arquivo segregado (sucesso + falha): `Processed/X.txt` e `{FolderPathCondominio}/Input/X.txt` com só os sucessos; nenhum `.traceparent` no `Input` do Condomínio.
- Segregação com pendência: 1ª passada entrega `X.txt` (A); após reprocessar as pendências, B é entregue como `X.txt` só depois que o Condomínio consumir A; nenhum arquivo renomeado em `Processed`/Condomínio.
- Condomínio indisponível com A e B na fila: B espera A; nada é sobrescrito.
- Propagação de trace pelo fluxo alternativo (Windows), e raiz nova quando ele não existe.
- Atualizar os testes que esperam `__sucesso-` (`ReplicacaoTests`, `FluxoCondominioTests`, `FalhasBancoTests`, `ExecucaoDuplaTests`, `MigracaoLegadoTests`, `ElegibilidadeTests`) e os de colisão.
- Idempotência (Princípio III): crash entre gravar a mescla em `Processed` e registrar a parte não duplica registros na mescla.

## Risks & Considerations

- **Princípio III** (nunca sobrescrever, mesmo estado em N execuções): a mescla em `Processed` reescreve um arquivo
  existente; precisa ser idempotente (não duplicar registros numa retomada) e atômica.
- **Princípio IV** (propagação de trace): remover o sidecar sem alternativa exige emenda da constituição.
- **Fluxo alternativo NTFS**: não sobrevive a cópias por ferramentas que não o preservam, a sistemas de arquivos não NTFS
  nem, provavelmente, à sincronização do SharePoint/OneDrive. Nesses casos perde-se só a correlação, não dados.
- **Entrega em espera**: com o nome fixo, uma parte B fica parada em `Output` enquanto o Condomínio não consumir a A
  (Condomínio parado = fila parada para esse nome). Coberto pelo alerta FR-007a.
- **Interação com o bug anterior (`ja-inativo-sem-replicacao`)**: a entrega é única por `(hash, nome_output)` e o
  Condomínio identifica arquivos por nome + conteúdo. Com o nome original, dois arquivos de origem com o **mesmo nome**
  e a mesma parte de sucesso (nome reutilizado) passam a ser vistos como o mesmo arquivo no Condomínio. Com nomes por
  timestamp, não ocorre.
- Muitos testes de integração assumem `__sucesso-`; o diff de testes será grande.

## Decisões do usuário (2026-11-12)

Estas decisões prevalecem sobre a remediação preferida acima onde houver diferença:

- **Colisão em `Processed`**: **mesclar** — a nova parte de sucesso é acrescentada ao arquivo existente com o nome
  original (reescrita atômica e idempotente).
- **Trace**: **não propagar**. `CopiarAsync` deixa de gravar `.traceparent` no Condomínio e **não** há metadado
  alternativo (descartado o fluxo NTFS). O Condomínio sempre abre uma raiz de trace nova. Exige **emendar o Princípio IV**
  da constituição (trecho "propagado … na replicação Inter → Condomínio") — fazer junto com a correção, via
  `/speckit-constitution` ou edição registrada no relatório do fix. O `traceparent` continua registrado na tabela
  `entrega` do Inter (correlação manual possível pelo nome do arquivo + horário).
- **Escopo de "nunca renomear"**: vale também para **`Fail`** (colisão → mesclar, como em `Processed`; FR-010/SC-006
  mudam). **Partes `__pendente-` ficam como estão.**
- Entrega ao Condomínio com colisão de nome: espera na fila `Output` (item 2 da remediação), sem renomear.
- Fila interna `Output`: pode usar nome único quando duas partes do mesmo original coexistirem, desde que a entrega use o
  nome original (item 3). Não foi objeto de pergunta; manter, salvo indicação em contrário.

## Open Questions

- Resolvidas acima: colisão em `Processed`, trace, escopo (`Fail` sim, pendentes não).
- [NEEDS CLARIFICATION: confirmar que "Success" = parte de sucesso em `Processed`, e não uma pasta nova `Success`.
  Assumido assim por padrão.]
