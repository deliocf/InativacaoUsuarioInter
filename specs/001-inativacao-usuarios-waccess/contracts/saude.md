# Contrato: Sinal de saúde (`saude.json`)

**Origem**: FR-040, FR-042; constituição ("Health"); [research.md R-13](../research.md).
**Teste de contrato**: `tests/InativacaoUsuarioInter.Tests.Contract/Saude/*`.

**Caminho**: `<Processamento:DiretorioEstado>/saude.json`. É regravado de forma atômica (`.tmp` + rename) no início e no fim de cada ciclo e na partida.

```json
{
  "versao": 1,
  "instancia": "Inter",
  "status": "Saudavel",
  "iniciadoEm": "2026-11-11T08:00:00-03:00",
  "ultimoCicloInicio": "2026-11-11T10:30:00-03:00",
  "ultimoCicloFim": "2026-11-11T10:30:02-03:00",
  "proximoCicloPrevisto": "2026-11-11T10:31:02-03:00",
  "intervaloSegundos": 60,
  "dependencias": {
    "waccess":    { "status": "Disponivel", "circuito": "Fechado", "ultimaFalha": null },
    "pastaLocal": { "status": "Disponivel", "circuito": "Fechado", "ultimaFalha": null },
    "replicacao": { "status": "Disponivel", "circuito": "Fechado", "ultimaFalha": null }
  },
  "backlog": { "registrosPendentes": 0, "pendenciaMaisAntigaDesde": null, "entregasPendentes": 0, "arquivoEntradaMaisAntigoDesde": null },
  "alertas": []
}
```

| Campo | Valores |
| --- | --- |
| `status` | `Saudavel` \| `Degradado` (alguma dependência indisponível) \| `Alerta` (há item em `alertas`) |
| `dependencias.*.status` | `Disponivel` \| `Indisponivel` \| `NaoAplicavel` (`replicacao` no Condomínio) |
| `dependencias.*.circuito` | `Fechado` \| `Aberto` \| `MeioAberto` |
| `dependencias.*.ultimaFalha` | `{ "em": <ISO 8601>, "categoria": "Timeout"\|"Conexao"\|"Permissao"\|"Configuracao"\|"Bloqueio" }`, sem mensagens brutas, caminhos com credenciais ou CPFs |
| `alertas[]` | `{ "codigo": "PENDENCIA_ACIMA_LIMITE"\|"PERMISSAO_NEGADA"\|"CONFIGURACAO_WACCESS"\|"ARQUIVO_OUTPUT_DESCONHECIDO", "desde": <ISO 8601>, "quantidade": <int> }`. `ARQUIVO_OUTPUT_DESCONHECIDO` (somente Inter) indica arquivo em `Output` não gerado pelo serviço e, por isso, não replicado. `PERMISSAO_ABERTA_DADOS_PESSOAIS` indica que `Auditoria`, `Falhas` ou o diretório de estado concede leitura a grupos amplos (Windows: Everyone, Users, Authenticated Users; Unix: "outros"), com `quantidade` = número de pastas (FR-038) |

**Liveness**: a operação considera o serviço parado se `agora > proximoCicloPrevisto + 2 × intervaloSegundos + 300 s`, ou seja, se o ciclo não terminou dentro do esperado.

**Regras**:
- `PENDENCIA_ACIMA_LIMITE` aparece quando algum registro está pendente há mais de `Processamento:AlertaPendenciaHoras` (padrão 24 h) e sai no 1º ciclo sem pendências acima do limite (FR-042, SC-011).
- *Adicionado pela feature 002 (FR-007a)*: `ENTREGA_PENDENTE_ACIMA_LIMITE` (somente Inter) aparece quando a entrega pendente mais antiga entrou na fila `Output` há mais de `Processamento:AlertaPendenciaHoras`, com `quantidade` = entregas pendentes; sai no 1º ciclo sem entrega pendente acima do limite.
- *Feature 002*: as pastas de dados pessoais verificadas por `PERMISSAO_ABERTA_DADOS_PESSOAIS` passam a ser `Logs` e `Fail`; a entrada passa a ser `Input` (ver `specs/002-ajustes-pastas-fluxo/contracts/estrutura-pastas.md`).
- O arquivo **não contém** CPF, nome, string de conexão nem caminho de pasta.
- `backlog.arquivoEntradaMaisAntigoDesde`: data de escrita do arquivo `.json`/`.txt` mais antigo ainda em `Imput` no fim do ciclo, inclusive adiados por instabilidade ou bloqueio; `null` se a entrada está vazia.
- A readiness das dependências é sondada a cada ciclo (WAccess `SELECT 1`; destino da replicação no Inter), mesmo sem arquivos a processar.
- O mesmo estado é publicado como métricas OTel (`circuito_estado`, `backlog_pendente`, `idade_pendencia_mais_antiga_s`, `idade_arquivo_entrada_mais_antigo_s`).
