# Contrato: Logs de Auditoria e Falhas

**Origem**: FR-019, FR-020, FR-020a; Clarification Q4 (2026-11-11).
**Teste de contrato**: `tests/InativacaoUsuarioInter.Tests.Contract/Logs/*`, com golden files.

## Arquivos

| Log | Caminho |
| --- | --- |
| Auditoria | `<raiz>/Auditoria/log_inativacao.log` |
| Falhas | `<raiz>/Falhas/log_inativacao.log` |

- Codificação UTF-8 sem BOM. Uma entrada por linha, terminada em `\r\n`. O arquivo só recebe acréscimos; o serviço nunca reescreve nem trunca.
- Nenhum outro conteúdo (cabeçalho, comentário, log técnico) é gravado nesses arquivos.

## Linha de Auditoria

```text
<dataHora> | <ambiente> | arquivo=<nome> | cpf=<cpf> | id_responsavel=<id> | nome_responsavel=<nome> | status=<status>
```

| Campo | Formato |
| --- | --- |
| `dataHora` | `yyyy-MM-ddTHH:mm:sszzz` no horário local da instalação (ex.: `2026-11-11T10:30:00-03:00`); é o momento da confirmação no WAccess |
| `ambiente` | `Inter` \| `Condominio` |
| `arquivo` | Nome do arquivo de entrada que continha o registro (o nome, não o caminho) |
| `cpf` | 11 dígitos normalizados |
| `id_responsavel`, `nome_responsavel` | Valor sem espaços nas pontas |
| `status` | `INATIVADO` \| `JA_INATIVO` |

Exemplo:

```text
2026-11-11T10:30:00-03:00 | Inter | arquivo=20261111102958.json | cpf=22222222222 | id_responsavel=1 | nome_responsavel=Maria Eduarda | status=INATIVADO
```

## Linha de Falhas

```text
<dataHora> | <ambiente> | arquivo=<nome> | cpf=<cpf ou vazio> | causa=<causa>
```

| `causa` | Quando |
| --- | --- |
| `CPF_NAO_ENCONTRADO` | Nenhum cadastro com `IdNumber = cpf` |
| `DADO_INVALIDO` | Registro rejeitado na validação (`cpf=` traz o valor normalizado se for válido; senão vazio) |
| `ARQUIVO_INVALIDO` | Arquivo inteiro rejeitado (`cpf=` vazio) |
| `ARQUIVO_EXCEDE_LIMITE` | Arquivo acima de 1.000 registros ou 1 MB (`cpf=` vazio) |

Exemplos:

```text
2026-11-11T10:30:01-03:00 | Inter | arquivo=20261111102958.json | cpf=44444444444 | causa=CPF_NAO_ENCONTRADO
2026-11-11T10:30:01-03:00 | Inter | arquivo=20261111103500.txt | cpf= | causa=ARQUIVO_INVALIDO
```

## Saneamento (FR-020a)

Em qualquer valor, `\r`, `\n` e `|` são substituídos por um espaço **antes** da formatação. Assim cada entrada ocupa uma única linha e o conteúdo vindo dos arquivos não consegue criar campos falsos.

## Garantias

- **Exatamente uma** linha de Auditoria por inativação confirmada e **exatamente uma** linha de Falhas por falha definitiva, mesmo com crash ou reinício (R-08).
- Falhas transitórias **não** geram linha em nenhum desses logs; ficam no log técnico e na telemetria.
- Nenhuma credencial aparece nesses logs.
