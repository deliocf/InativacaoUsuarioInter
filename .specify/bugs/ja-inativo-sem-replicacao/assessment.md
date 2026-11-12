# Bug Assessment: Parte de sucesso com conteúdo já entregue não é replicada ao Condomínio

- **Slug**: ja-inativo-sem-replicacao
- **Created**: 2026-11-12
- **Source**: pasted text
- **Verdict**: valid
- **Severity**: high

## Report (verbatim or summarized)

> Correção comportamental:
> Durante o processo de importação no modo instância Inter, quando é identificado que um registro já está inativo no
> WAccess ele não está sendo copiado para a pasta `SharePointFolder:FolderPathCondominio/Input`. O comportamento
> esperado é enviar uma cópia para esta pasta sempre, pois na outra instância do WAccess este registro pode estar ativo
> ou seja pode estar com `CHState=0`.
> Não é necessário copiar para esta pasta apenas se houver erro grave como cpf não localizado.

## Symptom

Na instância Inter, um arquivo cujos CPFs resultam em `JA_INATIVO` é concluído normalmente (auditoria gravada, parte de
sucesso em `Processed`), mas a cópia **não chega** a `{FolderPathCondominio}/Input` — e nenhum log nem alerta indica
isso. Esperado: toda parte de sucesso (`INATIVADO` ou `JA_INATIVO`) é entregue ao Condomínio; só falhas definitivas
(`CPF_NAO_ENCONTRADO`, `DADO_INVALIDO`, rejeição) ficam de fora (FR-011 da spec 002, inalterado).

O código **não** exclui `JaInativo` da entrega (ele entra em `sucessos`, `src/InativacaoUsuarioInter.Service/Ciclo/ProcessadorArquivo.cs:108`).
A perda ocorre porque a entrega é deduplicada **pelo hash do conteúdo da parte**: quando os mesmos CPFs são reenviados
(e por isso já estão inativos), a parte de sucesso gerada tem bytes idênticos a uma parte já entregue antes, e o
serviço a descarta como "já entregue".

### Evidência do ambiente local (diário `C:\ProgramData\InativacaoUsuarioInter\Inter\estado.db`, lido em modo somente leitura)

| Arquivo de entrada | Resultado | Parte de sucesso (hash) | Entrega |
|---|---|---|---|
| `20261112111122.txt` (17:25Z) | 3× `INATIVADO` + 1× `CPF_NAO_ENCONTRADO` | `20261112111122__sucesso-95990873.txt` (`95990873…16bb`) | `Entregue` |
| `20261112111333.txt` (17:28Z) | 3× `JA_INATIVO` (mesmos CPFs) + 1× `CPF_NAO_ENCONTRADO` | `20261112111333__sucesso-95990873.txt` (**mesmo hash** `95990873…16bb`) | **nenhuma linha nova** |

- `parte_gerada` tem uma única linha para o hash `95990873…` (aponta para a parte de `…111122`); a parte de `…111333` não foi registrada.
- `entrega` tem uma única linha para esse hash, já `Entregue` (17:25Z).
- `Files/Inter/Processed/20261112111333__sucesso-95990873.txt` existe; `Files/Inter/Output` e `Files/Condominio/Input` estão vazios; `saude.json` mostra `entregasPendentes: 0`.

## Reproduction

1. Instância `Inter`, Condomínio acessível. Colocar em `{FolderPathInter}/Input` um arquivo com CPFs ativos A, B, C
   (opcionalmente + um CPF inexistente para forçar segregação). Aguardar o ciclo: parte `…__sucesso-<h8>` é entregue em
   `{FolderPathCondominio}/Input`.
2. (Opcional) Retirar o arquivo de `{FolderPathCondominio}/Input` (ex.: consumido pelo Condomínio).
3. Colocar em `{FolderPathInter}/Input` **outro** arquivo (nome e/ou conteúdo bruto diferentes) com os mesmos A, B, C e o
   mesmo responsável, na mesma ordem e formato. Agora todos resultam em `JA_INATIVO`.
4. Observar: parte `…__sucesso-<mesmo h8>` aparece em `Processed`, a cópia em `Output` é apagada na reconciliação do
   ciclo seguinte e nada chega a `{FolderPathCondominio}/Input`.

Também reproduzível no fluxo integral: se a parte/arquivo entregue tiver bytes idênticos a um conteúdo entregue antes,
o mesmo descarte ocorre.

## Suspected Code Paths

- `src/InativacaoUsuarioInter.Service/Estado/Migracoes.cs:41-49` — `parte_gerada.hash` e `entrega.hash` são `PRIMARY KEY`: a identidade da parte/entrega é só o hash do conteúdo, sem a origem.
- `src/InativacaoUsuarioInter.Service/Estado/ArmazenamentoEstadoSqlite.cs:226-245` — `RegistrarParteAsync` usa `INSERT OR IGNORE` em `parte_gerada` e `entrega`: a segunda parte com o mesmo conteúdo é ignorada silenciosamente (a entrega existente já está `Entregue`).
- `src/InativacaoUsuarioInter.Service/Arquivos/GerenciadorArquivos.cs:257-273` — `GravarParteAsync` grava a parte em `Processed` e a cópia em `Output` com nome novo, depois registra no diário (ignorado, ver acima).
- `src/InativacaoUsuarioInter.Service/Replicacao/ServicoReplicacao.cs:138-143` — `ReconciliarAsync`: arquivo de `Output` cujo hash já tem entrega `Entregue` → `LimparSaidaAsync`, que o apaga porque `Processed` tem o mesmo conteúdo (`:165-171`). É aqui que a cópia some, sem log (só tag de span `removido`).
- `specs/002-ajustes-pastas-fluxo/spec.md:138` — FR-009: "MUST NOT entregar o mesmo conteúdo mais de uma vez". O código cumpre a regra; é a regra que conflita com o comportamento agora pedido.
- `src/InativacaoUsuarioInter.Service/Ciclo/ProcessadorArquivo.cs:51-61` — no destino (instância Condomínio), um arquivo cujo hash já está terminal no diário é removido como duplicado **sem processar**. Mesmo que o Inter volte a entregar, o Condomínio descartaria uma cópia com conteúdo já processado — e o objetivo do relato (reinativar quem voltou a `CHState=0` no Condomínio) não seria atingido.
- `src/InativacaoUsuarioInter.Service/Entrada/ValidadorRegistro.cs:51` e `Entrada/LeitorArquivo.cs:56` — a chave de idempotência do registro é `instância + hashRaiz + CPF`; com o mesmo conteúdo, o Condomínio encontra os registros já terminais e não refaz o passo A/B.

## Root Cause Hypothesis

A entrega Inter → Condomínio (e o diário em geral) identifica trabalho só pelo hash do conteúdo. Ao reenviar CPFs que já
foram inativados — o caso típico de `JA_INATIVO` —, a serialização da parte de sucesso (`SerializadorPartes.SerializarRegistros`)
gera bytes idênticos aos de uma parte já entregue; `INSERT OR IGNORE` descarta o registro da nova parte e da nova entrega,
e a reconciliação de `Output` apaga a cópia porque o hash "já foi entregue". Isso é coerente com FR-009 da spec 002 e com a
deduplicação de arquivos da spec 001, mas contradiz o comportamento esperado ("enviar sempre"). Confiança: **alta**
(confirmada pelo diário local: dois arquivos de origem distintos, uma única parte/entrega para o hash `95990873…`).

## Proposed Remediation

**Preferred**: dar identidade de origem à parte e à entrega, e ao arquivo no destino.

1. **Inter — parte e entrega por origem**: nova migração do diário que troca a chave de `parte_gerada` para
   `(hash, hash_origem)` e a de `entrega` para o nome em `Output` (`nome_output`, já único na fila graças a
   `DestinoLivreAsync`) ou `(hash, hash_origem)`. Ajustar `RegistrarParteAsync`, `GarantirEntregaAsync`,
   `ObterEntregaAsync`/`ObterParteAsync`, `ListarEntregasPendentesAsync`, `RegistrarEntregaAsync`,
   `RegistrarFalhaEntregaAsync` e `ServicoReplicacao.ReconciliarAsync`/`RestaurarNaFilaAsync` para resolver a entrega pelo
   arquivo da fila, não só pelo hash. Assim, uma nova origem gera uma nova entrega; a mesma origem reprocessada (crash,
   retomada) continua idempotente.
2. **Condomínio — não descartar conteúdo repetido vindo de outra origem**: incluir o nome do arquivo na identidade do
   arquivo de entrada (ex.: `hashRaiz` derivado de `hash + nome`, ou chave de `arquivo` = `(hash, nome)`), para que
   `ProcessadorArquivo` só trate como duplicado o **mesmo arquivo** (mesmo nome e conteúdo) e as chaves de registro
   (`ValidadorRegistro.Chave`) sejam novas para uma nova entrega. O passo A/B roda de novo; quem voltou a `CHState=0` é
   inativado; quem continua inativo vira `JA_INATIVO` (FR-015 já trata isso sem efeitos colaterais).
3. **Spec**: emendar FR-009 da spec 002 ("MUST NOT entregar o mesmo **arquivo gerado** mais de uma vez") e a regra de
   arquivo duplicado da spec 001, e registrar que a chave continua determinística (nome + conteúdo, sem horário/GUID —
   compatível com o Princípio III).
4. **Observabilidade**: logar (Information/Warning) quando `ReconciliarAsync` remover de `Output` algo considerado já
   entregue, para que um descarte não volte a ser silencioso.

**Alternatives**:
- *Só o item 1 (Inter)*: menor, mas inútil para o objetivo — o Condomínio descartaria a cópia como duplicada e não
  reinativaria ninguém.
- *Tornar o conteúdo único* (ex.: cabeçalho/linha com a origem dentro da parte): evita mexer nas chaves, mas altera o
  formato CSV/TXT/JSON consumido pelo Condomínio e por quem lê `Processed`; não recomendado.
- *Desligar a deduplicação por conteúdo no Condomínio*: simples, mas quebra a idempotência de reentregas da mesma parte
  (retomada após crash entre cópia e registro `Entregue`) — viola o Princípio III.

**Files likely to change**:
- `src/InativacaoUsuarioInter.Service/Estado/Migracoes.cs`
- `src/InativacaoUsuarioInter.Service/Estado/ArmazenamentoEstadoSqlite.cs`
- `src/InativacaoUsuarioInter.Service/Estado/IArmazenamentoEstado.cs`
- `src/InativacaoUsuarioInter.Service/Arquivos/GerenciadorArquivos.cs`
- `src/InativacaoUsuarioInter.Service/Replicacao/ServicoReplicacao.cs`
- `src/InativacaoUsuarioInter.Service/Ciclo/ProcessadorArquivo.cs`
- `src/InativacaoUsuarioInter.Service/Entrada/LeitorArquivo.cs` / `Entrada/ValidadorRegistro.cs`
- `specs/002-ajustes-pastas-fluxo/spec.md` (FR-009) e, se aplicável, `specs/001-inativacao-usuarios-waccess/spec.md`
- `tests/InativacaoUsuarioInter.Tests.Integration/Replicacao/ReplicacaoTests.cs`
- `tests/InativacaoUsuarioInter.Tests.Integration/FluxoCondominio/FluxoCondominioTests.cs`
- `tests/InativacaoUsuarioInter.Tests.Integration/Idempotencia/ExecucaoDuplaTests.cs`
- `tests/InativacaoUsuarioInter.Tests.Integration/Migracao/*`

**Tests to add or update**:
- Inter: dois arquivos de origem distintos com os mesmos CPFs (o segundo todo `JA_INATIVO`, com e sem um CPF inexistente)
  → as duas partes de sucesso chegam a `{FolderPathCondominio}/Input`, cada uma uma única vez.
- Inter: arquivo só com `CPF_NAO_ENCONTRADO`/`DADO_INVALIDO` → nada entregue (FR-011, regressão).
- Inter: retomada após crash entre gravar a parte e registrar `Entregue` → a mesma parte não é entregue duas vezes.
- Condomínio: arquivo com conteúdo idêntico a um já processado mas nome diferente → reprocessado (passo A executado,
  CPF reativado volta a ser inativado); mesmo nome e mesmo conteúdo → continua no-op.
- Migração do diário: diário existente (com `entrega` `Entregue`/`NaoEntregue`) migra sem perder entregas nem reentregar o histórico.

## Risks & Considerations

- **Migração do diário SQLite** (troca de chave primária = recriar tabelas): precisa ser transacional e testada contra
  diários reais; erro aqui pode reentregar o histórico inteiro ou perder pendências.
- **Princípio III (idempotência, não negociável)**: a nova chave deve continuar determinística; usar nome + conteúdo
  respeita o texto ("derivada do conteúdo… nunca de horário ou GUID"), mas a mudança deve passar pelo Constitution Check
  do plano.
- **Volume no Condomínio**: reenvios passam a gerar processamento e consultas ao WAccess do Condomínio de novo; esperado
  e pequeno (`Processamento:TamanhoMaximoBytes`).
- **Auditoria**: o Condomínio passará a gravar uma linha por CPF a cada entrega nova (o que é o comportamento desejado,
  mas muda o volume do log).
- **Inter com arquivo idêntico renomeado**: com o item 2 aplicado às duas instâncias, o Inter também reprocessaria um
  arquivo de entrada com o mesmo conteúdo e outro nome — ver pergunta abaixo.

## Open Questions

- [NEEDS CLARIFICATION: um arquivo de entrada **byte a byte idêntico** a um já processado (mesmo nome) deve ser
  reentregue ao Condomínio? Hoje é removido como duplicado antes de qualquer processamento (`ProcessadorArquivo.cs:54`);
  a proposta mantém esse no-op e só trata como novo o que tem nome ou conteúdo diferente.]
- [NEEDS CLARIFICATION: a nova identidade por nome deve valer também na instância Inter (reprocessar arquivo com mesmo
  conteúdo e outro nome) ou só no Condomínio?]
- [NEEDS CLARIFICATION: confirmar que "erro grave" = falhas definitivas atuais (`CPF_NAO_ENCONTRADO`, `DADO_INVALIDO`,
  arquivo rejeitado) e que pendências transitórias continuam fora da entrega até concluírem.]
