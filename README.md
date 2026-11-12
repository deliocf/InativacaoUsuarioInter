# Operação — Serviço de Inativação de Usuários WAccess

Guia para instalar, configurar e acompanhar o serviço nas duas instalações: **Inter** e **Condomínio Aureliano Chaves**. A especificação completa está em [specs/001-inativacao-usuarios-waccess](../specs/001-inativacao-usuarios-waccess/).

## 1. O que o serviço faz

A cada ciclo, o serviço:

1. Cria, se faltarem, as pastas da própria raiz: `Input`, `Processed`, `Logs` e `Fail` (no Inter, também `Output`).
2. Migra para os nomes novos o conteúdo de pastas com nomes antigos, se existirem (seção 11).
3. Lê os arquivos `.json`/`.txt` estáveis em `<raiz>/Input`.
4. Inativa cada CPF no WAccess da própria instalação (`CHMain.CHState = 1` e `CHDownloadRequired = 1`, para que o WAccess envie a alteração aos equipamentos).
5. Grava uma linha por CPF em `Logs/log_inativacao.log` (auditoria) e em `Fail/log_inativacao.log` (falhas).
6. Guarda o arquivo concluído em `Processed`; os registros com falha definitiva vão para `Fail/<nome do original>`; pendências ficam na entrada.

Na instalação Inter, cada arquivo concluído também entra na fila `<FolderPathInter>/Output` e é entregue em `<FolderPathCondominio>/Input` **no mesmo ciclo**; com a entrega confirmada, ele sai de `Output`. Se o Condomínio estiver indisponível, o arquivo espera em `Output` e a entrega é tentada a cada ciclo, sem bloquear a entrada do Inter.

O primeiro ciclo roda na partida. Cada ciclo seguinte começa `Settings:IntervalSeconds` segundos (de 10 a 21600) **depois do fim** do anterior.

## 2. Instalação como serviço Windows

Em cada servidor, com o .NET 10 Runtime instalado:

```powershell
dotnet publish src/InativacaoUsuarioInter.Service -c Release -o C:\Servicos\InativacaoUsuarioInter
# Criar C:\Servicos\InativacaoUsuarioInter\appsettings.json a partir de appsettings.example.json (seção 3).
sc.exe create InativacaoUsuarioInter binPath= "C:\Servicos\InativacaoUsuarioInter\InativacaoUsuarioInter.Service.exe" start= delayed-auto obj= "DOMINIO\svc-inativacao" password= "<senha-da-conta>"
sc.exe failure InativacaoUsuarioInter reset= 86400 actions= restart/60000/restart/60000/restart/60000
sc.exe start InativacaoUsuarioInter
```

- Com configuração inválida, o processo **encerra na partida com código 1** e registra no log (Event Log ou console) uma mensagem que cita a chave e a regra violada. A mensagem nunca mostra a string de conexão.
- Para parar: `sc.exe stop InativacaoUsuarioInter`. O desligamento gracioso respeita `HostOptions:ShutdownTimeout` (padrão 30 s). Um registro interrompido fica pendente e é retomado sem duplicação.

> O instalador `InativacaoUsuarioInter.Setup.vdproj` ainda aponta para a saída de Debug e não registra o serviço (tarefa opcional T092). Use o procedimento acima.

## 3. Configuração (`appsettings.json`)

O contrato completo está em [contracts/configuracao.md](../specs/001-inativacao-usuarios-waccess/contracts/configuracao.md).

| Chave | Inter | Condomínio |
| --- | --- | --- |
| `Settings:Instancia` | `Inter` | `Condominio` |
| `Settings:IntervalSeconds` | 10–21600 | 10–21600 |
| `SharePointFolder:FolderPathInter` | obrigatória | — |
| `SharePointFolder:FolderPathCondominio` | obrigatória (destino da replicação) | obrigatória |
| `ConnectionString:WAccess` | WAccess do Inter | WAccess do Condomínio |
| `Processamento:DiretorioEstado` | padrão `%ProgramData%\InativacaoUsuarioInter\Inter` | padrão `%ProgramData%\InativacaoUsuarioInter\Condominio` |

- A chave antiga `ConnectionString:WAccessInter` **não é mais lida**. Se ela estiver presente sem `WAccess`, a partida falha pedindo a renomeação.
- `appsettings.json` **não é versionado** (`.gitignore`). O repositório só contém `appsettings.example.json`, com placeholders.
- No ambiente local de testes, a senha fica explícita no `appsettings.json` local (documento-base §4.1). **Ela já esteve no histórico do git**, então troque a senha do usuário `waccess` de testes.
- Para exportar telemetria, defina `Telemetria:OtlpEndpoint` (ex.: `http://coletor:4317`). Sem o endpoint, a instrumentação continua ativa e nada é enviado.

## 4. Contas e permissões (dados pessoais, LGPD — FR-038)

| Caminho | Conta do serviço | Demais usuários |
| --- | --- | --- |
| `<raiz>/Input` | leitura, escrita, exclusão | escrita para quem envia os arquivos |
| `<raiz>/Processed` (e `<raiz>/Output` no Inter) | leitura e escrita | somente leitura para a operação |
| `<raiz>/Logs`, `<raiz>/Fail` | leitura e escrita | **somente operação autorizada**: contêm CPF e nome |
| `Processamento:DiretorioEstado` (disco **local**) | controle total | **nenhum**: `estado.db` guarda linhas com CPF e nome |
| `<FolderPathCondominio>/Input` (no servidor Inter) | criar a pasta, escrita, renomear, excluir `.tmp` | — |

O diário `estado.db` (SQLite) não pode ficar em compartilhamento de rede.

## 5. Arquivos de entrada

O contrato está em [contracts/arquivos-entrada.md](../specs/001-inativacao-usuarios-waccess/contracts/arquivos-entrada.md).

- JSON: array de `{ "cpf", "id_responsavel", "nome_responsavel" }`, todos como texto. TXT: `cpf,id_responsavel,nome_responsavel` por linha, sem cabeçalho. Codificação UTF-8.
- O CPF pode vir com `.`, `-` ou espaços, que são removidos. Precisa ter 11 dígitos.
- Limite por arquivo: 1.000 registros e 1 MB. Acima disso, o arquivo inteiro é rejeitado (`ARQUIVO_EXCEDE_LIMITE`).
- O arquivo só é lido quando está **estável**: sem escrita há `Processamento:EstabilizacaoSegundos` (padrão 5 s) e sem outro processo com ele aberto.

## 6. Resultados e onde ficam

| Situação | Banco | Log | Arquivo |
| --- | --- | --- | --- |
| CPF ativo | `CHState = 1` e `CHDownloadRequired = 1` | Logs `status=INATIVADO` | — |
| CPF já inativo | só `CHDownloadRequired = 1`, se a marca faltar | Logs `status=JA_INATIVO` | — |
| CPF inexistente | — | Fail `causa=CPF_NAO_ENCONTRADO` | registro em `Fail/<nome do original>` |
| Registro inválido | — | Fail `causa=DADO_INVALIDO` | idem |
| Arquivo inválido ou acima do limite | — | Fail `ARQUIVO_INVALIDO` / `ARQUIVO_EXCEDE_LIMITE` | arquivo inteiro movido para `Fail/` |
| Banco ou pasta indisponível | — | nenhuma linha (só log técnico) | pendentes ficam em `Input/<base>__pendente-<h8>.<ext>`, ou o arquivo fica intacto se nada foi concluído |
| Todos com sucesso | — | — | arquivo original movido para `Processed`; no Inter, também entregue ao Condomínio |
| Sucesso parcial | — | — | `<base>__sucesso-<h8>.<ext>` em `Processed` (no Inter, também entregue ao Condomínio) |

Os arquivos de `Fail/` têm o mesmo nome e formato do original e servem para correção e reenvio manual. Se já existir em `Fail/` um arquivo com o mesmo nome e outro conteúdo (o mesmo nome reenviado dias depois), o novo recebe o nome `<base>__<h8>.<ext>`; nada é sobrescrito.

## 7. Saúde (`saude.json`)

O arquivo `<DiretorioEstado>/saude.json` é regravado na partida e no início e fim de cada ciclo. O contrato está em [contracts/saude.md](../specs/001-inativacao-usuarios-waccess/contracts/saude.md).

- **Liveness**: considere o serviço parado se `agora > proximoCicloPrevisto + 2 × intervaloSegundos + 300 s`.
- **`status`**:
  - `Saudavel`: tudo em ordem.
  - `Degradado`: alguma dependência ficou indisponível no ciclo; o trabalho fica pendente e é retentado automaticamente.
  - `Alerta`: há itens em `alertas[]` que precisam de ação.
- **Alertas**:

| Código | Significado | Ação |
| --- | --- | --- |
| `PENDENCIA_ACIMA_LIMITE` | Registro pendente há mais de `AlertaPendenciaHoras` (24 h) | Verificar a conectividade com o WAccess e as permissões; o acesso ainda não foi revogado |
| `PERMISSAO_NEGADA` | Falha de permissão em pasta | Corrigir as ACLs (seção 4) |
| `CONFIGURACAO_WACCESS` | Login, objeto ou permissão no banco | Verificar `ConnectionString:WAccess` e o usuário do banco |
| `ARQUIVO_OUTPUT_DESCONHECIDO` | Há na fila `Output` um arquivo que o serviço não gerou | Ele **não será entregue** ao Condomínio; remova-o ou reenvie pela entrada |
| `ENTREGA_PENDENTE_ACIMA_LIMITE` | Arquivo na fila `Output` do Inter há mais de `AlertaPendenciaHoras` (24 h) sem conseguir ser entregue ao Condomínio | Verificar o acesso a `<FolderPathCondominio>/Input`; o Condomínio ainda não recebeu essas inativações |

## 8. Pontos de atenção conhecidos

- **CPF com mais de um cadastro no WAccess** (research R-02): todos os cadastros daquele CPF são inativados, e o log técnico registra o aviso `CpfComMultiplosCadastros`. **Regra confirmada pelo negócio em 2026-11-11.**
- **`IdNumber` gravado com pontuação no WAccess**: a busca usa os 11 dígitos, então esse cadastro resulta em `CPF_NAO_ENCONTRADO`.
- **Arquivos TXT em ANSI/Windows-1252** (acentos): o arquivo é rejeitado como `ARQUIVO_INVALIDO`. O sistema que gera os arquivos precisa gravar em UTF-8.
- **Proteção de branch no GitHub**: para o CI (`.github/workflows/ci.yml`) bloquear o merge, configure em *Settings → Branches* que `main` exige o check `ci / build-test`.

## 9. Validação manual (quickstart §4)

O roteiro está em [quickstart.md](../specs/001-inativacao-usuarios-waccess/quickstart.md). Os resultados da execução ficam registrados na seção 10.

## 10. Registro de validação

### 2026-11-11 — validação da implementação (ambiente local)

| Verificação | Resultado |
| --- | --- |
| Testes unitários (`Tests.Unit`) | 102/102 aprovados |
| Testes de contrato (`Tests.Contract`, SQL Server 2022 em container) | 50/50 aprovados |
| Testes de integração (`Tests.Integration`: fluxos, injeção de falhas, crash real com `FailFast`, replicação, Condomínio, ponta a ponta, telemetria, idempotência, carga) | 56/56 aprovados (+1 opt-in) |
| CA-20 opt-in contra o `W_ACCESS` local (`WACCESS_TESTE_LOCALDB=1`) | Aprovado: 2 cadastros inativados de verdade, senha ausente dos logs, `CHState` original restaurado |
| Carga, 1.000 registros (pool ligado) | p95 96,5 ms; 1.071 registros/min; 56 s — linha de base gravada em `Performance/linha-base.json` |
| Partida com `Settings:IntervalSeconds=9` e `Settings:Instancia=Matriz` | Encerra com código 1; mensagem cita as duas chaves; sem senha na saída |
| Partida real com o `appsettings.json` local (pastas `Files/`, entrada vazia) | 1º ciclo imediato; `saude.json` com `status=Saudavel` e as 3 dependências `Disponivel` |

### 2026-11-12 — feature 002: pastas novas, fila `Output`, `CHDownloadRequired` (ambiente local)

| Verificação | Resultado |
| --- | --- |
| Testes unitários (`Tests.Unit`) | 121/121 aprovados |
| Testes de contrato (`Tests.Contract`, inclui a varredura de nomes antigos de pasta) | 57/57 aprovados |
| Testes de integração (`Tests.Integration`: migração com crash e retomada, fila `Output`, destino indisponível, alerta de entrega, Condomínio sem `FolderPathInter`, carga) | 82/82 aprovados (+1 opt-in) |
| Carga, 1.000 registros | dentro do orçamento e sem regressão acima de 20% contra `Performance/linha-base.json` |
| CA-20 opt-in contra o `W_ACCESS` local | Aprovado: `CHState` e `CHDownloadRequired` gravados e depois restaurados |
| Cenário manual com `Files/Inter/Input/20261112111111.txt` (3 CPFs), diário novo | Inter: 3 `INATIVADO`, arquivo em `Processed`, entregue em `Condominio/Input` com `.traceparent`, `Output` vazio; banco: `CHState = 1` e `CHDownloadRequired = 1` nas 3 linhas. Condomínio (mesmo banco local): 3 `JA_INATIVO`, arquivo em `Processed` |
| Observação | Com o diário de `%ProgramData%`, o arquivo foi reconhecido como já processado pela versão anterior (mesmo conteúdo) e removido da entrada como duplicado, sem efeito no banco — comportamento de idempotência esperado. O alerta `PERMISSAO_ABERTA_DADOS_PESSOAIS` aparece na máquina de desenvolvimento por causa das ACLs herdadas |

## 11. Atualização para a versão com as pastas novas (feature 002)

A especificação está em [specs/002-ajustes-pastas-fluxo](../specs/002-ajustes-pastas-fluxo/).

- **Ordem obrigatória: atualize o Condomínio primeiro (ou na mesma janela) e depois o Inter.** Com o Condomínio novo e o Inter antigo, as entregas que o Inter antigo ainda fizer na pasta antiga são migradas pelo Condomínio a cada ciclo. Com o Inter novo e o Condomínio antigo, as entregas ficam paradas em `<FolderPathCondominio>/Input` até o Condomínio ser atualizado.
- Pastas antigas e novas: `Imput` → `Input`, `Auditoria` → `Logs`, `Processados` → `Processed`, `Falhas` → `Fail`. A migração é automática, a cada ciclo, enquanto existir alguma pasta antiga: os arquivos são movidos (com o `.traceparent`), os logs antigos são mesclados aos novos (linhas antigas antes das novas) e cada pasta antiga é removida quando fica vazia. Arquivos `.tmp` na pasta antiga não são tocados.
- No Inter, os arquivos que a versão antiga deixou em `Output` e que já foram entregues vão para `Processed` sem nova entrega; os ainda não entregues são entregues e depois vão para `Processed`.
- Nenhuma chave de configuração muda. O diário `estado.db` é atualizado sozinho na partida.
- Cadastros inativados pela versão antiga não recebem `CHDownloadRequired = 1` retroativamente; reenviar o CPF num arquivo grava a marca (status `JA_INATIVO`).
