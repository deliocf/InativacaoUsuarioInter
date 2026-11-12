# Bug Verification: Parte de sucesso renomeada e `.traceparent` copiado para o Condomínio

- **Slug**: nome-original-sem-trace
- **Tested**: 2026-11-12
- **Assessment**: ./assessment.md
- **Fix**: ./fix.md
- **Result**: verified

## Summary

O sintoma não se reproduz mais: a parte de sucesso de um arquivo segregado fica com o nome do original em `Processed` e
no `Input` do Condomínio, colisões são mescladas (`Processed`/`Fail`) ou aguardam (Condomínio) sem renomear, e nenhum
`.traceparent` é gravado no Condomínio com rastreamento ativo. A suíte completa passou, exceto por um teste
intermitente de temporização (`CicloWorkerTests`), sem relação com a correção, que passou em todas as repetições.

## Checks Performed

| Check | Command / Action | Result | Notes |
| --- | --- | --- | --- |
| Reproduction (post-fix), automatizada | Testes de integração que executam os passos 1–3 da avaliação pelo ciclo real (`IOrquestradorCiclo`), com sistema de arquivos real e SQL Server em container (Testcontainers): `FluxoInterTests`, `ReplicacaoTests`, `InterCondominioTests`, `FluxoCondominioTests`, `FalhasBancoTests` | pass (41/41) | Arquivo com CPF existente + inexistente → `Processed/20261111103000.json` e entrega com o mesmo nome; ponta a ponta Inter → Condomínio com telemetria ativa → nenhum `*.traceparent` na raiz do Condomínio e trace próprio no Condomínio |
| Cenários de colisão | Mesmos testes: 2ª parte do mesmo original (pendência reprocessada), `Fail` com mesmo nome (TXT e JSON rejeitado), nome ocupado no Condomínio | pass | `Processed`/`Fail` mesclados sem novo nome; entrega espera em `Output` e sai com o nome original após o Condomínio consumir o anterior |
| New / updated tests — unit | `dotnet test --project tests/…Tests.Unit -- --filter-class "*MescladorHistoricoTests" --filter-class "*PartesTests"` | pass (12/12) | mescla TXT/JSON/bloco, idempotência, `GravarAsync`/`MoverAsync` |
| New / updated tests — contract | `dotnet test --project tests/…Tests.Contract -- --filter-class "*ReplicacaoContratoTests"` | pass (5/5) | sem sidecar; colisão aguarda e entrega com nome original |
| Regression suite | `dotnet test src/InativacaoUsuarioInter.Service.slnx` | pass* | 277 testes: 275 ok, 1 ignorado (opt-in `LocalDbOptInTests`), 1 falha intermitente — ver abaixo. Integração 100 % e Contract 100 % |
| Falha intermitente | `CicloWorkerTests` repetido 5× isolado + suíte Unit completa | pass (5/5; 127/127) | `Falha_em_um_ciclo_nao_encerra_o_laco` usa `FakeTimeProvider` + espera de timer; falhou só sob a carga da suíte completa. Arquivos não alterados desde o commit inicial (`4486f1b`) |
| Lint / type-check | `dotnet build src/InativacaoUsuarioInter.Service.slnx --no-incremental` (TreatWarningsAsErrors + analisadores `latest`) | pass | 0 erros, 0 avisos |
| Formatação | `dotnet format … --verify-no-changes --include <arquivos alterados>` | pass | exit 0 |
| Reprodução no serviço Windows real contra o WAccess local | — | skipped | Destrutivo: inativaria usuários reais no banco configurado em `appsettings`; não executado sem consentimento |

## Output Excerpts

```text
# Integração direcionada (reprodução)
  total: 41
  failed: 0
  succeeded: 41

# Suíte completa
failed InativacaoUsuarioInter.Tests.Unit.Ciclo.CicloWorkerTests.Falha_em_um_ciclo_nao_encerra_o_laco (10s 377ms)
  Ciclo 2 não ocorreu.
…Tests.Contract.dll passed (33s 053ms)
…Tests.Integration.dll passed (3m 36s 737ms)
  total: 277  failed: 1  succeeded: 275  skipped: 1

# CicloWorkerTests repetido
  failed: 0   succeeded: 3   (×5)
# Unit completo
  total: 127  failed: 0  succeeded: 127
```

## Residual Risks

- A reprodução foi automatizada (mesmo ciclo do serviço, pastas e SQL Server reais em container), não no serviço
  Windows instalado contra `SharePointFolder` e o WAccess reais; comportamento de sincronização do SharePoint/OneDrive
  (ex.: arquivos substituídos por rename) não foi exercitado.
- Limitações registradas no `fix.md`: cópia sumida de `Output` com histórico já mesclado não pode ser restaurada;
  entrega com nome ocupado espera indefinidamente se o Condomínio parar (coberto pelo alerta de 24 h); `MigradorLegado`
  ainda usa nome único em colisão.
- A emenda 2.0.0 da constituição (Princípio IV) ainda depende de aprovação por PR.
- `CicloWorkerTests.Falha_em_um_ciclo_nao_encerra_o_laco` é intermitente sob carga (pré-existente, fora do escopo).

## Recommendation

Fechar o bug — verificado pela reprodução automatizada de ponta a ponta e pela suíte de regressão. Antes de publicar,
recomenda-se um teste manual no ambiente (como feito no bug anterior) cobrindo um arquivo segregado e a pasta do
Condomínio, aprovar a emenda da constituição e, separadamente, estabilizar o teste intermitente do `CicloWorker`.
