# Contrato: Arquivos de entrada e estrutura de diretórios

**Consumidores**: empregados/sistema gerador (Inter) e a própria instância Inter (Condomínio).
**Testes de contrato**: `tests/InativacaoUsuarioInter.Tests.Contract/ArquivosEntrada/*`, `EstruturaDiretorios/*`.

## 1. Estrutura de diretórios

```text
{SharePointFolder:FolderPathInter}/          {SharePointFolder:FolderPathCondominio}/
├── Imput/                                   ├── Imput/
├── Output/                                  ├── Processados/
├── Auditoria/log_inativacao.log             ├── Auditoria/log_inativacao.log
└── Falhas/log_inativacao.log  (+ partes)    └── Falhas/log_inativacao.log  (+ partes)
```

- Os nomes são exatos, incluindo a grafia `Imput`. Diretórios ausentes são criados; nada existente é apagado.
- A instância Condomínio não cria nem usa `Output`. A pasta `Files/Condominio/Output`, que existe no ambiente local de testes, é ignorada.

## 2. Elegibilidade

| Condição | Resultado |
| --- | --- |
| Extensão `.json` ou `.txt` (sem diferenciar maiúsculas) | Candidato |
| Qualquer outra extensão (`.tmp`, `.traceparent`, `.part`, …) | Ignorado; não é tocado |
| Última escrita há menos de `Processamento:EstabilizacaoSegundos` | Adiado para o próximo ciclo |
| Não abre com acesso exclusivo | Adiado para o próximo ciclo |
| > `Processamento:TamanhoMaximoBytes` (1 MB) ou > `Processamento:MaximoRegistros` (1.000) | Rejeitado → `Falhas/`, `causa=ARQUIVO_EXCEDE_LIMITE` |
| Nome fora de `yyyyMMddHHmmss` | Aceito (o padrão é só convenção) |

## 3. JSON

- Codificação: UTF-8, com ou sem BOM.
- Raiz: **array** de objetos.
- Propriedades obrigatórias, todas **string**: `cpf`, `id_responsavel`, `nome_responsavel`. Outras propriedades são ignoradas.

```json
[
  { "cpf": "22222222222", "id_responsavel": "1", "nome_responsavel": "Maria Eduarda" },
  { "cpf": "333.333.333-33", "id_responsavel": "2", "nome_responsavel": "Carlos Renato da Silva" }
]
```

## 4. TXT

- Codificação: UTF-8, com ou sem BOM; fim de linha `\n` ou `\r\n`.
- Um registro por linha: `cpf,id_responsavel,nome_responsavel`. **Não há cabeçalho**; uma linha de cabeçalho vira `DADO_INVALIDO`.
- O 1º campo vai até a 1ª vírgula, o 2º até a 2ª vírgula, e **o restante da linha é o nome** (pode conter vírgulas).
- Espaços nas pontas de cada campo são descartados; linhas vazias ou só com espaços são ignoradas.

```text
22222222222,1,Maria Eduarda
333.333.333-33,2,Carlos Renato da Silva, Jr.
```

## 5. Normalização e validação do CPF

1. Remover espaços nas pontas.
2. Remover somente `.`, `-` e espaços internos.
3. O resultado precisa casar com `^[0-9]{11}$`; caso contrário → `DADO_INVALIDO`.
4. Os dígitos verificadores **não** são validados.
5. No JSON, um `cpf` numérico (por exemplo `22222222222`, sem aspas) → `DADO_INVALIDO`.

| Entrada | Resultado |
| --- | --- |
| `"01234567890"` | `01234567890` |
| `"012.345.678-90"` | `01234567890` |
| `" 012 345 678 90 "` | `01234567890` |
| `"0123456789"` (10 dígitos) | `DADO_INVALIDO` |
| `"012/345/678-90"` | `DADO_INVALIDO` |
| `"cpf"` (cabeçalho) | `DADO_INVALIDO` |
| `"1'; DROP TABLE CHMain;--"` | `DADO_INVALIDO` (CA-15) |

## 6. Rejeição do arquivo inteiro (`ARQUIVO_INVALIDO`)

UTF-8 inválido; JSON que não interpreta; raiz que não é array; nenhum registro (arquivo vazio, array vazio ou só linhas vazias). O arquivo é movido para `Falhas/` com o nome original (`<base>__<h8>.<ext>` se houver colisão) e gera uma linha no log de falhas com `cpf=` vazio.

## 7. Arquivos gerados (mesmo formato do original)

Os nomes estão em [research.md § R-10](../research.md). JSON gerado: array indentado com 2 espaços, propriedades na ordem `cpf`, `id_responsavel`, `nome_responsavel`, CPF normalizado. TXT gerado: `cpf,id_responsavel,nome_responsavel` + `\r\n`. Os dois em UTF-8 sem BOM. A parte `__falhas-` preserva o **texto bruto** de cada registro.
