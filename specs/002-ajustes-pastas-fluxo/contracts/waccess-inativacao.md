# Contrato: Inativação no WAccess (v2)

**Origem**: FR-014, FR-015; [research.md R-01, R-02](../research.md).
**Altera**: `specs/001-inativacao-usuarios-waccess/contracts/waccess-inativacao.md` (somente o passo B e a regra de execução).
**Teste de contrato**: `tests/InativacaoUsuarioInter.Tests.Contract/WAccess/*` (texto SQL e parâmetros) e `tests/InativacaoUsuarioInter.Tests.Integration/LocalDb/*` (efeito real, opt-in).

## Passo A — pré-leitura (inalterado)

```sql
SELECT COUNT(*) AS Existentes,
       SUM(CASE WHEN CHState <> 1 THEN 1 ELSE 0 END) AS Ativos
FROM dbo.CHMain WHERE IdNumber = @Cpf;
```

`Existentes = 0` → `Inexistente`; `Ativos = 0` → `Inativo`; senão `Ativo`.

## Passo B — alteração

Executado quando o estado anterior gravado é `Ativo` **ou** `Inativo` (antes: só `Ativo`). Nunca para `Inexistente`.

```sql
DECLARE @afetados TABLE (CHID int, EstadoAnterior smallint);
UPDATE dbo.CHMain SET CHState = 1, CHDownloadRequired = 1
  OUTPUT inserted.CHID, deleted.CHState INTO @afetados
  WHERE IdNumber = @Cpf
    AND (CHState <> 1 OR CHDownloadRequired IS NULL OR CHDownloadRequired = 0);
SELECT (SELECT COUNT(*) FROM @afetados WHERE EstadoAnterior <> 1)   AS Inativados,
       (SELECT COUNT(*) FROM @afetados WHERE EstadoAnterior = 1)    AS SomenteDownload,
       (SELECT COUNT(*) FROM dbo.CHMain WHERE IdNumber = @Cpf)      AS Existentes;
```

- `@Cpf`: `VarChar(50)`, sempre parâmetro (001/FR-015).
- Transação `ReadCommitted`; `OUTPUT … INTO` por causa do trigger em `CHMain` (001/R-03).
- Nenhuma outra coluna é alterada.

## Classificação (usa somente o estado anterior gravado)

| Estado anterior | Passo B | Resultado / status de auditoria |
| --- | --- | --- |
| `Inexistente` | não executa | `CPF_NAO_ENCONTRADO` |
| `Ativo` | `Existentes ≥ 1` | `INATIVADO` |
| `Ativo` | `Existentes = 0` | `CPF_NAO_ENCONTRADO` |
| `Inativo` | executa (pode marcar download) | `JA_INATIVO` |

## Telemetria

Span `waccess.inativar` com `waccess.linhas_afetadas` (= `Inativados`) e **novo** `waccess.linhas_marcadas_download` (= `SomenteDownload`). Sem CPF em claro.

## Idempotência

Reexecutar o passo B após sucesso afeta 0 linhas (todas já com `CHState = 1` e `CHDownloadRequired = 1`); o resultado é o mesmo porque a classificação vem do diário.
