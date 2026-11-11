# Quickstart: validar a inativação e a replicação de ponta a ponta

**Feature**: [spec.md](./spec.md) | **Contratos**: [contracts/](./contracts/) | **Modelo**: [data-model.md](./data-model.md)

Este guia prova que a feature funciona. Ele não traz código de implementação. Os comandos são para PowerShell e partem da raiz do repositório.

## 1. Pré-requisitos

- .NET SDK 10.0.x (`dotnet --list-sdks`).
- Docker em execução, para os testes de integração, contrato e injeção de falhas (Testcontainers).
- Opcional, para a validação manual (§4) e o CA-20: o SQL Server LocalDB com o banco `W_ACCESS` de testes que já está configurado no `appsettings.json` local.

## 2. Configuração local

1. Se ainda não existir, criar `src/InativacaoUsuarioInter.Service/appsettings.json` a partir de `appsettings.example.json` ([contracts/configuracao.md](./contracts/configuracao.md)).
2. Renomear `ConnectionString:WAccessInter` para `ConnectionString:WAccess` (Clarification Q1). A senha de testes continua explícita no arquivo local.
3. Confirmar que `git status` **não** lista `appsettings.json` (ele está no `.gitignore`).

## 3. Suítes automatizadas (mapa CA → teste)

```powershell
dotnet test tests/InativacaoUsuarioInter.Tests.Unit
dotnet test tests/InativacaoUsuarioInter.Tests.Contract        # requer Docker
dotnet test tests/InativacaoUsuarioInter.Tests.Integration     # requer Docker
```

| Critério | Suíte / cenário |
| --- | --- |
| CA-01, CA-02, CA-03 | Integration › `FluxoInter`: JSON e TXT com 3 CPFs existentes |
| CA-04 | Unit › `NormalizadorCpf`; Contract › `WAccess` (zeros à esquerda) |
| CA-05 | Integration › `FluxoInter`: CPF ausente → linha em Falhas + parte `__falhas-` + parte `__sucesso-` |
| CA-06, CA-07 | Integration › `InjecaoFalhas`: banco pausado no meio do arquivo (Toxiproxy) |
| CA-08 | Integration › `InjecaoFalhas`: crash em cada ponto de `IPontoFalha` + retomada |
| CA-09, CA-10 | Integration › `Replicacao`: entrega após esgotar novos arquivos; destino sem permissão |
| CA-11, CA-12 | Integration › `FluxoCondominio` |
| CA-13 | Unit › `LeitorArquivo`; Integration › `FluxoInter` (arquivo malformado ou extensão ignorada) |
| CA-14 | Integration › `Elegibilidade`: arquivo aberto para escrita ou `.tmp` |
| CA-15 | Contract › `WAccess` + Unit › `FormatadorLinhaLog` (saneamento de `\|` e quebras de linha) |
| CA-16 – CA-19 | Unit › `ValidacaoConfiguracao`, `AgendadorCiclo` (`FakeTimeProvider`) |
| CA-20 | Integration › `LocalDbOptIn` (somente com `$env:WACCESS_TESTE_LOCALDB = "1"`) |
| SC-008 | Integration › `TelemetriaCobertura`: exportador em memória; falha se faltar span/atributo ou se aparecer CPF ou senha |
| SC-009 | Integration › `Performance`: arquivo com 1.000 registros ≤ 5 min; p95 ≤ 500 ms; **regressão > 20% contra `Performance/linha-base.json` falha** (análise C3) |
| CA-08 (processo real) | Integration › `CrashRetomada`: serviço como processo filho encerrado por `Environment.FailFast` (`INATIVACAO_PONTO_FALHA`, só com `DOTNET_ENVIRONMENT=Testing`) e retomado (análise C2) |
| Princípio III | Integration › `Idempotencia`: mesma entrada reprocessada → estado final idêntico (banco, pastas, logs, diário) |
| SC-011 | Integration › `AlertaPendencia`: `FakeTimeProvider` avança 24 h |

Resultado esperado: as três suítes verdes. Nenhum teste de injeção de falha pode ser ignorado (Princípio V).

## 4. Validação manual ponta a ponta (duas instâncias, banco local)

> Esta etapa altera `CHMain.CHState` no banco **local de testes**, como autorizado no documento-base §4.1. Antes de começar, anote o `CHState` atual dos CPFs escolhidos.

1. **Preparar as pastas**: usar `Files/Inter` e `Files/Condominio` (já existem no repositório local) ou diretórios temporários.
2. **Instância Inter**: `Settings:Instancia = Inter`, `IntervalSeconds = 10`.
   ```powershell
   dotnet run --project src/InativacaoUsuarioInter.Service
   ```
   Esperado: a primeira varredura acontece logo na partida, e `saude.json` aparece em `%ProgramData%\InativacaoUsuarioInter\Inter\` com `status = Saudavel`.
3. **Entrada**: criar `Files/Inter/Imput/20261111103000.json` com 2 CPFs existentes e ativos e 1 CPF inexistente (formato em [contracts/arquivos-entrada.md](./contracts/arquivos-entrada.md)).
4. **Resultado no Inter** (até ~15 s):
   - `Auditoria/log_inativacao.log`: 2 linhas `status=INATIVADO` ([contracts/logs-auditoria-falhas.md](./contracts/logs-auditoria-falhas.md)).
   - `Falhas/log_inativacao.log`: 1 linha `causa=CPF_NAO_ENCONTRADO`, e `Falhas/20261111103000__falhas-<h8>.json` com o registro.
   - `Output/20261111103000__sucesso-<h8>.json` com os 2 registros; `Imput/` vazio.
   - No banco: os 2 CPFs com `CHState = 1`.
5. **Replicação**: no mesmo ciclo, ou no seguinte, `Files/Condominio/Imput/20261111103000__sucesso-<h8>.json` e o `.traceparent` aparecem. Reinicie o serviço e confirme que **não** surge uma segunda cópia.
6. **Instância Condomínio**: parar o Inter, configurar `Settings:Instancia = Condominio` (com o mesmo banco local ou outro) e rodar o serviço. Os 2 CPFs já estão inativos, então a Auditoria do Condomínio recebe 2 linhas `status=JA_INATIVO`, e o arquivo vai para `Files/Condominio/Processados/`. Nada é criado em `Output`.
7. **Idempotência**: copiar de novo o mesmo arquivo de entrada para `Files/Inter/Imput/`. Esperado: nenhuma linha nova em Auditoria ou Falhas, e o arquivo duplicado sai da entrada.
8. **Falha transitória**: parar o LocalDB (`sqllocaldb stop MSSQLLocalDB`), depositar um arquivo novo, aguardar 2 ciclos e confirmar que o arquivo continua em `Imput/` e que `saude.json` mostra `waccess.status = Indisponivel`. Religar o LocalDB (`sqllocaldb start MSSQLLocalDB`) e confirmar a conclusão no ciclo seguinte (SC-004).
9. **Configuração inválida**: com `IntervalSeconds = 9`, o serviço precisa encerrar na partida com uma mensagem que cita `Settings:IntervalSeconds` e a faixa 10–21600, sem mostrar a string de conexão.
10. **Restaurar** o `CHState` original dos CPFs usados (passo exclusivo do ambiente de teste).

## 5. Instalação como serviço Windows (por instalação)

```powershell
dotnet publish src/InativacaoUsuarioInter.Service -c Release -o C:\Servicos\InativacaoUsuarioInter
sc.exe create InativacaoUsuarioInter binPath= "C:\Servicos\InativacaoUsuarioInter\InativacaoUsuarioInter.Service.exe" start= delayed-auto
sc.exe start InativacaoUsuarioInter
```

- A conta do serviço precisa de leitura e escrita em `<raiz>/*` e em `Processamento:DiretorioEstado`. Em `Auditoria/`, `Falhas/` e no diretório de estado, o acesso deve ser **restrito** à conta do serviço e à operação autorizada (dados pessoais, FR-038).
- No servidor Inter, a conta também precisa de escrita em `{FolderPathCondominio}/Imput`.
