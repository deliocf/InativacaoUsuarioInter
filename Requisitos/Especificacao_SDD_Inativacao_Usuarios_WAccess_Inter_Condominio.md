# Especificação SDD — Serviço de Inativação e Replicação de Usuários WAccess

**Projeto alvo:** `src/InativacaoUsuarioInter.Service`  
**Ambientes:** Banco Inter (origem) e Condomínio Aureliano Chaves (destino)  
**Status:** especificação para implementação; decisões pendentes identificadas ao final.

## 1. Objetivo

Implementar **um único serviço configurável**, executável em duas instalações independentes, para monitorar periodicamente arquivos de inativação de usuários (`.json` e `.txt`), atualizar o banco de dados WAccess da respectiva instalação, registrar auditoria/falhas e controlar arquivos processados. Na instalação **Inter**, também replicar os registros processados com sucesso para o ambiente **Condominio**. O processamento ocorre primeiro no Banco Inter e, após transferência, no Condomínio Aureliano Chaves.

## 2. Base de referência e limites

- Requisitos recebidos nesta solicitação.
- Documento `Inativação Usuários Inter.docx`: descreve os dois ambientes, a transferência entre eles, os formatos dos registros, o significado dos responsáveis e a convenção de nomes de arquivo.
- Imagem `Diagrama corporativo com marca WAccess.png`: mostra o fluxo usuário → FTP/diretório compartilhado → serviço → WAccess → banco de dados, com uma ramificação de cópia para o FTP do condomínio.
- O documento menciona FTP **ou** diretório compartilhado. O mecanismo físico de transporte/montagem precisa ser confirmado, pois o requisito técnico fornecido especifica **caminhos de pasta** em `appsettings.json`, não um cliente FTP com credenciais.
- **Não** implementar uma API WAccess adicional: a operação solicitada é diretamente na tabela `CHMain` do banco WAccess. Não presumir que o serviço original já tenha módulos, bibliotecas ou interfaces específicas: inspecionar o código existente antes de implementar.

## 3. Escopo

### 3.1 Incluído

1. Monitoramento periódico de diretórios de entrada, com intervalo definido por `Settings:IntervalSeconds` (de 10 a 21600 segundos).
2. Leitura de arquivos JSON e TXT, validação de formato e processamento registro a registro.
3. Inativação de usuário por CPF no banco WAccess do ambiente corrente.
4. Auditoria de inativações e registro de falhas definitivas.
5. Tratamento de erros transitórios com reprocessamento posterior de registros pendentes, preservando os concluídos.
6. Movimentação de arquivos processados para `Output` (Inter) ou `Processados` (Condominio).
7. Replicação Inter → Condomínio exclusivamente a partir dos arquivos já disponibilizados em `Output`, conforme condições do fluxo.
8. Configuração de comportamento por `Settings:Instancia`, sem manter dois códigos diferentes.

### 3.2 Fora do escopo, salvo decisão posterior

- Criação/manutenção de usuários ou reversão da inativação.
- Mudança do esquema do banco WAccess ou de sua regra de inativação.
- Alterações no cadastro de funcionários responsáveis.
- Interface de usuário para upload dos arquivos.
- Serviço FTP próprio ou credenciais FTP adicionais, pois não foram especificados.

## 4. Configurações

**Preservar os nomes de chave solicitados**, inclusive `Imput` (grafia intencional do caminho neste requisito). Evitar valores de conexão e caminhos fixos em código.

| Chave no `appsettings.json` | Instância | Uso |
|---|---|---|
| `Settings:Instancia` | Ambas | `Inter` ou `Condominio` |
| `Settings:IntervalSeconds` | Ambas | Frequência de varredura das pastas, em segundos; mínimo de `10` e máximo de `21600` (6 horas) |
| `SharePointFolder:FolderPathInter` | Inter | Diretório raiz do Inter |
| `SharePointFolder:FolderPathCondominio` | Ambas | Diretório raiz do condomínio; destino de replicação no Inter |
| `ConnectionString:WAccessInter` | Inter | String de conexão com o WAccess do Inter |
| `ConnectionString` (chave específica a definir) | Condominio | Conexão com o WAccess do condomínio; ver pontos em aberto |

### 4.1 Banco de dados de testes e credenciais locais

- **Todas as conexões WAccess informadas na seção `ConnectionString` do `appsettings.json` deste projeto apontam exclusivamente para bancos de dados locais de testes.** Não interpretar essas conexões como acesso a produção.
- **Durante os testes, é permitido atualizar registros existentes**, incluindo executar a inativação (`CHMain.CHState = 1`) e conferir seus efeitos diretamente no banco configurado, sem exigir banco simulado ou modo *read-only*.
- **Não ocultar, mascarar, ofuscar, criptografar ou mover automaticamente a senha** presente na string de conexão de testes; manter seu valor explícito no `appsettings.json` local para permitir o funcionamento e a inspeção da configuração.
- Esta autorização aplica-se **somente ao ambiente local de testes identificado nas configurações**. Não estendê-la a bancos de produção ou a outros ambientes. Não incluir senhas em logs, mensagens de erro, evidências de teste ou documentação publicada. Evitar publicar o arquivo de configuração contendo credenciais reais.

### 4.2 Intervalo de monitoramento

- As instâncias `Inter` e `Condominio` devem consultar periodicamente suas respectivas pastas `Imput` procurando arquivos elegíveis `.json` ou `.txt`.
- Aguardar **`N = Settings:IntervalSeconds` segundos** entre varreduras; a unidade é segundos.
- **Intervalo mínimo: 10 segundos. Intervalo máximo: 21600 segundos (6 horas).** Validar o valor na inicialização; valores ausentes, não numéricos ou fora desse intervalo devem produzir erro claro de configuração, sem iniciar a rotina de monitoramento com um valor arbitrário.
- Realizar a primeira varredura na inicialização e aguardar o intervalo configurado **após o término de cada ciclo** para iniciar o seguinte, impedindo varreduras concorrentes. Manter o tratamento dos arquivos pendentes e a etapa de replicação de acordo com os fluxos desta especificação.
- Uma alteração em `appsettings.json` deve ser aplicada na próxima inicialização do serviço; atualização dinâmica do intervalo em tempo de execução não é requisito.

> **Atenção:** o texto original identifica explicitamente `ConnectionString:WAccessInter` para a instância Inter, mas, para a instância Condominio, menciona somente a conexão “referenciada na ConnectionString”, sem indicar o nome da chave. Não assumir silenciosamente `WAccessCondominio` como uma configuração já existente.

Modelo **ilustrativo**, condicionado à definição da chave do condomínio:

```json
{
  "Settings": {
    "Instancia": "Inter",
    "IntervalSeconds": 60
  },
  "SharePointFolder": {
    "FolderPathInter": "<CAMINHO_RAIZ_INTER>",
    "FolderPathCondominio": "<CAMINHO_RAIZ_CONDOMINIO>"
  },
  "ConnectionString": {
    "WAccessInter": "<CONEXAO_WACCESS_INTER>"
  }
}
```

O exemplo utiliza `60` segundos apenas como configuração demonstrativa. O valor efetivo é sempre o informado em `Settings:IntervalSeconds`, respeitando os limites de 10 a 21600 segundos.

## 5. Estrutura de diretórios

### 5.1 Inter

```text
{SharePointFolder:FolderPathInter}/
├── Imput/                       # Arquivos de entrada (grafia conforme requisito)
├── Output/                      # Arquivos com registros processados com sucesso
├── Auditoria/
│   └── log_inativacao.log
└── Falhas/
    └── log_inativacao.log
```

### 5.2 Condomínio

```text
{SharePointFolder:FolderPathCondominio}/
├── Imput/                       # Arquivos recebidos por replicação
├── Processados/                 # Arquivos totalmente processados com sucesso
├── Auditoria/
│   └── log_inativacao.log
└── Falhas/
    └── log_inativacao.log       # Aplicação simétrica da política de falhas
```

O uso do diretório `Falhas` no condomínio segue a instrução “mesmo procedimento de log”; confirmar em caso de diferença operacional. `Imput` e `Output` diferenciam maiúsculas/minúsculas em sistemas de arquivos case-sensitive. Criar/verificar subdiretórios necessários com tratamento de permissões e erros; não apagar dados existentes.

## 6. Contrato dos arquivos recebidos

### 6.1 Extensões e conteúdo

Aceitar apenas `.json` ou `.txt` para processamento. Os exemplos abaixo são registros demonstrativos; nenhum CPF deve ser tratado como dado real.

**JSON:** array na raiz, com as propriedades textuais `cpf`, `id_responsavel` e `nome_responsavel`.

```json
[
  { "cpf": "22222222222", "id_responsavel": "1", "nome_responsavel": "Maria Eduarda" },
  { "cpf": "33333333333", "id_responsavel": "2", "nome_responsavel": "Carlos Renato da Silva" },
  { "cpf": "44444444444", "id_responsavel": "3", "nome_responsavel": "Luana Figueiredo" }
]
```

**TXT:** um registro por linha, campos separados por vírgula, na ordem `cpf,id_responsavel,nome_responsavel`.

```text
22222222222,1,Maria Eduarda
33333333333,2,Carlos Renato da Silva
44444444444,3,Luana Figueiredo
```

- `cpf`: identificador do usuário no WAccess; preservar como string para não perder zeros à esquerda.
- `id_responsavel`: matrícula do empregado que enviou o arquivo; **somente auditoria**.
- `nome_responsavel`: nome do empregado responsável; **somente auditoria**.
- A especificação original não define linha de cabeçalho no TXT; por padrão, interpretar as linhas como dados, não remover silenciosamente a primeira linha.
- Precisam de decisão: codificação do arquivo, cabeçalhos opcionais, campos com vírgula no nome, espaços, linhas vazias, duplicidades de CPF, CPF inválido e comportamento para arquivos malformados (ver seção 13).

### 6.2 Nome do arquivo

O documento de referência define nome baseado em data e hora: `yyyyMMddhhmmss.json` ou `.txt`. Recomenda-se usar **`yyyyMMddHHmmss`**, com hora de 00 a 23, para não gerar ambiguidades entre AM/PM, mas esta correção exige validação do contrato com o sistema gerador. Os exemplos de nome do documento têm mês `20` e, portanto, **não correspondem a datas válidas**; não incorporá-los como casos válidos de teste. O requisito não informa se nomes fora do padrão devem ser rejeitados; registrar como ponto em aberto.

## 7. Requisito central: inativação no WAccess

Para cada registro válido, abrir conexão com o banco WAccess correspondente ao ambiente da instância e executar a atualização **parametrizada**:

```sql
UPDATE CHMain
SET CHState = 1
WHERE IdNumber = @CPF;
```

Critérios obrigatórios:

1. Vincular `@CPF` ao CPF do arquivo; **nunca** concatenar conteúdo do arquivo à consulta SQL.
2. Não alterar nenhum outro campo da tabela `CHMain`.
3. Verificar se o CPF existe; ausência do registro corresponde à **falha definitiva** explicitada no requisito.
4. Uma execução SQL sem exceção não deve ser considerada prova suficiente de inativação se nenhum usuário correspondente existir.
5. Se o usuário já estiver inativo (`CHState = 1`), adotar política explícita e idempotente, evitando duplicar auditorias de uma mesma inativação (a decisão funcional consta na seção 13).
6. Garantir consistência entre sucesso de banco, auditoria e transição de arquivos; em falha após a atualização, não perder registros nem produzir duplicação indevida na próxima tentativa.

> Nota técnica: `CHState = 1` é o **estado de inativação**, conforme regra de negócio recebida, mesmo que o tipo físico seja boolean/bit. Não inferir o significado de outros estados.

## 8. Fluxo A — Instância `Inter`

**Condição de ativação:** `Settings:Instancia = Inter`.

1. Ler e validar a configuração da instalação e a conectividade necessária.
2. Monitorar periodicamente `{FolderPathInter}/Imput`.
3. Detectar arquivos `.json` e `.txt` já finalizados pelo processo de envio; evitar capturar upload incompleto.
4. Para cada arquivo elegível, analisar e validar registros antes das atualizações possíveis.
5. Para cada registro existente no WAccess Inter, executar `UPDATE CHMain SET CHState = 1 WHERE IdNumber = @CPF`, com parâmetro.
6. Para cada inativação concluída, escrever no arquivo `{FolderPathInter}/Auditoria/log_inativacao.log` pelo menos `cpf`, `id_responsavel`, `nome_responsavel`, data e hora do processamento.
7. Se o CPF não existir, registrar a falha em `{FolderPathInter}/Falhas/log_inativacao.log`, associando CPF e arquivo de origem; definir status definitivo para esse registro, sem insistir indefinidamente.
8. Se ocorrer falha transitória (por exemplo, indisponibilidade de conexão), preservar o registro para retentativa posterior, sem deixar que registros já concluídos sejam executados novamente por descuido.
9. Se **todos os registros** do arquivo forem inativados com sucesso, mover o arquivo para `{FolderPathInter}/Output`.
10. Se apenas parte dos registros for concluída e houver pendências transitórias, gerar em `Output` um arquivo contendo **somente os registros processados com sucesso** e manter na entrada um arquivo contendo **somente os registros pendentes para nova tentativa**, preservando o formato JSON/TXT. A estratégia exata para nomes, operações atômicas e arquivos de falha definitiva exige decisão (seção 13).
11. Quando não houver **novos arquivos elegíveis** para processamento na entrada (desconsiderando arquivos destinados a retentativas, conforme texto original), replicar para `{FolderPathCondominio}/Imput` os arquivos elegíveis presentes em `Output` que **ainda não tenham sido copiados com sucesso**.
12. Após a cópia integral e validada, registrar um estado durável de envio para evitar cópias repetidas em todos os ciclos. Manter o conteúdo em `Output`, salvo política de retenção aprovada.

**Importante:** replicar **somente conjuntos de registros efetivamente concluídos no Inter**; não replicar pendências ou falhas definitivas como se fossem sucessos. A entrega ao condomínio é independente do processamento posterior no banco do condomínio.

## 9. Fluxo B — Instância `Condominio`

**Condição de ativação:** `Settings:Instancia = Condominio`.

1. Monitorar periodicamente `{FolderPathCondominio}/Imput`.
2. Receber arquivos copiados pela instância Inter e processar os formatos `.json` ou `.txt` com as mesmas regras de leitura e validação.
3. Conectar-se **apenas ao banco de dados WAccess do condomínio**, com a string de conexão correspondente a ser confirmada.
4. Executar a mesma inativação `CHMain.CHState = 1` pelo campo `IdNumber`, para cada CPF do arquivo.
5. Registrar inativações em `{FolderPathCondominio}/Auditoria/log_inativacao.log`, incluindo CPF, matrícula, nome do responsável e data/hora.
6. Registrar registros inexistentes como falhas definitivas, seguindo a mesma política de logs; manter registros com falhas transitórias elegíveis para reprocessamento.
7. Mover para `{FolderPathCondominio}/Processados` os arquivos **totalmente concluídos com sucesso**. Para arquivos parcialmente concluídos, aplicar a mesma segregação de sucesso/pendências, com destino de concluídos **`Processados`**, sujeito à definição da política de falhas definitivas.
8. **Não** gerar cópias em `Output` nem executar replicação a um terceiro ambiente.

## 10. Auditoria, falhas e classificação de resultados

| Resultado por registro | Atualização no WAccess | Auditoria | Falhas | Nova tentativa |
|---|---|---|---|---|
| CPF existente; inativação bem-sucedida | `CHState = 1` | Sim | Não | Não |
| CPF não encontrado | Nenhuma | Não | Sim: definitiva | Não |
| Erro temporário de conexão / indisponibilidade | Não confirmada | Não registrar sucesso sem confirmação | Diagnóstico técnico recomendado; destino exato a definir | Sim |
| Erro permanente de dados/formato | Não executar para registro inválido | Não | Política a definir | Não, se classificado como definitivo |
| CPF já inativo | Sem alteração necessária | Política a definir | Política a definir | Não, após definição idempotente |

### 10.1 Conteúdo mínimo do log de auditoria

Registros separados e legíveis contendo:

- Data e hora da inativação com fuso horário ou offset identificável;
- CPF inativado;
- `id_responsavel` (matrícula);
- `nome_responsavel`;
- Identificador do ambiente (`Inter` ou `Condominio`) e nome do arquivo de origem (recomendados para rastreabilidade).

Exemplo de **formato sugerido**, não imposto pela regra de negócio:

```text
2026-11-11T10:30:00-03:00 | Inter | arquivo=20261111102958.json | cpf=22222222222 | id_responsavel=1 | nome_responsavel=Maria Eduarda | status=INATIVADO
```

### 10.2 Conteúdo mínimo do log de falhas

Registrar data/hora, CPF quando identificável, arquivo, ambiente e causa (`CPF_NAO_ENCONTRADO` para o caso especificado). Proteger arquivos de log contra acesso indevido, pois contêm identificadores pessoais. O log de falhas não substitui a preservação dos registros pendentes.

## 11. Integridade e recomendações técnicas de implementação

Estas medidas são **recomendações de engenharia para atender à robustez exigida**, não funcionalidades adicionais de negócio:

- Utilizar o padrão de serviço em background já adotado pela solução (`BackgroundService`/Worker Service, se aplicável).
- Impedir duas execuções simultâneas sobre o mesmo arquivo e a leitura de arquivo ainda em escrita (por convenção de nome temporário, renomeação atômica ou outro protocolo compatível com o compartilhamento).
- Usar staging/arquivos temporários e renomeações seguras para criar os subconjuntos de registros, evitando corrupção/perda em desligamento inesperado.
- Preservar extensão e formato do arquivo original ao segregar registros; garantir nomes únicos para os arquivos de saída e para arquivos replicados.
- Manter um controle durável do progresso por arquivo/registro e do resultado de replicação (manifesto local, base de controle ou abordagem equivalente), para resistir a reinicializações e evitar duplicação. Não marcar replicação como concluída antes de confirmar integridade da cópia.
- Implementar retentativas com atraso configurável e sem espera ocupada; tentativa ilimitada **apenas para falhas reconhecidamente transitórias**, conforme exigência original.
- Manter o serviço operacional caso um único arquivo/registro apresente erro; produzir logs técnicos suficientes para diagnóstico.
- Validar configurações obrigatórias por instância no início da execução; falhar de forma explícita quando uma configuração indispensável estiver ausente.
- Restringir permissões de leitura e escrita nos caminhos de entrada, saída e auditoria. No ambiente local de testes, manter a senha legível no `appsettings.json` conforme seção 4.1, sem ocultação ou proteção automática; **não registrar strings de conexão ou senhas nos logs** e não propagar essa exceção para produção.
- Não introduzir mudanças incompatíveis nos demais serviços da solução. Reutilizar infraestrutura e bibliotecas existentes, após inspeção do projeto.

## 12. Critérios de aceite e cenários de teste

| ID | Dado / situação | Resultado esperado |
|---|---|---|
| CA-01 | `Settings:Instancia=Inter` | Entrada em `{FolderPathInter}/Imput`; banco do Inter; auditoria do Inter |
| CA-02 | JSON com três CPFs existentes | Três registros inativados, três auditorias; arquivo em `Output` |
| CA-03 | TXT com três CPFs existentes | Mesmo resultado de CA-02, respeitando ordem de campos |
| CA-04 | CPF com zeros à esquerda | Valor preservado integralmente na consulta SQL |
| CA-05 | Um CPF ausente | Log de falha definitiva; nenhuma atualização de outro CPF por engano |
| CA-06 | Banco WAccess indisponível | Registros não confirmados ficam pendentes; retentativa posterior |
| CA-07 | Arquivo com sucesso parcial + erro transitório | Somente registros bem-sucedidos em `Output`; somente pendentes em reprocessamento |
| CA-08 | Reinício do serviço em processamento | Nenhuma perda de arquivo; retomada sem falsos sucessos nem duplicações indevidas |
| CA-09 | Nenhum arquivo novo elegível no Inter | `Output` ainda não replicado é copiado para `Condominio/Imput`, mesmo com pendências de reprocessamento na origem |
| CA-10 | Falha na cópia para o condomínio | Origem preservada e entrega repetível em outro ciclo |
| CA-11 | `Settings:Instancia=Condominio` | Entrada no condomínio, banco do condomínio e auditoria local |
| CA-12 | Arquivo integralmente concluído no condomínio | Movido para `Processados`; não existe nova replicação via `Output` |
| CA-13 | Arquivo malformado / extensão não suportada | Não executar SQL com dados incorretos; resultado conforme política de rejeição aprovada |
| CA-14 | Transferência/cópia ainda em andamento | O receptor não lê arquivo parcial |
| CA-15 | SQL e registro com caracteres imprevistos | Consulta parametrizada; logs preservam integridade e não executam entradas como comando |
| CA-16 | `Settings:IntervalSeconds=10` | Intervalo mínimo aceito e varredura recorrente sem concorrência |
| CA-17 | `Settings:IntervalSeconds=21600` | Intervalo máximo de 6 horas aceito |
| CA-18 | `Settings:IntervalSeconds=9`, `21601`, ausente ou inválido | Erro explícito de configuração; rotina não inicia com frequência inválida |
| CA-19 | `Settings:IntervalSeconds=60` em qualquer instância | A primeira varredura ocorre na inicialização; próximas varreduras começam 60 segundos após o término do ciclo anterior |
| CA-20 | Instância configurada com banco de testes local | Testes de integração podem alterar efetivamente `CHMain.CHState`; senha permanece legível no `appsettings.json` local, sem exposição em logs |

Incluir testes unitários para parse/validação/classificação, testes de integração com banco controlado para o `UPDATE` e testes ponta a ponta com as duas instâncias e diretórios isolados. Reproduzir CPFs e nomes demonstrativos dos exemplos, sem depender de dados reais.

## 13. Pontos em aberto — confirmar antes de fechar o contrato funcional

1. **Chave de conexão do condomínio:** qual o nome exato em `appsettings.json`? O texto somente nomeia `ConnectionString:WAccessInter` no Inter.
2. **Endereço/forma de transferência:** `FolderPathCondominio` aponta para compartilhamento já montado no servidor Inter, ou será necessário acesso FTP/SFTP? Em caso de FTP/SFTP, faltam endpoint, autenticação, porta, diretório remoto e requisitos de segurança.
3. **Política adicional de retentativas:** a frequência de varredura já está definida em `Settings:IntervalSeconds` (10–21600 segundos); confirmar apenas se tentativas de falhas transitórias exigem atraso/backoff próprio, além do próximo ciclo de varredura.
4. **Falha definitiva em arquivo misto:** o registro inexistente deve ficar apenas no log ou também ser isolado em arquivo específico? A regra “mover somente se todos forem desativados” precisa ser conciliada com “não retentar falhas definitivas”.
5. **Arquivo com sucessos + pendências:** convenção de nomes e identificação das partes; como evitar colisão com arquivos já existentes em `Output`, `Processados` e `Imput`.
6. **Usuário já inativo:** considerar processamento bem-sucedido/idempotente e como registrar a auditoria sem afirmar que houve mudança de estado não realizada?
7. **Múltiplos usuários para o mesmo CPF:** confirmar unicidade de `CHMain.IdNumber` ou definir comportamento se um `UPDATE` afetar mais de uma linha.
8. **Contrato de entrada:** arquivos com cabeçalho CSV, aspas/vírgulas em nomes, encodings, linhas vazias, propriedades ausentes, CPF em formato numérico ou não numérico, CPFs repetidos, arquivo vazio e CPF inválido.
9. **Nome/data:** o documento prescreve `yyyyMMddhhmmss`, mas recomenda-se `yyyyMMddHHmmss`; exemplos do documento contêm data inválida. Confirmar se o nome deve ser estritamente validado ou apenas usado como convenção.
10. **Auditoria transacional:** qual estratégia de recuperação para `UPDATE` confirmado quando falha escrita de log ou movimentação de arquivo?
11. **Fuso horário:** utilizar horário local da instalação, Brasília, ou UTC com offset?
12. **Retenção e expurgo:** por quanto tempo manter `Output`, `Processados`, logs e arquivos de falha, e como serão feitos backups?
13. **Ordem de replicação:** permitir entrega de arquivos de sucesso parcial enquanto existem arquivos aguardando retentativa, conforme leitura literal do requisito? Esta especificação assume que **sim**, depois de esgotados os novos arquivos elegíveis no ciclo.
14. **Falhas no condomínio:** confirmar estrutura exata de `Falhas` e destino de partes concluídas de arquivos parcialmente processados.

## 14. Instrução final para implementação SDD

Implementar no projeto **`src/InativacaoUsuarioInter.Service`** os requisitos desta especificação, com uma única base de código e seleção do fluxo via `Settings:Instancia`. Primeiro inspecionar a estrutura do projeto e as configurações existentes. Não alterar regras de negócio, nomes de diretórios ou strings de conexão sem evidência ou aprovação. Priorizar integridade dos registros, idempotência, auditoria, isolamento de falhas e replicação segura. Respeitar `Settings:IntervalSeconds` (10–21600 segundos), validar limites e testar a atualização real no banco exclusivamente local de testes. Preservar a senha explícita na configuração local, sem registrá-la em logs. Adicionar testes automatizados correspondentes aos critérios de aceite e documentar as escolhas técnicas. Quando um ponto em aberto impedir uma decisão correta, destacá-lo explicitamente e não inventar uma regra de negócio silenciosa.
