# Quickstart: validar os ajustes de pastas, fluxo e `CHDownloadRequired`

**Feature**: [spec.md](./spec.md) · Contratos: [estrutura-pastas](./contracts/estrutura-pastas.md), [replicacao](./contracts/replicacao.md), [waccess-inativacao](./contracts/waccess-inativacao.md)

## Pré-requisitos

Os mesmos da feature 001 (`specs/001-inativacao-usuarios-waccess/quickstart.md`): .NET SDK 10, Docker para as suítes de contrato/integração e, para o cenário real, o `W_ACCESS` local em `(localdb)\MSSQLLocalDB`.

## 1. Suítes automatizadas

```powershell
dotnet test tests/InativacaoUsuarioInter.Tests.Unit
dotnet test tests/InativacaoUsuarioInter.Tests.Contract        # requer Docker
dotnet test tests/InativacaoUsuarioInter.Tests.Integration     # requer Docker
$env:WACCESS_TESTE_LOCALDB = "1"; dotnet test tests/InativacaoUsuarioInter.Tests.Integration   # inclui LocalDbOptIn contra o W_ACCESS local
```

Mapeamento cenário → suíte esperada:

| Spec | Validação |
| --- | --- |
| US1 / FR-001–FR-003, SC-001, SC-002 | Contract › `EstruturaDiretorios` (pastas criadas; nenhum literal antigo fora de `NomesPastasLegado`) |
| US1 cenários 4–5 / FR-004, SC-002a | Integration › `Migracao` (migração completa; crash em cada passo + retomada; contagem de arquivos e linhas preservada) |
| US2 / FR-006–FR-011, SC-003–SC-006 | Integration › `FluxoInter`, `Replicacao`, `InjecaoFalhas` (destino indisponível, reinício entre "Entregue" e remoção de `Output`) |
| FR-004a | Integration › `Replicacao` (Output herdado com entrega `Entregue` → `Processed`, sem reentrega) |
| US3 / FR-012–FR-013 | Integration › `FluxoCondominio` (falhas em `{FolderPathCondominio}/Fail`; nenhum acesso a `FolderPathInter`) |
| US4 / FR-014–FR-015, SC-007 | Contract › `WAccess` (texto SQL) e Integration › `LocalDb` (antes/depois: só `CHState` e `CHDownloadRequired` mudam; caso `CHState = 1` + `NULL`/`0` → `JA_INATIVO` com marca gravada) |

## 2. Cenário manual com o arquivo de teste (SC-008)

Estado atual da máquina de desenvolvimento: `Files/Inter/Input/20261112111111.txt` (arquivo de teste) e `Files/Condominio/Imput/20261112111111.txt` + `.traceparent` (legado).

1. Instância Inter (`Settings:Instancia = Inter` no `appsettings.json`):

   ```powershell
   dotnet run --project src/InativacaoUsuarioInter.Service
   ```

2. Depois do primeiro ciclo, conferir:
   - `Files/Inter/Input` sem o arquivo de teste;
   - `Files/Inter/Processed/20261112111111.txt` presente (ou a parte `__sucesso-`, se houver falhas);
   - `Files/Inter/Output` vazio;
   - `Files/Condominio/Input/20261112111111.txt` + `.traceparent` presentes (se o legado `Imput` tinha um arquivo de mesmo nome e conteúdo diferente, a entrega usa o nome de colisão `__<h8>`);
   - `Files/Inter/Logs/log_inativacao.log` com uma linha por CPF; registros com falha em `Files/Inter/Fail/20261112111111.txt` e em `Files/Inter/Fail/log_inativacao.log`.
3. No banco local, para os CPFs do arquivo (consulta agregada, sem expor CPFs em logs):

   ```sql
   SELECT CHState, CHDownloadRequired, COUNT(*) FROM dbo.CHMain
   WHERE IdNumber IN (<CPFs do arquivo>) GROUP BY CHState, CHDownloadRequired;
   ```

   Esperado: somente `CHState = 1, CHDownloadRequired = 1`.
4. Instância Condomínio (`Settings:Instancia = Condominio`), mesmo comando. Esperado:
   - `Files/Condominio/Imput` migrado para `Input` e removido (se vazio);
   - arquivos de `Input` processados e movidos para `Files/Condominio/Processed`;
   - falhas em `Files/Condominio/Fail`; auditoria em `Files/Condominio/Logs`.
5. Entrega com destino indisponível: renomear temporariamente `Files/Condominio` (ou negar escrita), depositar novo arquivo em `Files/Inter/Input`, rodar um ciclo → arquivo em `Files/Inter/Output` e `Processed`; restaurar o destino → no ciclo seguinte o arquivo chega a `Files/Condominio/Input` e `Output` fica vazio.

## 3. Implantação

- **Ordem obrigatória: atualizar o Condomínio primeiro (ou na mesma janela), depois o Inter.**
  - Condomínio novo + Inter antigo: funciona — o Inter antigo entrega em `Imput` e a migração de cada ciclo do Condomínio move esses arquivos para `Input`.
  - Inter novo + Condomínio antigo: **não funciona** — o Inter entrega em `Condominio/Input`, que o Condomínio antigo não lê; as entregas ficam paradas até o Condomínio ser atualizado.
- Nenhuma mudança de configuração é necessária: as chaves de `appsettings.json` não mudam.
- O diário SQLite é migrado automaticamente para v3 na partida.
