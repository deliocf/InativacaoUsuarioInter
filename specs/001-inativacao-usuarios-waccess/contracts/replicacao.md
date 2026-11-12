# Contrato: Artefato de replicação Inter → Condomínio

> **Substituído pela feature 002** — ver [`specs/002-ajustes-pastas-fluxo/contracts/replicacao.md`](../../002-ajustes-pastas-fluxo/contracts/replicacao.md): fila `Output`, histórico em `Processed`, entrega em `{FolderPathCondominio}/Input` no mesmo ciclo.

**Origem**: FR-032–FR-036; [research.md R-11](../research.md).
**Teste de contrato**: `tests/InativacaoUsuarioInter.Tests.Contract/Replicacao/*`.

## O que é entregue

Uma cópia **byte a byte** de cada arquivo de `{FolderPathInter}/Output` ainda não entregue (o original inteiro ou a parte `__sucesso-`), colocada em `{FolderPathCondominio}/Imput/`. O formato é o de [arquivos-entrada.md](./arquivos-entrada.md), então o Condomínio processa o arquivo como qualquer outra entrada.

## Protocolo de escrita (visibilidade atômica)

1. Remover `<nome>.tmp` órfão de tentativa anterior, se houver.
2. Copiar para `{FolderPathCondominio}/Imput/<nome>.tmp`.
3. Ler o `.tmp` de volta e comparar o SHA-256 com o da origem. Se diferir, a entrega falha, o `.tmp` é removido e a entrega fica para o próximo ciclo.
4. Gravar `{FolderPathCondominio}/Imput/<nome>.traceparent` (opcional; uma linha no formato W3C `00-<trace-id>-<span-id>-<flags>`).
5. Renomear `<nome>.tmp` para `<nome>`. É **a partir deste ponto** que o arquivo fica elegível no Condomínio.
6. Registrar `Entregue` no diário do Inter.

## Regras

| Situação | Comportamento |
| --- | --- |
| `<nome>` já existe no destino com o mesmo SHA-256 | Registra `Entregue` (no-op) |
| `<nome>` já existe com conteúdo diferente | Entrega como `<base>__<h8>.<ext>` |
| Destino indisponível ou sem permissão | Transitório: origem intacta, nova tentativa no próximo ciclo, alerta de saúde |
| Arquivo já `Entregue` | Não é copiado de novo (inclusive depois de reiniciar) |
| Momento | Só depois de processados os arquivos **novos** do ciclo; as partes `__pendente-` não bloqueiam (CA-09) |

## Lado Condomínio

- Ignora `.tmp` e `.traceparent` na elegibilidade.
- Se `<nome>.traceparent` existir, usa o valor como *parent* do span `arquivo` e o move junto com o arquivo para o destino final (`Processados/` ou `Falhas/`).
- Um arquivo com hash já processado é no-op (proteção extra contra reentrega).
- **Não** replica para nenhum outro destino (FR-036).

## O que nunca é entregue

Partes `__pendente-` e `__falhas-`, arquivos rejeitados, qualquer arquivo fora de `Output` e **qualquer arquivo em `Output` cujo hash o diário do Inter não reconheça como parte `Integral`/`Sucesso`** (por exemplo, copiado à mão). Esses geram o alerta `ARQUIVO_OUTPUT_DESCONHECIDO` (FR-035).
