# Data Model: Inativação e Replicação de Usuários WAccess

**Feature**: [spec.md](./spec.md) | **Research**: [research.md](./research.md)

Há três grupos de dados: (1) **domínio em memória**, derivado dos arquivos de entrada; (2) **estado durável** no SQLite local do módulo `Estado` (R-07); (3) **dado externo**, `CHMain` no WAccess, do qual o serviço só lê e altera `CHState`.

---

## 1. Domínio (em memória)

### Instancia (enum)

| Valor | Pasta raiz | Destino de concluídos | Replica? |
| --- | --- | --- | --- |
| `Inter` | `SharePointFolder:FolderPathInter` | `Output` | Sim, para `FolderPathCondominio/Imput` |
| `Condominio` | `SharePointFolder:FolderPathCondominio` | `Processados` | Não |

### ArquivoEntrada

| Campo | Tipo | Regra |
| --- | --- | --- |
| `Caminho` | string | Arquivo em `<raiz>/Imput` |
| `Nome` | string | Nome com extensão |
| `Formato` | `Json` \| `Txt` | Pela extensão, sem diferenciar maiúsculas; outras extensões são ignoradas (não entram no modelo) |
| `Hash` | string (64 hex) | SHA-256 dos bytes |
| `HashRaiz` | string (64 hex) | `= Hash` para um arquivo original; para a parte `__pendente-`, o hash do original. **Resolvido no diário (`parte_gerada.hash` → `hash_origem` → `arquivo.hash_raiz`) antes de validar os registros e calcular as chaves** (research R-08) |
| `BaseRaiz` | string | Nome-base do original, usado para nomear as partes (R-10) |
| `TamanhoBytes` | long | ≤ `Processamento:TamanhoMaximoBytes` (padrão 1.048.576) |
| `Registros` | lista de `RegistroEntrada` | ≤ `Processamento:MaximoRegistros` (padrão 1.000) |

**Validação do arquivo** (FR-012). Qualquer falha aqui faz o arquivo inteiro ser rejeitado como definitivo, sem nenhuma inativação:
- UTF-8 válido (BOM opcional); bytes inválidos → `ARQUIVO_INVALIDO`.
- JSON: precisa interpretar e ter um array na raiz; caso contrário → `ARQUIVO_INVALIDO`.
- Nenhum registro após ignorar linhas vazias / array vazio → `ARQUIVO_INVALIDO`.
- Tamanho ou quantidade acima do limite → `ARQUIVO_EXCEDE_LIMITE`.

### RegistroEntrada

| Campo | Tipo | Regra |
| --- | --- | --- |
| `Indice` | int | Posição no arquivo, a partir de 0 (linha não vazia ou elemento do array) |
| `TextoBruto` | string | Linha TXT original ou elemento JSON serializado como veio; usado na parte de falhas |
| `CpfOriginal` | string? | Como veio |
| `Cpf` | string? | Normalizado: remove `.`, `-` e espaços, e exige `^[0-9]{11}$` (FR-011) |
| `IdResponsavel` | string? | Sem espaços nas pontas; obrigatório, não vazio |
| `NomeResponsavel` | string? | Sem espaços nas pontas; obrigatório, não vazio; no TXT, tudo o que vem depois da 2ª vírgula |
| `ChaveIdempotencia` | string (64 hex) | SHA-256 de `instancia`, `hashRaiz` e `cpf` concatenados com o separador barra vertical (`instancia\|hashRaiz\|cpf`), usando o `HashRaiz` já resolvido; só existe para registro válido |
| `Validacao` | `Valido` \| `DadoInvalido` \| `Duplicado` | Ver as regras abaixo |

**Regras do registro**:
- JSON: `cpf`, `id_responsavel` e `nome_responsavel` precisam ser **strings**. Um número, nulo ou propriedade ausente → `DadoInvalido`. Propriedades extras são ignoradas.
- TXT: menos de 3 campos (menos de 2 vírgulas) → `DadoInvalido`. Uma linha de cabeçalho cai em `DadoInvalido` pela regra do CPF.
- O mesmo `Cpf` válido repetido no arquivo: a 1ª ocorrência é `Valido` e as seguintes são `Duplicado` (descartadas com log técnico; não vão para nenhuma parte).

### ResultadoRegistro (enum)

| Valor | Classe | Auditoria (`status=`) | Falhas (`causa=`) | Parte de destino |
| --- | --- | --- | --- | --- |
| `Inativado` | Sucesso | `INATIVADO` | — | concluídos |
| `JaInativo` | Sucesso | `JA_INATIVO` | — | concluídos |
| `CpfNaoEncontrado` | Definitiva | — | `CPF_NAO_ENCONTRADO` | `Falhas/` |
| `DadoInvalido` | Definitiva | — | `DADO_INVALIDO` | `Falhas/` |
| `PendenteTransitorio` | Transitória | — | — (só log técnico) | `Imput/` (`__pendente-`) |

---

## 2. Estado durável (SQLite, módulo `Estado`)

Arquivo: `<Processamento:DiretorioEstado>/estado.db` (WAL, `synchronous=FULL`). As migrações são versionadas pela tabela `versao_esquema`.

### Tabela `arquivo`

| Coluna | Tipo | Notas |
| --- | --- | --- |
| `hash` | TEXT PK | SHA-256 do conteúdo |
| `hash_raiz` | TEXT NOT NULL | Igual a `hash` no original |
| `instancia` | TEXT NOT NULL | `Inter`/`Condominio` |
| `nome_original` | TEXT NOT NULL | Nome no momento da detecção |
| `base_raiz` | TEXT NOT NULL | Para nomear as partes |
| `formato` | TEXT NOT NULL | `Json`/`Txt` |
| `estado` | TEXT NOT NULL | Ver a máquina de estados |
| `causa_rejeicao` | TEXT NULL | `ARQUIVO_INVALIDO`/`ARQUIVO_EXCEDE_LIMITE` |
| `linha_log` | TEXT NULL | Linha de Falhas **pré-formatada** de um arquivo rejeitado (carimbo de horário fixado), para gravá-la exatamente uma vez (FR-021) |
| `log_gravado` | INTEGER NOT NULL DEFAULT 0 | 1 depois de confirmada a linha de rejeição no log de Falhas |
| `detectado_em` | TEXT NOT NULL | ISO 8601 com offset |
| `concluido_em` | TEXT NULL | |
| `traceparent` | TEXT NULL | Recebido do Inter (somente no Condomínio) |

### Tabela `registro`

| Coluna | Tipo | Notas |
| --- | --- | --- |
| `chave_idempotencia` | TEXT PK | Ver `RegistroEntrada` |
| `hash_arquivo` | TEXT NOT NULL FK → `arquivo.hash` | Arquivo em que o registro foi visto por último |
| `hash_raiz` | TEXT NOT NULL | |
| `indice` | INTEGER NOT NULL | |
| `estado` | TEXT NOT NULL | `Pendente`, `EmExecucao`, `Inativado`, `JaInativo`, `CpfNaoEncontrado`, `DadoInvalido` |
| `estado_anterior` | TEXT NULL | `Ativo` \| `Inativo` \| `Inexistente`; resultado da pré-leitura (research R-03, passo A), gravado **na mesma transação** que `EmExecucao`; obrigatório sempre que `estado = EmExecucao` |
| `tentativas` | INTEGER NOT NULL DEFAULT 0 | Acumulado entre ciclos |
| `pendente_desde` | TEXT NULL | 1ª falha transitória; base do alerta de 24 h (FR-042) |
| `linha_log` | TEXT NULL | Linha de Auditoria ou Falhas **pré-formatada** (carimbo de horário fixado) |
| `log_gravado` | INTEGER NOT NULL DEFAULT 0 | 1 depois de confirmada a linha no arquivo de log |
| `linhas_afetadas` | INTEGER NULL | Telemetria (R-02) |
| `atualizado_em` | TEXT NOT NULL | |

> Registros `DadoInvalido` sem CPF válido usam a chave `SHA-256(instancia|hashRaiz|"#"+indice)`.
> `linha_log` contém CPF e nome em claro. O arquivo `estado.db` fica no diretório de estado, com ACL restrita à conta do serviço (o mesmo nível de proteção dos logs de Auditoria/Falhas, FR-038).

### Tabela `parte_gerada`

| Coluna | Tipo | Notas |
| --- | --- | --- |
| `hash` | TEXT PK | Hash do conteúdo da parte |
| `hash_origem` | TEXT NOT NULL FK → `arquivo.hash` | |
| `tipo` | TEXT NOT NULL | `Integral`, `Sucesso`, `Falhas`, `Pendente`, `Rejeitado` |
| `caminho_final` | TEXT NOT NULL | Destino depois do rename atômico |
| `gravado_em` | TEXT NOT NULL | |

### Tabela `output_conhecido` (somente Inter, migração v2 — T098)

| Coluna | Tipo | Notas |
| --- | --- | --- |
| `nome` | TEXT PK | Nome do arquivo em `Output` |
| `tamanho` | INTEGER NOT NULL | Bytes |
| `escrito_em` | TEXT NOT NULL | Data de escrita (UTC) |
| `hash` | TEXT NOT NULL | SHA-256 já calculado; reutilizado enquanto nome, tamanho e data não mudarem, para que a reconciliação da replicação não recalcule o hash de todo o histórico de `Output` a cada ciclo |

### Tabela `entrega` (somente Inter)

| Coluna | Tipo | Notas |
| --- | --- | --- |
| `hash` | TEXT PK | Hash do arquivo em `Output` |
| `nome_output` | TEXT NOT NULL | |
| `nome_destino` | TEXT NULL | Nome final em `Condominio/Imput` |
| `estado` | TEXT NOT NULL | `NaoEntregue`, `Entregue` |
| `tentativas` | INTEGER NOT NULL DEFAULT 0 | |
| `ultima_falha` | TEXT NULL | Categoria do erro, sem caminho completo nem credencial |
| `entregue_em` | TEXT NULL | |
| `traceparent` | TEXT NULL | Enviado no sidecar |

---

## 3. Máquinas de estado

### Arquivo

```text
                ┌──────────── validação falhou ─────────────► Rejeitado (→ Falhas/)
Detectado ──────┤
                └─► EmProcessamento ──┬─ sem pendências ─────► Concluido
                                      └─ há pendências ──────► Segregado (parte __pendente- volta a Imput como novo `arquivo` com mesmo hash_raiz)
```

- `Concluido`: a parte `Integral` ou `Sucesso` foi gravada no destino de concluídos e a parte `Falhas` (se houver) foi gravada em `Falhas/`; o original saiu da entrada.
- `Segregado`: além das partes acima, a parte `Pendente` foi gravada em `Imput/`; o original saiu da entrada.
- Um arquivo que reaparece com um `hash` já `Concluido`/`Segregado`/`Rejeitado` é tratado como no-op (R-08).

### Registro

```text
Pendente ──(pré-leitura)──► EmExecucao[estado_anterior] ──┬─► Inativado ─┐
   ▲                                                      ├─► JaInativo ─┼─► (log_gravado = 1)
   │                                                      ├─► CpfNaoEncontrado ┘
   └──────────────── erro transitório ────────────────────┘
DadoInvalido (terminal, definido na validação)
```

- A classificação usa **somente** `estado_anterior` + o resultado do passo B (tabela de research R-03): `Ativo` → `Inativado` (inclusive quando o UPDATE afeta 0 linhas porque uma execução anterior desta mesma intenção já aplicou a mudança); `Inativo` → `JaInativo`; `Inexistente` → `CpfNaoEncontrado`.
- Recuperação: `Pendente` recomeça pela pré-leitura; `EmExecucao` reaplica a mesma tabela com o `estado_anterior` gravado. **Nunca** se infere o estado anterior pela existência da intenção.
- Um erro transitório na pré-leitura mantém `Pendente`; no passo B, mantém `EmExecucao` (com `estado_anterior`) até a próxima tentativa.
- Os estados terminais nunca são reexecutados contra o WAccess.

### Entrega (Inter)

```text
NaoEntregue ──(cópia .tmp + hash conferido + rename)──► Entregue
     ▲                    │
     └──── falha ─────────┘   (origem intacta; .tmp órfão removido no próximo ciclo)
```

---

## 4. Dado externo — `dbo.CHMain` (WAccess)

| Coluna | Tipo real | Uso |
| --- | --- | --- |
| `CHID` | `int` PK | Somente em `OUTPUT inserted.CHID` para contar as linhas afetadas |
| `IdNumber` | `varchar(50) NULL`, **sem unicidade** | Predicado de igualdade com `@Cpf` (`VarChar(50)`) na pré-leitura e no UPDATE |
| `CHState` | `smallint NOT NULL` | Lido na pré-leitura (`<> 1` = ativo); recebe `1` (inativo) só quando `<> 1` |

Nenhuma outra coluna é lida ou escrita pelo serviço. Triggers do WAccess (`wtr_CHMainChangeCHState`) podem alterar outras tabelas ou colunas; isso é comportamento do próprio WAccess e não é controlado pelo serviço. Contrato: [contracts/waccess-inativacao.md](./contracts/waccess-inativacao.md).
