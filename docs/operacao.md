# Operação — Serviço de Inativação de Usuários WAccess

Guia para instalar, configurar e acompanhar o serviço nas duas instalações: **Inter** e **Condomínio Aureliano Chaves**. A especificação completa está em [specs/001-inativacao-usuarios-waccess](../specs/001-inativacao-usuarios-waccess/).

## 1. O que o serviço faz

A cada ciclo, o serviço:

1. Lê os arquivos `.json`/`.txt` estáveis em `<raiz>/Imput`.
2. Inativa cada CPF no WAccess da própria instalação (`CHMain.CHState = 1`).
3. Grava uma linha por CPF em `Auditoria/log_inativacao.log` e em `Falhas/log_inativacao.log`.
4. Move ou segrega o arquivo para o destino de concluídos (`Output` no Inter, `Processados` no Condomínio), para `Falhas/`, ou mantém uma parte pendente na entrada.

Na instalação Inter, depois de processar os arquivos novos do ciclo, o serviço também copia para `<FolderPathCondominio>/Imput` os arquivos de `Output` ainda não entregues.

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

Alternativa ao `sc.exe`: o próprio executável registra ou remove o serviço (como administrador), com início automático atrasado e reinício em falha:

```powershell
C:\Servicos\InativacaoUsuarioInter\InativacaoUsuarioInter.Service.exe --instalar
C:\Servicos\InativacaoUsuarioInter\InativacaoUsuarioInter.Service.exe --desinstalar
```

> **Credenciais fora do pacote:** o `appsettings.json` local (com a senha de testes) **não é publicado** nem empacotado no MSI (`CopyToPublishDirectory=Never`). A publicação leva `appsettings.example.json`; crie o `appsettings.json` da instalação a partir dele.

### Instalador MSI (`InativacaoUsuarioInter.Setup.vdproj`) — pendente (T092)

O ajuste do projeto de setup exige o Visual Studio com a extensão *Microsoft Visual Studio Installer Projects*, que não estava disponível para validar a mudança. No designer do projeto de setup:

1. **File System → Application Folder**: manter apenas **Publish Items** do `InativacaoUsuarioInter.Service` (configuração *Release*); remover *Primary output* (`Built`), o *PublishItemsOutputGroup* duplicado e **Content Files** (este último empacotaria o `appsettings.json` local).
2. **Custom Actions → Install**: adicionar *Publish Items* com `Arguments = --instalar` e `InstallerClass = False`.
3. **Custom Actions → Uninstall**: adicionar *Publish Items* com `Arguments = --desinstalar` e `InstallerClass = False`.
4. Ajustar *Manufacturer* e *ProductName* (hoje `Default Company Name` e `InativacaoUsuarioInter.Setup`) e compilar em *Release*.

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
| `<raiz>/Imput` | leitura, escrita, exclusão | escrita para quem envia os arquivos |
| `<raiz>/Output` ou `<raiz>/Processados` | leitura e escrita | somente leitura para a operação |
| `<raiz>/Auditoria`, `<raiz>/Falhas` | leitura e escrita | **somente operação autorizada**: contêm CPF e nome |
| `Processamento:DiretorioEstado` (disco **local**) | controle total | **nenhum**: `estado.db` guarda linhas com CPF e nome |
| `<FolderPathCondominio>/Imput` (no servidor Inter) | escrita, renomear, excluir `.tmp` | — |

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
| CPF ativo | `CHState = 1` | Auditoria `status=INATIVADO` | — |
| CPF já inativo | sem alteração | Auditoria `status=JA_INATIVO` | — |
| CPF inexistente | — | Falhas `causa=CPF_NAO_ENCONTRADO` | registro em `Falhas/<base>__falhas-<h8>.<ext>` |
| Registro inválido | — | Falhas `causa=DADO_INVALIDO` | idem |
| Arquivo inválido ou acima do limite | — | Falhas `ARQUIVO_INVALIDO` / `ARQUIVO_EXCEDE_LIMITE` | arquivo inteiro movido para `Falhas/` |
| Banco ou pasta indisponível | — | nenhuma linha (só log técnico) | pendentes ficam em `Imput/<base>__pendente-<h8>.<ext>`, ou o arquivo fica intacto se nada foi concluído |
| Todos com sucesso | — | — | arquivo original movido para `Output`/`Processados` |
| Sucesso parcial | — | — | `<base>__sucesso-<h8>.<ext>` no destino de concluídos |

Os arquivos em `Falhas/<base>__falhas-*` estão no mesmo formato do original e servem para correção e reenvio manual.

## 7. Saúde (`saude.json`)

O arquivo `<DiretorioEstado>/saude.json` é regravado na partida e no início e fim de cada ciclo. O contrato está em [contracts/saude.md](../specs/001-inativacao-usuarios-waccess/contracts/saude.md).

- **Readiness**: a cada ciclo o serviço testa o WAccess (`SELECT 1`) e, no Inter, o destino da replicação, mesmo sem arquivos a processar.
- **`backlog.arquivoEntradaMaisAntigoDesde`** e a métrica `idade_arquivo_entrada_mais_antigo_s` mostram há quanto tempo o arquivo mais antigo está parado em `Imput`, inclusive os travados por outro processo.
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
| `ARQUIVO_OUTPUT_DESCONHECIDO` | Há em `Output` um arquivo que o serviço não gerou | Ele **não será replicado** (FR-035); remova-o ou reenvie pela entrada |
| `PERMISSAO_ABERTA_DADOS_PESSOAIS` | `Auditoria`, `Falhas` ou o diretório de estado concede leitura a Everyone, Users ou Authenticated Users (Unix: "outros") | Restringir as ACLs (seção 4). Atenção: `%ProgramData%` concede leitura a Users por padrão |

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
