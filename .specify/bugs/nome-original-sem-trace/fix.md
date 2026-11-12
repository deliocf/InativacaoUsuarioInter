# Bug Fix: Parte de sucesso renomeada e `.traceparent` copiado para o Condomínio

- **Slug**: nome-original-sem-trace
- **Fixed**: 2026-11-12
- **Assessment**: ./assessment.md
- **Status**: applied

## Summary

A parte de sucesso passa a ter sempre o nome do arquivo original, em `Processed` (Inter e Condomínio) e na entrega a
`{FolderPathCondominio}/Input`. Nada é renomeado no histórico nem no destino: colisão em `Processed`/`Fail` é
**mesclada** (união ordenada, idempotente, atômica); colisão no `Input` do Condomínio faz a entrega **esperar** na fila
até o nome ficar livre. O Inter deixa de gravar `.traceparent` no Condomínio, sem propagação alternativa (constituição
emendada para 2.0.0, Princípio IV).

## Changes

| File | Change | Notes |
| --- | --- | --- |
| `src/InativacaoUsuarioInter.Service/Arquivos/MescladorHistorico.cs` | added | `GravarAsync`/`MoverAsync`/`Mesclar`: TXT por linha, JSON por elemento (formato das partes), bloco opaco para conteúdo inválido; no-op quando nada é novo |
| `src/InativacaoUsuarioInter.Service/Arquivos/EscritorAtomico.cs` | modified | `SubstituirAsync` (temporário + rename com substituição), usado só pela mescla |
| `src/InativacaoUsuarioInter.Service/Arquivos/NomeadorPartes.cs` | modified | `ArquivoFalhas` → `NomeOriginal` (sucesso e falhas) |
| `src/InativacaoUsuarioInter.Service/Arquivos/GerenciadorArquivos.cs` | modified | parte de sucesso com o nome original; `Processed`/`Fail` (inclusive integral e rejeitado) via `MescladorHistorico`; pendentes e `Output` continuam com nome único em colisão; registra o nome de destino da entrega; protege a retomada que acharia o original já mesclado |
| `src/InativacaoUsuarioInter.Service/Estado/IArmazenamentoEstado.cs`, `ArmazenamentoEstadoSqlite.cs` | modified | `RegistrarParteAsync(..., nomeDestino)` grava `entrega.nome_destino` no registro; `EntregaPendente.NomeDestino` (sem migração: coluna já existia) |
| `src/InativacaoUsuarioInter.Service/Replicacao/ServicoReplicacao.cs` | modified | entrega com `NomeDestino ?? NomeOutput`; destino ocupado com outro conteúdo → `DestinoOcupado` (fica em `Output`, sem marcar indisponível, sem renomear); sem sidecar `.traceparent`; limpeza de `Output` não renomeia nada em `Processed` (parte com histórico existente → só remove; herdado → move/mescla com nome original) |
| `.specify/memory/constitution.md` | modified | **emenda 2.0.0** do Princípio IV: sem propagação de trace Inter → Condomínio; Sync Impact Report |
| `specs/002-ajustes-pastas-fluxo/spec.md` | modified | Clarifications da correção; US Condomínio cenário 3 removido; Edge Cases; FR-009 (nome original, sem `.traceparent`); FR-010 (mescla); FR-010a novo; SC-006; Assumptions |
| `specs/002-ajustes-pastas-fluxo/contracts/replicacao.md` | modified | protocolo sem passo do sidecar; regra de colisão = espera; lado Condomínio |
| `specs/002-ajustes-pastas-fluxo/data-model.md` | modified | uso de `entrega.nome_destino`, `DestinoOcupado`, `caminho_final` mesclado |
| `tests/InativacaoUsuarioInter.Tests.Unit/Arquivos/MescladorHistoricoTests.cs` | added | 6 testes |
| `tests/InativacaoUsuarioInter.Tests.Unit/Arquivos/PartesTests.cs` | modified | `NomeOriginal` |
| `tests/InativacaoUsuarioInter.Tests.Contract/Replicacao/ReplicacaoContratoTests.cs` | modified | sem sidecar; colisão aguarda e entrega com o nome original |
| `tests/InativacaoUsuarioInter.Tests.Integration/Replicacao/ReplicacaoTests.cs` | modified + 2 tests | nomes originais; 2ª parte do mesmo original; mescla em `Fail` (TXT e rejeitado JSON) |
| `tests/InativacaoUsuarioInter.Tests.Integration/FluxoInter/FluxoInterTests.cs` | modified | parte de sucesso com nome original; colisão em `Fail` mesclada |
| `tests/InativacaoUsuarioInter.Tests.Integration/InjecaoFalhas/FalhasBancoTests.cs` | modified | CA07: um único `Processed/<original>` com os três CPFs |
| `tests/InativacaoUsuarioInter.Tests.Integration/PontaAPonta/InterCondominioTests.cs` | modified | nome original; Condomínio com trace próprio e nenhum `.traceparent` |
| `tests/InativacaoUsuarioInter.Tests.Integration/FluxoCondominio/FluxoCondominioTests.cs` | modified | parte de sucesso do Condomínio com nome original |
| `tests/InativacaoUsuarioInter.Tests.Integration/Estado/ArmazenamentoEstadoTests.cs` | modified | `nome_destino` registrado com a entrega |

## Diff Highlights

```csharp
// ServicoReplicacao.CopiarAsync — nome original; nunca renomeia nem sobrescreve; sem sidecar
var nome = entrega.NomeDestino ?? entrega.NomeOutput;
var final = Path.Combine(pastaDestino, nome);
if (File.Exists(final))
{
    return await HashConteudo.CalcularArquivoAsync(final, cancelamento) == entrega.Hash
        ? new ResultadoCopia(false, nome, null)   // já entregue
        : new ResultadoCopia(true, nome, null);   // ocupado: espera em Output
}
```

```csharp
// GerenciadorArquivos.SegregarAsync — parte de sucesso com o nome do original
_ => NomeadorPartes.NomeOriginal(leitura.BaseRaiz, extensao),
// GravarParteAsync — Processed/Fail: mescla em colisão (pendentes mantêm nome único)
await MescladorHistorico.GravarAsync(destino, conteudo, cancelamento);
```

## Tests Added or Updated

- `Unit/Arquivos/MescladorHistoricoTests.cs` — TXT união idempotente; normalização de BOM/quebra de linha; JSON união no formato das partes; conteúdo inválido como bloco único; `GravarAsync` cria/mescla/no-op mantendo o nome; `MoverAsync` mescla, remove a origem e é retomável.
- `Contract/Replicacao/ReplicacaoContratoTests.cs::Copia_byte_a_byte_sem_sidecar_traceparent_e_sem_temporario_visivel` — nenhum `.traceparent` no Condomínio.
- `Contract/Replicacao/ReplicacaoContratoTests.cs::Colisao_com_conteudo_diferente_aguarda_e_entrega_com_nome_original_depois_de_consumido`.
- `Integration/Replicacao/ReplicacaoTests.cs::Segunda_parte_do_mesmo_original_eh_mesclada_em_Processed_e_entregue_com_o_nome_original_depois_de_consumida` — cenário principal (pendência reprocessada).
- `Integration/Replicacao/ReplicacaoTests.cs::Falhas_com_mesmo_nome_sao_mescladas_em_Fail_sem_renomear` — TXT e arquivo rejeitado (JSON inválido).
- `Integration/FluxoInter/FluxoInterTests.cs::Arquivo_de_falhas_com_mesmo_nome_e_conteudo_diferente_eh_mesclado_sem_renomear_nem_perder` (substitui o teste da regra antiga).
- `Integration/PontaAPonta/InterCondominioTests.cs` — ponta a ponta com nome original e trace próprio no Condomínio.
- Ajustes de nome em `CA09`, `Falhas_e_rejeitados_nunca_sao_entregues`, testes do bug `ja-inativo-sem-replicacao`, `CA07`, `FluxoCondominioTests`.

## Local Verification

- `dotnet build src/InativacaoUsuarioInter.Service.slnx` → sucesso, 0 avisos.
- 1ª execução de `dotnet test src/InativacaoUsuarioInter.Service.slnx` → 4 falhas: (a) formato da mescla JSON (elementos crus sem indentação) — corrigido reescrevendo os elementos; (b) dois testes FR-004a — **defeito real** da limpeza de `Output`: removia a cópia confiando só no diário, sem conferir que o histórico existia em `Processed`; corrigido (só remove se o arquivo do histórico existir); (c) teste que fixava a regra antiga de colisão em `Fail` — atualizado para a mescla.
- 2ª execução (SQL Server via Testcontainers/Docker) → **Passed**: 277 testes, 276 ok, 0 falhas, 1 ignorado (`LocalDbOptInTests`, opt-in). Unit 127/127.

## Deviations from Assessment

- **Trace**: conforme a decisão do usuário, **sem** metadado NTFS; a propagação Inter → Condomínio foi abandonada e o
  Princípio IV emendado (versão 1.0.0 → **2.0.0**, MAJOR por redefinir uma regra MUST). A governança exige aprovação da
  emenda por PR — **pendente**. O Condomínio continua aceitando um `.traceparent` se ele existir (arquivos antigos ou
  manuais); só o Inter deixou de gravá-lo.
- **Arquivo rejeitado em `Fail`** também segue a mescla (decisão "Fail também"); como o conteúdo é inválido, é
  acrescentado como bloco (separado por `\r\n`), uma única vez.
- **Arquivo integral** com colisão em `Processed` (mesmo nome reutilizado com outro conteúdo) também é mesclado — antes
  recebia nome único; decorre de "nunca renomear".
- **Fila `Output`** mantém nome único quando duas partes do mesmo original coexistem (pasta interna); a entrega usa o
  nome original guardado em `entrega.nome_destino`. Não houve migração de esquema (a coluna já existia).
- **`MigradorLegado`** (migração das pastas antigas da feature 001) não foi alterado: em colisão ainda gera nome único.
  Fora do escopo pedido; ver Follow-ups.

## Follow-ups

- Aprovar a emenda 2.0.0 da constituição por PR (Governance) e, se desejado, rodar `/speckit-constitution` para revisar o texto.
- **Restauração da fila**: se a cópia de uma parte sumir de `Output` antes da entrega e o histórico em `Processed` já
  estiver mesclado com outra parte, ela não pode mais ser recriada a partir de `Processed` (log "sem origem"). Caso
  raro (exige apagar `Output` à mão).
- **Espera por nome ocupado**: se o Condomínio ficar parado, entregas com o mesmo nome aguardam em `Output`; o alerta
  `ENTREGA_PENDENTE_ACIMA_LIMITE` (24 h) cobre.
- Avaliar se `MigradorLegado` também deve mesclar em vez de gerar nome único.
- O histórico em `Processed`/`Fail` mesclado é normalizado (UTF-8 sem BOM, `\r\n`, JSON indentado) apenas quando há colisão.
