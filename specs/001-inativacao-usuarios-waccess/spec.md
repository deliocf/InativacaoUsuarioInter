# Feature Specification: Inativação e Replicação de Usuários WAccess (Inter → Condomínio)

**Feature Branch**: `main` (nenhum hook de criação de branch registrado; diretório da feature: `specs/001-inativacao-usuarios-waccess`)

**Created**: 2026-11-11

**Status**: Finalizada (2026-11-11) — implementação no PR #1; SC-009 confirmado no CI; regra R-02 confirmada pelo negócio

**Input**: User description: "use Requisitos\Especificacao_SDD_Inativacao_Usuarios_WAccess_Inter_Condominio.md como base"

**Fontes**: `Requisitos/Especificacao_SDD_Inativacao_Usuarios_WAccess_Inter_Condominio.md` (base, doravante "documento-base"), `Requisitos/Inativação Usuários Inter.docx`, `Requisitos/Diagrama corporativo com marca WAccess.png` e `.specify/memory/constitution.md` v1.0.0.

## Contexto

O Banco Inter precisa inativar, no sistema de controle de acesso WAccess, usuários identificados por CPF, a partir de listas enviadas por empregados responsáveis. A inativação acontece primeiro na instalação **Inter** e, em seguida, os registros concluídos com sucesso são entregues à instalação do **Condomínio Aureliano Chaves**, que aplica a mesma inativação no seu próprio WAccess. Uma única solução, instalada duas vezes e diferenciada apenas por configuração (`Settings:Instancia`), atende às duas instalações.

## Clarifications

### Session 2026-11-11

- Q: Qual o nome da chave de conexão WAccess de cada instância? → A: `ConnectionString:WAccess` em **ambas** as instâncias; cada instalação informa nela o seu próprio banco. Substitui a chave `ConnectionString:WAccessInter` citada no requisito original.
- Q: Como tratar registros com falha definitiva em arquivo misto? → A: Registrar no log de `Falhas` **e** gravar em `Falhas` um arquivo, no formato original, contendo somente os registros com falha definitiva. O arquivo conta como concluído quando não restam pendências transitórias, e o destino de concluídos (`Output`/`Processados`) recebe somente os registros com sucesso.
- Q: CPF com pontuação (ex.: `222.222.222-22`) deve ser normalizado ou rejeitado? → A: Normalizar — remover `.`, `-` e espaços internos e então exigir exatamente 11 dígitos; qualquer outro caractere → `DADO_INVALIDO`.
- Q: O mesmo CPF pode estar em mais de um cadastro no WAccess? → A (resposta original): "CPF é chave primária, não repete". **Revisado em 2026-11-11 com evidência do esquema** (research R-01/R-02): a chave primária de `CHMain` é `CHID`; `IdNumber` é `varchar(50)` sem restrição de unicidade, e o banco de testes tem 1 CPF em 2 cadastros. Regra adotada: aplicar o requisito literal (`WHERE IdNumber = @CPF`) e inativar **todos** os cadastros com aquele CPF, com uma única linha de auditoria. **Confirmado pelo negócio em 2026-11-11.**
- Q: Qual o volume esperado e o teto de registros por arquivo? → A: Média de 3 registros por arquivo; teto de 1.000 registros (1 MB) — acima disso o arquivo é rejeitado com `ARQUIVO_EXCEDE_LIMITE`.
- Q: O formato de linha sugerido para os logs de Auditoria e Falhas vira contrato fixo? → A: Sim — uma linha por entrada, campos `chave=valor` separados por ` | `; Auditoria termina em `status=`, Falhas em `causa=`.
- Q: Por quanto tempo um registro pode ficar pendente antes de alertar a operação? → A: 24 horas (configurável); acima disso o serviço sinaliza alerta no estado de saúde e no log técnico, sem interromper as retentativas.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Inativar usuários no Inter a partir de um arquivo (Priority: P1)

Um empregado responsável deposita na pasta de entrada do Inter (`{FolderPathInter}/Imput`) um arquivo `.json` ou `.txt` com a lista de CPFs a inativar, acompanhados da sua matrícula e nome. Na próxima varredura, cada CPF existente no WAccess do Inter é marcado como inativo, cada inativação é registrada na auditoria com os dados do responsável, cada CPF inexistente é registrado como falha definitiva, e o arquivo concluído é movido para `Output`.

**Why this priority**: é o objetivo de negócio central — sem ele nenhum acesso é revogado. Sozinho já entrega valor para a instalação Inter (MVP).

**Independent Test**: com a instância configurada como `Inter` e um banco WAccess local de testes contendo CPFs conhecidos, depositar um arquivo JSON e um TXT e verificar o estado dos usuários no banco, as entradas de auditoria/falha e a localização final dos arquivos.

**Acceptance Scenarios**:

1. **Given** a instância `Inter` e um arquivo JSON com três CPFs existentes e ativos, **When** ocorre a varredura, **Then** os três usuários ficam inativos, a auditoria do Inter recebe exatamente três linhas no formato contratual de FR-019 e o arquivo é movido para `{FolderPathInter}/Output`. *(CA-01, CA-02)*
2. **Given** um arquivo TXT equivalente (`cpf,id_responsavel,nome_responsavel` por linha), **When** ocorre a varredura, **Then** o resultado é idêntico ao cenário 1. *(CA-03)*
3. **Given** um CPF com zeros à esquerda (ex.: `01234567890`), **When** é processado, **Then** a busca e a inativação usam o valor integral de 11 dígitos, sem perda de zeros. *(CA-04)*
4. **Given** um arquivo em que um dos CPFs não existe no WAccess, **When** é processado, **Then** os CPFs existentes são inativados e auditados, o CPF inexistente é registrado em `{FolderPathInter}/Falhas/log_inativacao.log` com causa `CPF_NAO_ENCONTRADO` e arquivo de origem, um arquivo de falhas no formato original contendo apenas esse registro é gravado em `{FolderPathInter}/Falhas`, `Output` recebe um arquivo apenas com os registros inativados, nenhum outro usuário é alterado e esse CPF não é tentado novamente. *(CA-05)*
5. **Given** um registro cujo conteúdo contém caracteres imprevistos (aspas, ponto e vírgula, palavras de comando), **When** é processado, **Then** o conteúdo é tratado exclusivamente como dado — nunca executado como comando — e o registro é rejeitado como dado inválido sem afetar outros registros. *(CA-15)*
6. **Given** um usuário que já estava inativo antes do processamento, **When** seu CPF é processado, **Then** o registro é considerado concluído com sucesso (idempotente), sem nova alteração de estado, e a auditoria registra o status distinto `JA_INATIVO` uma única vez.

---

### User Story 2 - Configurar a instalação e o ciclo de varredura (Priority: P2)

O operador de cada instalação define, no arquivo de configuração, qual instância está rodando (`Inter` ou `Condominio`), os diretórios raiz, a conexão com o WAccess local e o intervalo entre varreduras. O serviço valida tudo na partida e se recusa a iniciar com configuração inválida, explicando qual chave está errada.

**Why this priority**: sem seleção correta de instância e intervalo válido, o serviço poderia agir no banco ou na pasta errada; é pré-requisito de segurança operacional para as demais histórias.

**Independent Test**: iniciar o serviço com combinações válidas e inválidas de configuração e observar se ele parte (e com qual cadência executa ciclos) ou se termina com erro explícito.

**Acceptance Scenarios**:

1. **Given** `Settings:IntervalSeconds = 10` ou `21600`, **When** o serviço inicia, **Then** a configuração é aceita e as varreduras se repetem sem concorrência. *(CA-16, CA-17)*
2. **Given** `Settings:IntervalSeconds` igual a `9`, `21601`, ausente ou não numérico, **When** o serviço inicia, **Then** ele não inicia a rotina de monitoramento e informa um erro de configuração que identifica a chave e a faixa válida (10–21600). *(CA-18)*
3. **Given** `Settings:IntervalSeconds = 60`, **When** o serviço inicia, **Then** a primeira varredura ocorre imediatamente e cada varredura seguinte começa 60 segundos após o **término** da anterior, nunca se sobrepondo. *(CA-19)*
4. **Given** `Settings:Instancia` ausente ou com valor diferente de `Inter`/`Condominio`, ou uma pasta raiz/conexão obrigatória da instância ausente, **When** o serviço inicia, **Then** ele se recusa a iniciar com mensagem que identifica a chave faltante — sem jamais expor a senha da conexão.
5. **Given** a instância configurada com o banco local de testes, **When** os testes de integração rodam, **Then** a inativação altera efetivamente o estado do usuário no banco, a senha permanece legível no arquivo de configuração local e não aparece em nenhum log. *(CA-20)*

---

### User Story 3 - Sobreviver a falhas transitórias sem perder nem duplicar registros (Priority: P3)

Quando o WAccess fica indisponível, o compartilhamento de arquivos falha ou o serviço é reiniciado no meio de um arquivo, os registros ainda não confirmados permanecem pendentes e são retomados em ciclos posteriores, enquanto os registros já concluídos não são executados nem auditados de novo.

**Why this priority**: garante a integridade dos registros (Princípios II e III da constituição); sem isso uma indisponibilidade comum gera perdas ou auditorias duplicadas.

**Independent Test**: com injeção de falhas (banco indisponível no meio de um arquivo, reinício do processo, arquivo bloqueado), verificar que, após a recuperação, cada CPF existente foi inativado e auditado exatamente uma vez e que nenhum arquivo ou registro se perdeu.

**Acceptance Scenarios**:

1. **Given** o banco WAccess indisponível, **When** um arquivo é processado, **Then** nenhum registro é dado como concluído, todos permanecem pendentes e são retentados em ciclos posteriores até o banco voltar. *(CA-06)*
2. **Given** um arquivo em que parte dos registros foi inativada antes de o banco ficar indisponível, **When** o ciclo termina, **Then** `Output` recebe um arquivo, no mesmo formato do original, contendo **somente** os registros concluídos, e a entrada mantém um arquivo contendo **somente** os registros pendentes. *(CA-07)*
3. **Given** o serviço é interrompido abruptamente no meio de um arquivo, **When** é reiniciado, **Then** nenhum arquivo é perdido, os registros já concluídos não são reinativados nem reauditados e os pendentes são retomados, sem falsos sucessos. *(CA-08)*
4. **Given** a inativação de um registro foi confirmada no banco mas a escrita da auditoria falhou, **When** o processamento é retomado, **Then** a auditoria desse registro é escrita exatamente uma vez.
5. **Given** um único arquivo ou registro com erro, **When** o ciclo prossegue, **Then** os demais arquivos e registros continuam sendo processados normalmente.

---

### User Story 4 - Replicar ao Condomínio os registros concluídos no Inter (Priority: P4)

Depois que o Inter processa todos os novos arquivos de entrada de um ciclo, os arquivos de `Output` que ainda não foram entregues são copiados para a pasta de entrada do Condomínio (`{FolderPathCondominio}/Imput`), de forma íntegra e uma única vez.

**Why this priority**: estende a revogação de acesso ao segundo ambiente; depende da História 1, mas a entrega pode ser verificada sozinha (presença e integridade dos arquivos no destino).

**Independent Test**: com arquivos já presentes em `{FolderPathInter}/Output` e nenhum arquivo novo em `Imput`, executar ciclos e verificar que cada arquivo aparece uma única vez, íntegro, em `{FolderPathCondominio}/Imput`, mesmo após vários ciclos e após uma falha de cópia.

**Acceptance Scenarios**:

1. **Given** não há novos arquivos elegíveis na entrada do Inter (desconsiderando arquivos aguardando retentativa) e existem arquivos em `Output` ainda não replicados, **When** ocorre o ciclo, **Then** esses arquivos são copiados para `{FolderPathCondominio}/Imput`, inclusive os resultantes de sucesso parcial. *(CA-09)*
2. **Given** a cópia para o Condomínio falha (destino indisponível ou cópia incompleta), **When** o ciclo termina, **Then** o arquivo de origem permanece em `Output`, não é marcado como entregue e a entrega é tentada novamente em outro ciclo. *(CA-10)*
3. **Given** um arquivo já entregue com sucesso, **When** ocorrem novos ciclos ou o serviço é reiniciado, **Then** ele não é copiado novamente.
4. **Given** uma cópia em andamento, **When** o Condomínio faz sua varredura, **Then** ele não lê o arquivo parcialmente escrito. *(CA-14)*
5. **Given** um arquivo com falha definitiva ou registros pendentes, **When** ocorre a replicação, **Then** apenas registros efetivamente concluídos no Inter são entregues.

---

### User Story 5 - Inativar no Condomínio os registros recebidos (Priority: P5)

A instância `Condominio` monitora sua pasta `Imput`, aplica as mesmas regras de leitura, validação e inativação no WAccess **do condomínio**, registra auditoria e falhas localmente e move os arquivos concluídos para `Processados`, sem replicar para nenhum outro ambiente.

**Why this priority**: completa o fluxo ponta a ponta; reutiliza integralmente as regras das Histórias 1 e 3 com destinos diferentes.

**Independent Test**: com a instância configurada como `Condominio` e um banco WAccess local de testes do condomínio, depositar arquivos em `{FolderPathCondominio}/Imput` e verificar banco, auditoria, falhas e a pasta `Processados`.

**Acceptance Scenarios**:

1. **Given** `Settings:Instancia = Condominio`, **When** ocorre a varredura, **Then** o serviço lê apenas `{FolderPathCondominio}/Imput`, conecta-se apenas ao WAccess do condomínio e registra em `{FolderPathCondominio}/Auditoria/log_inativacao.log`. *(CA-11)*
2. **Given** um arquivo totalmente concluído no Condomínio, **When** o ciclo termina, **Then** o arquivo é movido para `{FolderPathCondominio}/Processados` e nenhuma cópia é gerada em `Output` nem enviada a qualquer outro ambiente. *(CA-12)*
3. **Given** um CPF inexistente no WAccess do condomínio, **When** é processado, **Then** é registrado em `{FolderPathCondominio}/Falhas/log_inativacao.log` como falha definitiva e isolado em um arquivo de falhas, no formato original, em `{FolderPathCondominio}/Falhas`.
4. **Given** um arquivo parcialmente concluído por falha transitória, **When** o ciclo termina, **Then** os registros concluídos vão para `Processados` e os pendentes permanecem na entrada para nova tentativa.

---

### Edge Cases

- **Upload ou cópia incompletos**: arquivo ainda sendo escrito, ou com extensão temporária, não é lido; só se torna elegível quando concluído e estável. *(CA-14)*
- **Extensão não suportada** (qualquer coisa diferente de `.json`/`.txt`): não é processada nem movida; é apenas ignorada (pode ser um upload temporário em curso). *(CA-13)*
- **Arquivo malformado** (JSON inválido, raiz que não é array, arquivo vazio, codificação ilegível): nenhuma inativação é executada; o arquivo inteiro é retirado da entrada, preservado em `Falhas` e registrado no log de falhas com causa `ARQUIVO_INVALIDO`. *(CA-13)*
- **CPF com pontuação** (ex.: `222.222.222-22` ou `222 222 222 22`): normalizado para `22222222222` e processado normalmente.
- **Registro inválido dentro de arquivo válido** (CPF que, após normalização, não tem exatamente 11 dígitos, CPF com letras ou outros símbolos, CPF numérico em JSON, campos ausentes/vazios, linha TXT com menos de três campos): o registro é falha definitiva com causa `DADO_INVALIDO`, entra no arquivo de falhas em `Falhas`, e os demais registros seguem normalmente.
- **Arquivo em que todos os registros falham definitivamente**: nada vai para `Output`/`Processados` (nem é replicado); o arquivo sai da entrada e seus registros ficam no arquivo de falhas em `Falhas`, além do log.
- **Arquivo com sucessos, falhas definitivas e pendências transitórias ao mesmo tempo**: os sucessos vão para o destino de concluídos, as falhas definitivas para o arquivo de falhas, e somente as pendências transitórias permanecem na entrada.
- **Linha de cabeçalho no TXT**: não é removida silenciosamente; é tratada como registro e, por não conter CPF válido, cai em `DADO_INVALIDO`.
- **Vírgula no nome do responsável (TXT)**: os dois primeiros campos são CPF e matrícula; todo o restante da linha compõe o nome.
- **Espaços e linhas vazias**: espaços nas extremidades dos campos são descartados; linhas vazias são ignoradas.
- **CPF repetido no mesmo arquivo**: é processado e auditado uma única vez.
- **CPF com mais de um cadastro no WAccess**: todos os cadastros com aquele CPF são inativados; a auditoria recebe uma única linha para o CPF e a telemetria técnica registra um aviso com a quantidade de cadastros (sem CPF).
- **Nome de arquivo fora do padrão `yyyyMMddHHmmss`**: não é rejeitado; o padrão é tratado como convenção.
- **Colisão de nomes** em `Output`, `Processados`, `Falhas` ou na entrada do Condomínio: nenhum arquivo existente é sobrescrito; arquivo com mesmo conteúdo já presente é tratado como já entregue/movido; conteúdo diferente recebe nome único rastreável ao original.
- **Arquivo acima do volume máximo suportado** (ver Orçamento de Performance): não é processado, é retirado da entrada para `Falhas` e registrado com causa `ARQUIVO_EXCEDE_LIMITE`.
- **Subdiretórios ausentes** (`Imput`, `Output`/`Processados`, `Auditoria`, `Falhas`): são criados quando faltarem; nenhum dado existente é apagado; falta de permissão é reportada como erro claro, sem derrubar o serviço.
- **Mesmo arquivo reaparecendo** (reentrega ou reinício): conteúdo já concluído não gera nova inativação, auditoria ou replicação.
- **Desligamento do serviço durante um registro**: o registro corrente termina ou é abandonado de forma segura, permanecendo pendente se não confirmado.

## Requirements *(mandatory)*

### Functional Requirements

#### Configuração e ciclo de varredura

- **FR-001**: O sistema MUST ser uma única solução cujo comportamento por instalação é definido por `Settings:Instancia`, aceitando somente os valores `Inter` e `Condominio`.
- **FR-002**: O sistema MUST ler as configurações com os nomes de chave exatamente como especificados — `Settings:Instancia`, `Settings:IntervalSeconds`, `SharePointFolder:FolderPathInter`, `SharePointFolder:FolderPathCondominio`, `ConnectionString:WAccess` — preservando a grafia `Imput` no nome da pasta de entrada.
- **FR-003**: As duas instâncias MUST obter a conexão com o WAccess pela mesma chave, `ConnectionString:WAccess`; cada instalação informa nela o seu próprio banco (Inter ou condomínio). A chave `ConnectionString:WAccessInter` do requisito original deixa de ser usada.
- **FR-004**: O sistema MUST validar, na inicialização, todas as configurações obrigatórias da instância e recusar-se a iniciar quando alguma estiver ausente ou inválida, com mensagem que identifique a chave, sem expor credenciais.
- **FR-005**: O sistema MUST aceitar `Settings:IntervalSeconds` somente como número inteiro entre 10 e 21600 (inclusive); qualquer outro valor (incluindo ausente) MUST impedir o início do monitoramento.
- **FR-006**: O sistema MUST executar a primeira varredura imediatamente após iniciar e iniciar cada varredura seguinte `IntervalSeconds` segundos após o término da anterior, nunca executando duas varreduras ao mesmo tempo.
- **FR-007**: Alterações de configuração MUST ser aplicadas na próxima inicialização; ajuste dinâmico durante a execução não é exigido.

#### Leitura e validação de arquivos

- **FR-008**: O sistema MUST considerar elegíveis somente arquivos `.json` e `.txt` da pasta `Imput` da instância corrente que estejam completamente escritos (não bloqueados para escrita e estáveis por um período configurável).
- **FR-009**: O sistema MUST interpretar JSON como um array na raiz de objetos com as propriedades textuais `cpf`, `id_responsavel` e `nome_responsavel`.
- **FR-010**: O sistema MUST interpretar TXT como um registro por linha no formato `cpf,id_responsavel,nome_responsavel`, sem linha de cabeçalho, onde todo o conteúdo após a segunda vírgula compõe o nome.
- **FR-011**: O sistema MUST tratar o CPF sempre como texto, preservando zeros à esquerda. Antes da validação, MUST remover somente pontos (`.`), hífens (`-`) e espaços internos; o resultado MUST ter exatamente 11 dígitos, e qualquer outro caractere ou quantidade de dígitos é `DADO_INVALIDO`. O CPF normalizado é o valor usado na busca, na auditoria, no log de falhas e nos arquivos gerados. O sistema MUST NOT validar dígitos verificadores (os exemplos oficiais usam CPFs demonstrativos como `22222222222`).
- **FR-012**: O sistema MUST validar o arquivo inteiro antes de qualquer inativação; arquivo malformado, vazio ou acima do limite de volume MUST ser retirado da entrada para `Falhas`, sem nenhuma inativação, com entrada no log de falhas de causa `ARQUIVO_INVALIDO` (ou `ARQUIVO_EXCEDE_LIMITE`, quando acima do volume máximo).
- **FR-013**: `id_responsavel` e `nome_responsavel` MUST ser obrigatórios e não vazios, e MUST ser usados somente para auditoria.

#### Inativação no WAccess

- **FR-014**: Para cada registro válido, o sistema MUST inativar no WAccess **da instância corrente** todos os cadastros cujo `IdNumber` na tabela `CHMain` seja igual ao CPF (normalmente um; `IdNumber` não é chave nem único no esquema real), definindo `CHState = 1` (estado de inativação, conforme regra de negócio recebida), sem alterar nenhum outro campo.
- **FR-015**: O CPF MUST ser enviado ao banco exclusivamente como valor parametrizado; conteúdo de arquivo MUST NOT ser incorporado ao texto de comandos.
- **FR-016**: O sistema MUST verificar a existência do CPF; ausência de usuário MUST resultar em falha definitiva `CPF_NAO_ENCONTRADO` e MUST NOT ser considerada sucesso só porque o comando não gerou erro.
- **FR-017**: Usuário já inativo MUST ser tratado como sucesso idempotente, sem nova alteração de estado e com auditoria de status `JA_INATIVO` registrada uma única vez.
- **FR-018**: A instância `Inter` MUST acessar somente o WAccess do Inter e a instância `Condominio` somente o WAccess do condomínio.

#### Auditoria e falhas

- **FR-019**: Para cada inativação concluída, o sistema MUST acrescentar a `{raiz}/Auditoria/log_inativacao.log` exatamente uma linha no formato contratual:
  `<data/hora ISO 8601 com offset> | <Inter|Condominio> | arquivo=<nome> | cpf=<cpf> | id_responsavel=<matrícula> | nome_responsavel=<nome> | status=<INATIVADO|JA_INATIVO>`
  Exemplo: `2026-11-11T10:30:00-03:00 | Inter | arquivo=20261111102958.json | cpf=22222222222 | id_responsavel=1 | nome_responsavel=Maria Eduarda | status=INATIVADO`
- **FR-020**: Para cada falha definitiva, o sistema MUST acrescentar a `{raiz}/Falhas/log_inativacao.log` exatamente uma linha no formato contratual:
  `<data/hora ISO 8601 com offset> | <Inter|Condominio> | arquivo=<nome> | cpf=<cpf ou vazio> | causa=<CPF_NAO_ENCONTRADO|DADO_INVALIDO|ARQUIVO_INVALIDO|ARQUIVO_EXCEDE_LIMITE>`
- **FR-020a**: Em ambos os logs, os campos MUST aparecer na ordem acima. Quebras de linha e o caractere `|` dentro de valores MUST ser substituídos por espaço, de modo que cada entrada ocupe uma única linha e nenhum conteúdo de arquivo altere o layout. O layout MUST ser coberto por teste de contrato.
- **FR-021**: Cada inativação confirmada MUST gerar exatamente uma entrada de auditoria, mesmo diante de falhas intermediárias e reinícios.
- **FR-022**: Falhas definitivas MUST NOT ser retentadas. Além da entrada no log de falhas, os registros de um arquivo com falha definitiva MUST ser gravados em um **arquivo de falhas** em `{raiz}/Falhas`, com o mesmo formato e extensão do original, contendo somente esses registros, com nome único rastreável ao original, para correção e reenvio manual.
- **FR-022a**: Um arquivo MUST ser considerado concluído quando nenhum de seus registros estiver pendente por falha transitória, mesmo que alguns tenham falhado definitivamente; falhas definitivas não impedem a conclusão nem a entrega dos registros com sucesso.

#### Falhas transitórias, retomada e idempotência

- **FR-023**: O sistema MUST classificar todo erro de banco ou de arquivos como transitório (reprocessar) ou definitivo (registrar e não repetir).
- **FR-024**: Registros afetados por falha transitória MUST permanecer pendentes e ser retentados em ciclos posteriores, sem limite de ciclos, com número finito de tentativas e espera crescente dentro de cada ciclo. A soma do tempo das retentativas e das esperas de um ciclo MUST NOT ultrapassar `IntervalSeconds`; ao esgotar esse orçamento, os registros restantes ficam pendentes para o ciclo seguinte.
- **FR-025**: O sistema MUST manter registro durável do progresso por arquivo e por registro, permitindo retomada após reinício sem reexecutar inativações, auditorias, movimentações ou replicações já concluídas.
- **FR-026**: Reprocessar o mesmo conteúdo (mesmo arquivo, mesmo registro, mesma instância) MUST produzir o mesmo estado final, sem efeitos duplicados.
- **FR-027**: Erro em um arquivo ou registro MUST NOT interromper o processamento dos demais nem derrubar o serviço.

#### Destino dos arquivos

- **FR-028**: Arquivo cujos registros foram todos concluídos com sucesso MUST ser movido para `{FolderPathInter}/Output` (Inter) ou `{FolderPathCondominio}/Processados` (Condomínio).
- **FR-029**: Arquivo com resultados mistos MUST ser segregado conforme o resultado de cada registro: sucessos em um arquivo no destino de concluídos, falhas definitivas em um arquivo em `Falhas` (FR-022) e pendências transitórias em um arquivo mantido na entrada. Cada parte contém **somente** os seus registros, no formato e extensão do original, com nome único rastreável ao original; partes sem registros não são geradas.
- **FR-030**: Criação, segregação e movimentação de arquivos MUST ser feitas de forma que uma interrupção inesperada nunca deixe arquivo corrompido, parcial ou perdido, e MUST NOT sobrescrever arquivos existentes.
- **FR-031**: O sistema MUST criar os subdiretórios necessários quando ausentes, sem apagar dados existentes.

#### Replicação Inter → Condomínio

- **FR-032**: Somente na instância `Inter`, quando não houver novos arquivos elegíveis na entrada (desconsiderando os arquivos de pendências aguardando retentativa), o sistema MUST copiar para `{FolderPathCondominio}/Imput` cada arquivo de `Output` ainda não entregue com sucesso.
- **FR-033**: A entrega MUST tornar o arquivo visível no destino apenas quando completo, e só MUST ser registrada como concluída, de forma durável, após confirmar a integridade da cópia.
- **FR-034**: Arquivo já entregue MUST NOT ser copiado novamente; falha de entrega MUST manter o arquivo em `Output` e elegível para outro ciclo.
- **FR-035**: Somente registros concluídos no Inter MUST ser replicados; pendências e falhas definitivas MUST NOT ser entregues como sucessos. Arquivos MUST permanecer em `Output` após a entrega.
- **FR-036**: A instância `Condominio` MUST NOT gerar arquivos em `Output` nem replicar para qualquer outro ambiente.

#### Segurança, dados pessoais e observabilidade

- **FR-037**: Senhas e strings de conexão MUST NOT aparecer em logs, mensagens de erro, evidências de teste ou documentação publicada.
- **FR-038**: Os logs de Auditoria e Falhas contêm CPF e nomes (dados pessoais, LGPD) e MUST ser gravados em diretórios com acesso restrito; a telemetria técnica (rastreamento, métricas, logs de diagnóstico) MUST NOT conter CPF ou nomes em claro.
- **FR-039**: Cada ciclo, arquivo e registro MUST ser rastreável na telemetria técnica com ambiente, arquivo, chave de idempotência, resultado (`sucesso|transitoria|definitiva`) e tentativa, incluindo a correlação entre o processamento no Inter e no Condomínio.
- **FR-040**: O sistema MUST expor sinal de saúde do ciclo de varredura e da disponibilidade das dependências (WAccess e diretórios).
- **FR-041**: O sistema MUST expor ao menos: registros processados por resultado, duração por registro/arquivo/ciclo, retentativas, tamanho do backlog pendente e idade do arquivo pendente mais antigo.
- **FR-042**: Quando algum registro permanecer pendente por falha transitória por mais de 24 horas (limite configurável), o sistema MUST sinalizar alerta no estado de saúde e registrar aviso no log técnico (sem CPF em claro), mantendo as retentativas. O alerta MUST cessar quando não houver mais pendências acima do limite.

### Orçamento de Performance *(exigido pelo Princípio VI da constituição)*

| Métrica | Orçamento |
| --- | --- |
| Volume típico por arquivo | média de 3 registros |
| Volume máximo por arquivo | 1.000 registros e 1 MB (configurável); acima disso → `ARQUIVO_EXCEDE_LIMITE` |
| Throughput mínimo (banco saudável) | ≥ 300 registros/minuto por instância |
| Latência p95 por registro (sem retentativas) | ≤ 500 ms |
| Duração máxima de ciclo | arquivo típico (3 registros) concluído em ≤ 10 segundos; arquivo no volume máximo (1.000 registros) em ≤ 5 minutos |
| Memória do processo | ≤ 512 MB com arquivo no volume máximo |

### Key Entities

- **Instância**: instalação em execução (`Inter` ou `Condominio`); determina pastas, banco WAccess, destino de concluídos e se há replicação.
- **Arquivo de Inativação**: arquivo `.json`/`.txt` recebido na entrada; tem nome, formato, conteúdo (identificado por hash) e estado (`novo`, `em processamento`, `parcial com pendências`, `concluído`, `inválido`).
- **Registro de Inativação**: linha/objeto do arquivo com `cpf`, `id_responsavel`, `nome_responsavel`; resultado `INATIVADO`, `JA_INATIVO`, `PENDENTE` (transitório) ou falha definitiva (`CPF_NAO_ENCONTRADO`, `DADO_INVALIDO`); identificado por chave de idempotência determinística (conteúdo do arquivo + instância + CPF).
- **Usuário WAccess**: cadastro em `CHMain` (chave primária `CHID`) localizado por `IdNumber` (= CPF; sem garantia de unicidade no esquema), cujo `CHState = 1` significa inativo; nenhum outro estado é interpretado.
- **Entrada de Auditoria**: evidência de uma inativação concluída (data/hora com offset, ambiente, arquivo, CPF, responsável, status).
- **Entrada de Falha**: evidência de falha definitiva (data/hora, ambiente, arquivo, CPF quando houver, causa).
- **Arquivo de Falhas**: arquivo em `Falhas`, no formato do original, com somente os registros de falha definitiva de um arquivo de entrada; serve para correção e reenvio manual e nunca é replicado.
- **Progresso de Processamento**: registro durável do resultado de cada registro e da etapa de cada arquivo, usado para retomada sem duplicação.
- **Estado de Replicação**: registro durável de quais arquivos de `Output` foram entregues e validados no Condomínio.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% dos CPFs existentes em arquivos válidos resultam em usuário inativo no WAccess da instância e exatamente uma entrada de auditoria.
- **SC-002**: 0 usuários alterados além dos CPFs listados e 0 campos alterados além do estado de inativação, verificado por comparação do banco antes/depois nos testes de aceitação.
- **SC-003**: Em todos os cenários de injeção de falha (banco indisponível, lentidão, reinício no meio de um arquivo, arquivo bloqueado, diretório sem permissão, falha de cópia), 0 registros perdidos, 0 auditorias duplicadas e 0 falsos sucessos.
- **SC-004**: Após o restabelecimento do WAccess, 100% dos registros pendentes são concluídos no primeiro ciclo iniciado depois do fim da pausa de proteção contra falhas em série (configurável, padrão 60 s). Ou seja, em até `pausa + IntervalSeconds` + duração do ciclo após o restabelecimento.
- **SC-005**: 100% dos arquivos de `Output` não entregues são entregues, íntegros, ao Condomínio no primeiro ciclo do Inter sem novos arquivos de entrada e com o destino disponível; em 100 ciclos consecutivos, 0 entregas duplicadas.
- **SC-006**: 100% das configurações inválidas (instância, intervalo fora de 10–21600, pasta ou conexão ausente) impedem a partida com mensagem que identifica a chave; 0 ocorrências de senha nas mensagens.
- **SC-007**: A primeira varredura ocorre em até 5 segundos após a partida, e cada varredura seguinte começa entre `IntervalSeconds` e `IntervalSeconds + 2` segundos após o término da anterior, sem sobreposição.
- **SC-008**: 0 ocorrências de senha ou string de conexão em qualquer log, e 0 CPFs ou nomes em claro na telemetria técnica, verificado por varredura automatizada dos artefatos de teste.
- **SC-009**: O orçamento de performance acima é atendido no teste de carga com o volume máximo declarado.
- **SC-010**: Um operador consegue rastrear qualquer CPF auditado até o arquivo de origem, o responsável e o ambiente consultando apenas os logs de Auditoria/Falhas, em menos de 1 minuto.
- **SC-011**: Em 100% dos testes com pendência transitória simulada acima de 24 horas, o estado de saúde passa a indicar alerta no primeiro ciclo após o limite e volta ao normal no primeiro ciclo após a pendência ser resolvida.

## Assumptions

- **Transporte entre ambientes**: `SharePointFolder:FolderPathCondominio` é um diretório (compartilhamento já montado) acessível pelo servidor Inter. FTP/SFTP, credenciais FTP e serviço de transferência próprio estão fora do escopo, conforme seção 3.2 do documento-base.
- **Somente o banco WAccess existente**: nenhuma API WAccess nova; a inativação é feita diretamente na tabela `CHMain`. O esquema e a regra de inativação do WAccess não são alterados.
- **Fora do escopo**: criação/manutenção ou reativação de usuários, alteração do cadastro de responsáveis, interface de upload de arquivos e expurgo automático.
- **Usuário já inativo** (ponto em aberto 6): resolvido pelo Princípio III da constituição — sucesso idempotente, com auditoria de status `JA_INATIVO` registrada uma única vez, sem afirmar mudança de estado.
- **Unicidade do CPF** (ponto 7): `CHMain.IdNumber` **não** é único no esquema real (ver Clarifications, revisão de 2026-11-11). Cada CPF normalmente corresponde a um cadastro; havendo mais de um, todos são inativados (regra literal, confirmada pelo negócio em 2026-11-11).
- **Contrato de entrada** (ponto 8): UTF-8 com ou sem BOM; espaços nas extremidades descartados; linhas vazias ignoradas; sem cabeçalho; CPF de 11 dígitos sem validação de dígito verificador, com pontuação normalizada (ver Clarifications); CPF numérico em JSON → `DADO_INVALIDO`; CPF repetido processado uma vez.
- **Nome do arquivo** (ponto 9): `yyyyMMddHHmmss` (24 h) é a convenção recomendada, mas não é validada; nomes fora do padrão são aceitos.
- **Fuso horário** (ponto 11): horário local da instalação com offset UTC explícito (ISO 8601), como no exemplo do documento-base.
- **Retenção** (ponto 12): nenhum expurgo automático de `Output`, `Processados`, logs ou arquivos de falha; retenção e backup ficam a cargo da operação.
- **Ordem de replicação** (ponto 13): arquivos de sucesso parcial são entregues mesmo havendo pendências aguardando retentativa, após esgotados os novos arquivos do ciclo (leitura literal do requisito, adotada pelo documento-base).
- **Falhas no Condomínio** (ponto 14): estrutura `Falhas/` simétrica à do Inter (log e arquivos de falhas); partes concluídas de arquivos parciais vão para `Processados`.
- **Migração da chave de conexão**: o `appsettings.json` atual do projeto usa `ConnectionString:WAccessInter`; com a decisão de 2026-11-11, essa entrada passa a se chamar `ConnectionString:WAccess`. A renomeação fica para a implementação.
- **Retentativas** (ponto 3): dentro de cada ciclo, até 3 retentativas por operação, com espera exponencial aleatorizada (base 1 s, teto 4 s). Esses valores ajustam os defaults revisáveis da constituição (5 tentativas, teto 60 s) ao intervalo mínimo de 10 s (research R-06). Além disso, o tempo total gasto em retentativas e esperas num ciclo é limitado a `IntervalSeconds`; esgotado esse orçamento, os registros restantes ficam pendentes para o próximo ciclo. Entre ciclos, pendências transitórias são retentadas indefinidamente, e pendências com mais de 24 horas geram alerta (FR-042).
- **Consistência banco/auditoria/arquivo** (ponto 10): garantida pelo progresso durável (FR-025) e pela regra de exatamente uma auditoria por inativação (FR-021).
- **Banco de testes local**: todas as conexões em `ConnectionString` do `appsettings.json` deste projeto apontam para bancos locais de testes; os testes podem alterar `CHMain.CHState` de verdade. A senha de testes permanece explícita e legível no arquivo de configuração local (seção 4.1 do documento-base), nunca em logs. Essa autorização não se estende a produção.
- **Tensão com a constituição**: a constituição exige que segredos não sejam versionados, enquanto o documento-base mantém a senha de testes explícita no `appsettings.json` local. Para conciliar, assume-se que o valor explícito fica no arquivo de configuração **local** da máquina de testes e que o arquivo versionado não carrega credenciais reais; o plano deve tratar isso explicitamente.
- **Orçamento de performance**: o volume típico (3) e o teto (1.000 registros) foram confirmados pelo usuário; latência, throughput e memória são defaults da constituição, não metas de negócio aprovadas.
