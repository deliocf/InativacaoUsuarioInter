# Contrato: Entrega Inter → Condomínio (v2)

**Origem**: FR-006–FR-011; [research.md R-03, R-04, R-07](../research.md).
**Substitui**: `specs/001-inativacao-usuarios-waccess/contracts/replicacao.md`.
**Teste de contrato**: `tests/InativacaoUsuarioInter.Tests.Contract/Replicacao/*`.

## O que é entregue

Uma cópia **byte a byte** de cada arquivo da fila de saída `{FolderPathInter}/Output` ainda não entregue (o original inteiro ou a parte `__sucesso-`), colocada em `{FolderPathCondominio}/Input/`. O formato é o de `specs/001-inativacao-usuarios-waccess/contracts/arquivos-entrada.md`, então o Condomínio processa o arquivo como qualquer outra entrada.

## Quando

- Ao concluir um arquivo, o Inter grava o conteúdo de sucesso em `Processed` **e** em `Output` e registra a entrega pendente (mesma transação do registro da parte).
- No **mesmo ciclo**, depois de processar todos os arquivos de `Input`, cada entrega pendente é tentada. A condição antiga "só quando não houver arquivos novos na entrada" deixa de existir.
- Entregas que falharem ou não couberem no orçamento do ciclo permanecem em `Output` e são tentadas em todo ciclo seguinte, sem limite de ciclos.

## Protocolo de escrita (inalterado, exceto a pasta)

0. Criar `{FolderPathCondominio}/Input` se ausente (FR-002a). Falha aqui é transitória da entrega: o arquivo permanece em `Output`.
1. Remover `<nome>.tmp` órfão de tentativa anterior, se houver.
2. Copiar para `{FolderPathCondominio}/Input/<nome>.tmp`.
3. Ler o `.tmp` de volta e comparar o SHA-256 com o da origem. Se diferir, a entrega falha, o `.tmp` é removido e a entrega fica para o próximo ciclo.
4. Gravar `{FolderPathCondominio}/Input/<nome>.traceparent` (opcional; W3C `00-<trace-id>-<span-id>-<flags>`).
5. Renomear `<nome>.tmp` para `<nome>`. A partir deste ponto o arquivo fica elegível no Condomínio.
6. Registrar `Entregue` no diário do Inter (durável).
7. **Novo**: remover `Output/<nome>` (e seu `.traceparent`, se houver). Se `Processed` não tiver arquivo com o mesmo hash (arquivo herdado da versão anterior), mover para `Processed` em vez de remover.

## Regras

| Situação | Comportamento |
| --- | --- |
| `<nome>` já existe no destino com o mesmo SHA-256 | Registra `Entregue` (no-op) e segue para o passo 7 |
| `<nome>` já existe com conteúdo diferente | Entrega como `<base>__<h8>.<ext>` |
| Destino indisponível ou sem permissão | Transitório: arquivo permanece em `Output`, `tentativas++`, nova tentativa no próximo ciclo |
| Reinício entre os passos 6 e 7 | Na reconciliação seguinte, o arquivo em `Output` com entrega `Entregue` é removido/movido (passo 7) sem nova cópia |
| Arquivo em `Output` sem registro no diário | Não é entregue nem removido; alerta `ARQUIVO_OUTPUT_DESCONHECIDO` |
| Entrega pendente há mais de `AlertaPendenciaHoras` | Alerta de saúde `ENTREGA_PENDENTE_ACIMA_LIMITE`; cessa quando não houver pendência acima do limite |

## Lado Condomínio

- Lê somente `{FolderPathCondominio}/Input`; ignora `.tmp` e `.traceparent` na elegibilidade.
- Usa `<nome>.traceparent` como *parent* do span `arquivo` e o move junto com o arquivo para `Processed/` ou `Fail/`.
- Arquivo com hash já processado é no-op.
- Não grava em `Output` nem entrega a outro ambiente; não acessa `FolderPathInter`.

## O que nunca é entregue

Partes `__pendente-`, arquivos de falhas e rejeitados, qualquer arquivo fora de `Output` e qualquer arquivo em `Output` cujo hash o diário do Inter não reconheça como parte `Integral`/`Sucesso`.
