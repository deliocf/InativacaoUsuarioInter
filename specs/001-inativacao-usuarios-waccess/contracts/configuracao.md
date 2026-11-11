# Contrato: Configuração (`appsettings.json`)

**Origem**: FR-001–FR-007, Clarification Q1, constituição ("Configuração" e "Defaults revisáveis").
**Teste de contrato**: `tests/InativacaoUsuarioInter.Tests.Contract/Configuracao/*`.

A configuração é validada **na partida** (`ValidateOnStart`). Qualquer violação impede o início do serviço, com uma mensagem que cita a chave e a regra violada, **sem exibir valores de `ConnectionString`**.

## Chaves de negócio (nomes exatos)

| Chave | Obrigatória em | Regra |
| --- | --- | --- |
| `Settings:Instancia` | Ambas | `Inter` \| `Condominio`, sem diferenciar maiúsculas |
| `Settings:IntervalSeconds` | Ambas | Inteiro entre 10 e 21600, inclusive |
| `SharePointFolder:FolderPathInter` | Inter | Caminho absoluto |
| `SharePointFolder:FolderPathCondominio` | Ambas | Caminho absoluto (no Inter, é o destino da replicação) |
| `ConnectionString:WAccess` | Ambas | Não vazia; aponta para o WAccess **da própria instalação** |

Se `ConnectionString:WAccessInter` estiver presente e `ConnectionString:WAccess` ausente, a partida falha com: *"Renomeie ConnectionString:WAccessInter para ConnectionString:WAccess"* (R-16).

## Chaves técnicas (novas; todas opcionais, com default)

| Chave | Default | Faixa |
| --- | --- | --- |
| `Processamento:DiretorioEstado` | `%ProgramData%\InativacaoUsuarioInter\<Instancia>` | Caminho absoluto **local** |
| `Processamento:EstabilizacaoSegundos` | 5 | 1–300 |
| `Processamento:MaximoRegistros` | 1000 | 1–1000 |
| `Processamento:TamanhoMaximoBytes` | 1048576 | 1–1048576 |
| `Processamento:AlertaPendenciaHoras` | 24 | 1–720 |
| `Processamento:TimeoutDiarioMs` | 5000 | 1000–60000 (espera por bloqueio do diário SQLite) |
| `Resiliencia:WAccess:TimeoutConexaoSegundos` | 15 | 1–120 |
| `Resiliencia:WAccess:TimeoutComandoSegundos` | 30 | 1–300 |
| `Resiliencia:<Dep>:MaxTentativas` | 3 | 0–10 |
| `Resiliencia:<Dep>:AtrasoBaseMs` | 1000 | 100–60000 |
| `Resiliencia:<Dep>:AtrasoMaximoMs` | 4000 | ≥ `AtrasoBaseMs` |
| `Resiliencia:<Dep>:Circuito:TaxaFalha` | 0.5 | 0.1–1.0 |
| `Resiliencia:<Dep>:Circuito:JanelaSegundos` | 30 | 5–600 |
| `Resiliencia:<Dep>:Circuito:MinimoChamadas` | 3 | 2–100 |
| `Resiliencia:<Dep>:Circuito:PausaSegundos` | 60 | 5–3600 |
| `Resiliencia:Arquivos:TimeoutOperacaoSegundos` | 30 | 1–300 |
| `Telemetria:OtlpEndpoint` | (vazio = sem exportação) | URI |
| `HostOptions:ShutdownTimeout` | `00:00:30` | — |

`<Dep>` ∈ `WAccess`, `Arquivos`, `Replicacao`. **Validação cruzada**: `Σ min(AtrasoBaseMs × 2^i, AtrasoMaximoMs)` para `i = 0 … MaxTentativas−1` precisa ser `≤ IntervalSeconds × 1000`, ou seja, a soma das esperas de nova tentativa cabe no intervalo do ciclo (Princípio II). Com os defaults: 1000 + 2000 + 4000 = 7000 ms ≤ 10000 ms (intervalo mínimo). Esta validação estática é só uma checagem de sanidade. A garantia do Princípio II vem do **orçamento de retentativas por ciclo**, aplicado em tempo de execução: o tempo total de retentativas (duração das tentativas extras + esperas) de um ciclo nunca passa de `IntervalSeconds`, qualquer que sejam os timeouts configurados (research R-06).

## Exemplo — Inter (`appsettings.example.json`, versionado)

```json
{
  "Settings": { "Instancia": "Inter", "IntervalSeconds": 60 },
  "SharePointFolder": {
    "FolderPathInter": "<CAMINHO_RAIZ_INTER>",
    "FolderPathCondominio": "<CAMINHO_RAIZ_CONDOMINIO>"
  },
  "ConnectionString": { "WAccess": "<CONEXAO_WACCESS_DA_INSTALACAO>" }
}
```

## Exemplo — Condomínio

```json
{
  "Settings": { "Instancia": "Condominio", "IntervalSeconds": 60 },
  "SharePointFolder": { "FolderPathCondominio": "<CAMINHO_RAIZ_CONDOMINIO>" },
  "ConnectionString": { "WAccess": "<CONEXAO_WACCESS_CONDOMINIO>" }
}
```

## Segredos

- O `appsettings.json` **local** da máquina de testes contém a senha explícita e legível (documento-base §4.1); o serviço não a mascara, criptografa nem move.
- `appsettings.json` **não é versionado** (`.gitignore`); só `appsettings.example.json` vai para o repositório (R-15).
- Os valores de `ConnectionString:*` nunca aparecem em log, mensagem de erro, span ou métrica. As exceções do `SqlClient` são registradas pela categoria (número do erro), sem a string de conexão.
