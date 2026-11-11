# Contrato: Inativação no WAccess (`dbo.CHMain`)

**Origem**: FR-014–FR-018; documento-base §7; [research.md R-01–R-03](../research.md).
**Teste de contrato**: `tests/InativacaoUsuarioInter.Tests.Contract/WAccess/*`, contra SQL Server em container com a réplica de `CHMain` descrita abaixo.

## Comandos (parametrizados)

**Passo A — pré-leitura** (`IRepositorioWAccess.LerEstadoAsync`). O resultado é gravado no diário como `estado_anterior` **antes** do passo B:

```sql
SELECT COUNT(*) AS Existentes,
       SUM(CASE WHEN CHState <> 1 THEN 1 ELSE 0 END) AS Ativos
FROM dbo.CHMain WHERE IdNumber = @Cpf;
```

`Existentes = 0` → `Inexistente`; `Ativos = 0` → `Inativo`; `Ativos > 0` → `Ativo`.

**Passo B — alteração** (`IRepositorioWAccess.InativarAsync`), só quando `estado_anterior = Ativo`, em transação `READ COMMITTED`:

```sql
DECLARE @afetados TABLE (CHID int);
UPDATE dbo.CHMain SET CHState = 1
  OUTPUT inserted.CHID INTO @afetados
  WHERE IdNumber = @Cpf AND CHState <> 1;
SELECT (SELECT COUNT(*) FROM @afetados)                      AS Inativados,
       (SELECT COUNT(*) FROM dbo.CHMain WHERE IdNumber = @Cpf) AS Existentes;
```

| Parâmetro | Tipo | Valor |
| --- | --- | --- |
| `@Cpf` | `VarChar(50)` | CPF normalizado (11 dígitos) |

## Classificação do resultado

| `estado_anterior` | Passo B | Resultado |
| --- | --- | --- |
| `Inexistente` | não executado | `CPF_NAO_ENCONTRADO` |
| `Inativo` | não executado | `JA_INATIVO` |
| `Ativo` | `Existentes ≥ 1` (qualquer `Inativados`, inclusive 0 em retry/recuperação) | `INATIVADO` (se `Inativados > 1`: aviso `CpfComMultiplosCadastros`, R-02) |
| `Ativo` | `Existentes = 0` | `CPF_NAO_ENCONTRADO` |

A mesma tabela vale para a execução normal, para um retry depois de timeout e para a recuperação após crash (research R-03/R-08).

## Invariantes verificados por teste

1. Somente `CHState` é escrito pelo comando; um snapshot de todas as colunas das linhas-alvo e não-alvo, antes e depois, confirma isso (SC-002). As mudanças feitas por triggers do WAccess ficam de fora do contrato do serviço.
2. Nenhuma linha com `IdNumber` diferente de `@Cpf` é alterada.
3. Os zeros à esquerda são preservados (`01234567890` ≠ `1234567890`).
4. Uma entrada maliciosa nunca chega ao comando: ela é barrada antes, na validação (`^[0-9]{11}$`). Mesmo assim, o teste envia o valor diretamente como parâmetro e verifica que nada é executado.
5. A contagem fica correta com um trigger que **não** usa `SET NOCOUNT ON` (a réplica de teste inclui esse trigger).
6. Executar duas vezes produz o mesmo estado final (Princípio III): a 1ª sequência A+B retorna `INATIVADO` e a 2ª pré-leitura retorna `Inativo` → `JA_INATIVO`, sem alteração adicional.
7. Reexecutar o passo B com `estado_anterior = Ativo` depois de o UPDATE já ter sido aplicado (`Inativados = 0`) classifica como `INATIVADO`, não `JA_INATIVO`. Simula o retry após timeout com o banco já tendo confirmado a alteração.
8. Um usuário que já estava inativo (`estado_anterior = Inativo`) nunca recebe UPDATE e é sempre classificado `JA_INATIVO`.

## Réplica mínima para testes (container)

```sql
CREATE TABLE dbo.CHMain (
  CHID int IDENTITY PRIMARY KEY,
  CHType smallint NOT NULL DEFAULT 2,
  IdNumber varchar(50) NULL,
  CHState smallint NOT NULL DEFAULT 0,
  LastModifDateTime datetime NULL
);
CREATE INDEX IX_CHMain_CHType_IdNumber ON dbo.CHMain (CHType, IdNumber);
CREATE TABLE dbo.CHMainHistoricoTeste (CHID int, CHState smallint, Em datetime2 DEFAULT sysdatetime());
-- Trigger que imita wtr_CHMainChangeCHState e infla @@ROWCOUNT (sem SET NOCOUNT ON)
CREATE TRIGGER dbo.wtr_Teste_ChangeCHState ON dbo.CHMain AFTER UPDATE AS
  INSERT dbo.CHMainHistoricoTeste (CHID, CHState) SELECT CHID, CHState FROM inserted;
```

Se o esquema real mudar (tipo de `IdNumber`/`CHState`), a réplica e este contrato precisam ser atualizados juntos (Princípio V).
