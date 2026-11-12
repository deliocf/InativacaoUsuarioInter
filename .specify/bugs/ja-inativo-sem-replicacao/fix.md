# Bug Fix: Parte de sucesso com conteúdo já entregue não é replicada ao Condomínio

- **Slug**: ja-inativo-sem-replicacao
- **Fixed**: 2026-11-12
- **Assessment**: ./assessment.md
- **Status**: applied

## Summary

A entrega Inter → Condomínio e o reconhecimento de arquivos já processados deixaram de usar só o hash do conteúdo. A
parte e a entrega passam a ser únicas por origem — `(hash, hash_origem)` e `(hash, nome_output)` —, e o arquivo de
entrada passa a ser identificado por **nome + conteúdo** nas duas instâncias. Assim, CPFs reenviados num novo arquivo
(que viram `JA_INATIVO` no Inter e geram uma parte com os mesmos bytes de uma já entregue) são entregues de novo, e o
Condomínio os reprocessa, reinativando quem voltou a `CHState = 0`. Falhas definitivas continuam fora da entrega (FR-011).

## Changes

| File | Change | Notes |
| --- | --- | --- |
| `src/InativacaoUsuarioInter.Service/Arquivos/HashConteudo.cs` | modified | `ChaveArquivo(nome, hashConteudo)` = `SHA-256(nome\|hashConteudo)` |
| `src/InativacaoUsuarioInter.Service/Estado/Migracoes.cs` | modified | migração **v4**: recria `parte_gerada` (PK `(hash, hash_origem)`) e `entrega` (PK `(hash, nome_output)`) preservando linhas e ordem (`rowid`) |
| `src/InativacaoUsuarioInter.Service/Estado/IArmazenamentoEstado.cs` | modified | `ResolverChaveArquivoAsync`; `ObterEntregaAsync`, `RegistrarEntregaAsync`, `RegistrarFalhaEntregaAsync` passam a receber `nomeOutput` |
| `src/InativacaoUsuarioInter.Service/Estado/ArmazenamentoEstadoSqlite.cs` | modified | implementações acima; `ObterParteAsync` prefere parte entregável e mais recente; `ResolverHashRaizAsync` pega a parte pendente mais recente; compatibilidade com diário antigo (mesmo nome + conteúdo → identidade = hash do conteúdo) |
| `src/InativacaoUsuarioInter.Service/Entrada/LeitorArquivo.cs` | modified | `ResultadoLeitura.Hash` = identidade (nome + conteúdo); novo `HashDoConteudo` = SHA-256 dos bytes |
| `src/InativacaoUsuarioInter.Service/Arquivos/GerenciadorArquivos.cs` | modified | comparações em disco e hash da parte integral/rejeitada usam `HashDoConteudo`; `hash_origem` usa a identidade |
| `src/InativacaoUsuarioInter.Service/Replicacao/ServicoReplicacao.cs` | modified | reconciliação de `Output` procura a entrega por `(hash, nome)`; log `Information` quando um arquivo da fila já entregue sai sem nova cópia (antes era silencioso) |
| `specs/002-ajustes-pastas-fluxo/spec.md` | modified | FR-009 corrigido ("mesmo arquivo gerado", não "mesmo conteúdo"), novo FR-009a (identidade nome + conteúdo), registro da correção nas Clarifications |
| `specs/002-ajustes-pastas-fluxo/data-model.md` | modified | operações novas/alteradas e migração v4 |
| `tests/InativacaoUsuarioInter.Tests.Integration/Estado/ArmazenamentoEstadoTests.cs` | modified + added tests | assinaturas novas; 3 testes novos |
| `tests/InativacaoUsuarioInter.Tests.Integration/Replicacao/ReplicacaoTests.cs` | added tests | 3 testes novos |
| `tests/InativacaoUsuarioInter.Tests.Integration/FluxoCondominio/FluxoCondominioTests.cs` | added test | 1 teste novo |
| `tests/InativacaoUsuarioInter.Tests.Integration/FluxoInter/FluxoInterTests.cs` | modified + added test | teste que fixava a regra antiga ajustado (ver Deviations) |

## Diff Highlights

```csharp
// LeitorArquivo — identidade do arquivo = nome + conteúdo (com compatibilidade para o diário antigo)
var conteudo = HashConteudo.Calcular(bytes);
var hash = await estado.ResolverChaveArquivoAsync(nome, conteudo, cancelamento);
var origem = await estado.ResolverHashRaizAsync(conteudo, cancelamento);
var hashRaiz = origem?.HashRaiz ?? hash;
```

```csharp
// ServicoReplicacao.ReconciliarAsync — "já entregue" é por arquivo da fila, não por conteúdo
if (await estado.ObterEntregaAsync(hash, info.Name, cancelamento) is { Estado: EstadosEntrega.Entregue } entregue)
{
    LogJaEntregue(logger, info.Name, entregue.NomeDestino);
    await LimparSaidaAsync(caminho, hash, cancelamento);
    continue;
}
```

```sql
-- Migracoes v4 (trecho)
CREATE TABLE entrega_v4 (..., PRIMARY KEY (hash, nome_output));
INSERT INTO entrega_v4 (...) SELECT ... FROM entrega ORDER BY rowid;
DROP TABLE entrega;
ALTER TABLE entrega_v4 RENAME TO entrega;
```

## Tests Added or Updated

- `Estado/ArmazenamentoEstadoTests.cs::Mesma_parte_de_origens_diferentes_gera_duas_partes_e_duas_entregas` — a segunda origem com a mesma parte cria uma entrega pendente nova (a raiz do bug no diário).
- `Estado/ArmazenamentoEstadoTests.cs::Chave_do_arquivo_usa_nome_e_conteudo_e_reconhece_o_diario_da_versao_anterior` — compatibilidade com linhas antigas; outro nome → outra identidade.
- `Estado/ArmazenamentoEstadoTests.cs::Migracao_v4_preserva_partes_e_entregas_de_um_diario_v3` — linhas, estados, tentativas e ordem da fila preservados; depois da migração a mesma parte de outra origem é aceita.
- `Estado/ArmazenamentoEstadoTests.cs::Migracao_v3_...` / `Entrega_pendente_mais_antiga_...` — atualizados para as assinaturas com `nomeOutput`.
- `Replicacao/ReplicacaoTests.cs::Bug_JA_INATIVO_reenviado_em_novo_arquivo_eh_entregue_mesmo_com_parte_identica_a_ja_entregue` — reproduz o cenário do diário local (`20261112111122` → `20261112111333`): a segunda parte, byte a byte igual à primeira, chega a `{FolderPathCondominio}/Input`; o CPF não encontrado fica de fora.
- `Replicacao/ReplicacaoTests.cs::Bug_arquivo_integral_com_mesmo_conteudo_e_outro_nome_eh_processado_e_entregue_de_novo` — fluxo integral; e o mesmo nome + conteúdo continua no-op.
- `Replicacao/ReplicacaoTests.cs::Arquivo_so_com_cpf_nao_encontrado_nao_eh_entregue` — FR-011 (erro grave não é entregue).
- `FluxoCondominio/FluxoCondominioTests.cs::Bug_entrega_com_conteudo_ja_processado_e_outro_nome_reinativa_cadastro_reativado` — o Condomínio reprocessa a reentrega e volta `CHState` de 0 para 1 (`INATIVADO`).
- `FluxoInter/FluxoInterTests.cs::Mesmo_arquivo_depositado_de_novo_nao_gera_efeito_e_sai_da_entrada` — passa a redepositar o **mesmo nome**; novo `Mesmo_conteudo_com_outro_nome_eh_processado_como_arquivo_novo`.

## Local Verification

- `dotnet build src/InativacaoUsuarioInter.Service.slnx` → sucesso, 0 avisos.
- `dotnet test src/InativacaoUsuarioInter.Service.slnx` (1ª execução, antes do ajuste em `FluxoInterTests`) → 268 testes: 266 ok, 1 ignorado (`LocalDbOptInTests`, opt-in), **1 falha**: `FluxoInterTests.Mesmo_arquivo_depositado_de_novo_nao_gera_efeito_e_sai_da_entrada`, que fixava a regra antiga (mesmo conteúdo com outro nome = no-op). Todos os testes novos passaram, inclusive os de idempotência (`ExecucaoDuplaTests`), migração legada e injeção de falhas.
- `dotnet test tests/InativacaoUsuarioInter.Tests.Integration` (após o ajuste, SQL Server via Testcontainers/Docker) → **Passed**: 91 testes, 90 ok, 0 falhas, 1 ignorado (`LocalDbOptInTests`, opt-in). Unit e Contract já tinham passado na 1ª execução e não foram afetados pelo ajuste (só um arquivo de teste de integração mudou).

## Deviations from Assessment

- **Teste fora da lista do assessment**: `FluxoInter/FluxoInterTests.cs` não estava em "Files likely to change", mas fixava exatamente a regra que o assessment manda mudar. Ajustado para redepositar o mesmo nome (o no-op continua coberto) e acrescentado um teste para o comportamento novo.
- **Perguntas abertas do assessment, decididas pelo padrão proposto** (o veredito era `valid`, então a correção seguiu):
  - Arquivo byte a byte idêntico **com o mesmo nome** continua no-op (Princípio III).
  - A identidade nome + conteúdo vale nas **duas instâncias** (um só caminho de código; no Inter, o mesmo conteúdo com outro nome também é reprocessado e entregue).
  - "Erro grave" = falhas definitivas atuais (`CPF_NAO_ENCONTRADO`, `DADO_INVALIDO`, arquivo rejeitado); pendências transitórias continuam fora da entrega até concluírem.
- `specs/001-.../spec.md` não foi alterada: FR-026 já fala em "mesmo arquivo"; o detalhamento ficou em FR-009a da spec 002.

## Follow-ups

- **Limitação residual**: dois arquivos de origem com o **mesmo nome-base** (ex.: `X.txt` reutilizado em outro dia) que gerem partes de sucesso idênticas produzem o mesmo nome `X__sucesso-<h8>.txt`; se o Condomínio já consumiu a primeira, a segunda é tratada como o mesmo arquivo. Com nomes por timestamp (padrão atual) não ocorre. Se for preciso cobrir, incluir um prefixo do hash da origem no nome da parte.
- **Upgrade**: o serviço aplica a migração v4 sozinho na primeira partida. Arquivos que estavam **em processamento** na hora da atualização continuam reconhecidos (mesmo nome + conteúdo → identidade antiga).
- **Ambiente local**: o diário `C:\ProgramData\InativacaoUsuarioInter\Inter\estado.db` tem a parte `20261112111333__sucesso-95990873.txt` registrada como já entregue pela versão antiga (a linha nem existe em `entrega`). A correção não a reentrega sozinha; para testar de ponta a ponta, deposite um novo arquivo com os mesmos CPFs.
- Rodar `/speckit-bug-test slug=ja-inativo-sem-replicacao`.
