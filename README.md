# D.A.N.T.E.

**Distributed Agent Network for Task Execution**

D.A.N.T.E. é um orquestrador local em .NET 10 que permite controlar **Claude Code** e **Codex CLI** pelo Telegram.

A proposta é simples: o Telegram funciona como interface remota, enquanto os agentes continuam executando localmente na sua máquina, usando as CLIs já instaladas e autenticadas.

```text
Telegram
   ↓
D.A.N.T.E.
   ↓
Command / Context Router
   ↓
General Workspace ou Repository Registry
   ↓
Claude Code / Codex CLI
   ↓
resultado
   ↓
Telegram
```

## Documentação para mantenedores

Se você vai **manter, depurar ou evoluir** o D.A.N.T.E., consulte o [Guia de manutenção](docs/maintainer/README.md).

Ele documenta:

- a arquitetura utilizada e seus limites;
- os módulos `Agents`, `Jobs`, `Repositories`, `Sessions`, `Settings` e `Telegram`;
- os fluxos session-first e one-shot de ponta a ponta;
- Claude stream-json e Codex app-server;
- estado, concorrência, approvals, streaming, formatação e redaction;
- configuração local, operação, troubleshooting e testes;
- glossário e roteiro recomendado de estudo.

A documentação formal das decisões continua em [ARCHITECTURE_DECISIONS](docs/context/ARCHITECTURE_DECISIONS.md).

---

## Estado atual

Converse com o agente como no terminal: uma mensagem comum abre uma **sessão interativa** do
agente padrão e as mensagens seguintes continuam a mesma conversa, com a resposta chegando
enquanto o agente trabalha.

```text
Explique a autenticação deste projeto.
Agora mostre onde o token é validado.
```

Aprovações, perguntas do agente, interrupção e encerramento também são feitos pelo Telegram.
Pedidos de aprovação oferecem botões **Aprovar uma vez**, **Aprovar na sessão** (quando
suportado pelo agente) e **Negar**. Após uma decisão ou os cinco minutos de expiração,
a mensagem mostra o resultado e os botões são removidos. Apenas o dono da sessão pode
responder; cliques duplicados ou tardios são recusados. Os comandos `/approve`,
`/approve-session` e `/deny` continuam disponíveis como fallback e atualizam a mesma
mensagem. As instruções textuais só aparecem no pedido quando o transporte não suporta
botões inline. Sessões com segredos vinculados seguem essa regra com os detalhes omitidos.
Perguntas do agente oferecem botões quando há uma única pergunta com até dez opções curtas.
Para texto livre ou outra orientação, use o **Reply** nativo à mensagem da pergunta, sem copiar
IDs. Se houver várias perguntas no pedido, responda na ordem, separando as respostas por `|`;
por exemplo, `primeira resposta | segunda resposta`. Opções numerosas ou longas aparecem
em texto para resposta por Reply. `/input` continua aceito como fallback e suas instruções
aparecem nos transportes sem suporte a input interativo. Mensagens comuns sem Reply continuam
na conversa; o bot não adivinha a qual pergunta responder. A pergunta é atualizada após resposta,
expiração, interrupção ou fechamento, quando o Telegram permite. Detalhes e opções de pedidos
com segredos vinculados são omitidos, mantendo a resposta por Reply.
`/claude` e `/codex` continuam disponíveis como execução avulsa (one-shot).

Respostas técnicas com blocos Markdown cercados por três ou mais crases (ou `~~~`)
chegam como blocos de código nativos do Telegram. A linguagem é preservada quando
informada (`csharp`, `python`, `bash`, `diff` e outras); texto antes/depois permanece
fora do bloco. Comandos multiline e comandos extensos aparecem após **→ Executando
comando**, em bloco `bash`; comandos curtos continuam compactos. Caminhos do workspace
aparecem relativos, como antes.

Blocos longos são divididos em mensagens válidas, com tags fechadas e espaço para o
prefixo de sessão em background. O conteúdo do agente é escapado antes de gerar HTML,
e segredos são redigidos antes da formatação. Se o Telegram recusar o markup, aquela
parte é enviada em texto simples e a entrega continua. Retry e `/resend` preservam as
partes pendentes e nunca reexecutam o agente.

O contexto de cada conversa ou execução segue um de dois modos.

### General Mode

Use o agente para perguntas, pesquisas, exemplos de código e outras tarefas que não precisam de um projeto específico.

```text
/claude explique a diferença entre JWT e cookie de sessão

/codex escreva um exemplo simples de Strategy Pattern em C#
```

Essas execuções usam um workspace neutro do D.A.N.T.E. e não entram automaticamente em nenhum repositório cadastrado.

### Repository Mode

Use um alias iniciado por `@` para executar o agente dentro de um repositório específico.

```text
/claude @fitness_backend analise a autenticação atual

/codex @dante revise a implementação do JobRegistry
```

O D.A.N.T.E. resolve o alias de forma determinística e inicia o agente diretamente no diretório cadastrado.

---

## Requisitos

- .NET 10 SDK
- Git
- Telegram
- Codex CLI e/ou Claude Code CLI disponíveis no `PATH`
- pelo menos uma das CLIs autenticada localmente para usar o respectivo agente
- opcional, para áudio e vídeo: `ffmpeg` e `whisper.cpp` no `PATH` e um modelo do whisper (ver
  [Áudio e vídeo](#áudio-e-vídeo))

O D.A.N.T.E. **não exige uma API key da OpenAI ou Anthropic**.

Você pode utilizar a autenticação já existente da CLI, inclusive quando ela estiver associada à sua conta/assinatura. Se `OPENAI_API_KEY` ou `ANTHROPIC_API_KEY` existirem no ambiente, o D.A.N.T.E. preserva essas variáveis para a CLI, mas elas são opcionais.

Antes de iniciar o Worker, confirme que as CLIs funcionam diretamente no mesmo usuário/ambiente em que o D.A.N.T.E. será executado.

---

## Executando o projeto

Clone o repositório e execute:

```bash
dotnet build Dante.sln
dotnet test Dante.sln
dotnet run --project src/Dante.Worker
```

O Worker permanece em execução até receber um sinal de encerramento, como `Ctrl+C`.

---

## Configuração do Telegram

Crie um bot usando o **BotFather** e configure o token no ambiente.

### Bash / WSL

```bash
export Telegram__BotToken="<token>"
export Telegram__AllowedUserIds="123456789"
```

Para mais de um usuário:

```bash
export Telegram__AllowedUserIds="123456789,987654321"
```

### PowerShell

```powershell
$env:Telegram__BotToken = "<token>"
$env:Telegram__AllowedUserIds = "123456789"
```

A autorização utiliza `message.from.id`, e não username ou ID do chat.

Se `Telegram__AllowedUserIds` estiver ausente ou possuir uma entrada inválida, os comandos ficam bloqueados.

O Telegram usa **long polling**. Não é necessário webhook, domínio público ou porta exposta.

---

## Primeiro teste

Com o Worker rodando:

```text
/ping
```

Resposta esperada:

```text
pong
```

---

## Executando como serviço

No WSL com systemd, o D.A.N.T.E. pode rodar como serviço do seu usuário, sem terminal aberto: o systemd
inicia o Worker quando a distro sobe e o reinicia após falha inesperada; uma tarefa do Windows sobe a distro
no logon e a mantém ativa.

```text
logon no Windows → tarefa "DANTE WSL" abre a distro (sem janela)
                 → systemd do usuário (linger) → dante.service → Telegram
```

Pré-requisitos: `systemd=true` na seção `[boot]` de `/etc/wsl.conf`, .NET SDK e as CLIs autenticadas no
mesmo usuário Linux. O serviço roda como esse usuário, sem root, e usa as mesmas credenciais locais das CLIs
(`~/.claude`, `~/.codex`).

### Instalar

No WSL, a partir do repositório:

```bash
deploy/dante-service.sh install
```

O serviço lê `~/.config/dante/dante.env`. Na primeira instalação, o comando cria esse arquivo convertendo o
`~/.config/dante/env` do setup manual, se existir, ou copiando [`deploy/dante.env.example`](deploy/dante.env.example).
Depois publica o Worker em `~/.local/share/dante/app`, instala `~/.config/systemd/user/dante.service` e ativa o
linger do usuário. O serviço só é habilitado e iniciado quando `Telegram__BotToken` está preenchido; senão,
preencha e rode `install` de novo:

```bash
nano ~/.config/dante/dante.env      # Telegram__BotToken, Telegram__AllowedUserIds, ...
deploy/dante-service.sh install
```

O systemd não usa shell: o `dante.env` aceita só `CHAVE=valor` literal, sem `$VAR`, `~`, `` `comando` ``, barra
invertida ou comentário na mesma linha (`export` é removido na conversão). Um arquivo com outra coisa é recusado,
indicando apenas o número da linha, antes de qualquer instalação — por exemplo,
`export DANTE_GENERAL_WORKSPACE="$HOME/general"` precisa virar `DANTE_GENERAL_WORKSPACE=/home/<usuario>/general`.
Valide um arquivo com `bash deploy/dante-env.sh check ~/.config/dante/dante.env`.

O `dante.env` guarda segredos: fica fora do repositório, com permissão `600`, e também recebe
`DANTE_GENERAL_WORKSPACE` e as variáveis do host usadas por `/repo env bind`. Se o linger não puder ser ativado
sem privilégio, rode uma vez `sudo loginctl enable-linger $USER`.

Depois, no **PowerShell do Windows** (sem administrador), registre a inicialização da distro no logon. Use o
nome mostrado por `wsl -l -v`:

```powershell
powershell -ExecutionPolicy Bypass -File deploy\windows\Register-DanteAutostart.ps1 -Distro Ubuntu
```

Pare qualquer Worker iniciado manualmente antes de iniciar o serviço: duas instâncias com o mesmo token
disputam o long polling.

### Operar

| Ação | Comando (WSL) |
| --- | --- |
| iniciar | `deploy/dante-service.sh start` ou `systemctl --user start dante` |
| parar | `deploy/dante-service.sh stop` ou `systemctl --user stop dante` |
| reiniciar | `deploy/dante-service.sh restart` ou `systemctl --user restart dante` |
| status | `deploy/dante-service.sh status` ou `systemctl --user status dante` |
| logs ao vivo | `deploy/dante-service.sh logs` ou `journalctl --user -u dante -f` |
| logs do boot atual | `journalctl --user -u dante -b` |
| atualizar após `git pull` | `deploy/dante-service.sh install` (republica e reinicia) |
| desabilitar | `systemctl --user disable --now dante` |
| remover | `deploy/dante-service.sh uninstall` |

`stop` envia `SIGTERM` ao Worker, que encerra jobs e sessões antes de sair. Uma falha inesperada é reiniciada
em 10 s; após 5 falhas em 5 minutos o serviço fica parado até um `start` manual. Os logs ficam no journald do
usuário, uma linha por evento, com prioridade.

Para remover a inicialização no Windows:

```powershell
powershell -ExecutionPolicy Bypass -File deploy\windows\Register-DanteAutostart.ps1 -Unregister
```

### Validar

Reinicie o Windows, faça logon e envie `/ping` pelo Telegram. Sem resposta, confira
`wsl -l -v` (distro `Running`), `systemctl --user status dante` e `journalctl --user -u dante -b`.

---

# Uso

## Consultas gerais

Sem `@alias`, o comando utiliza o **General Mode**.

```text
/claude qual a diferença entre RabbitMQ e Kafka?
```

```text
/codex mostre uma implementação simples de Result Pattern em C#
```

O workspace padrão é:

```text
~/.dante/workspaces/general
```

Você pode substituí-lo:

```bash
export DANTE_GENERAL_WORKSPACE="/caminho/absoluto/general"
```

O workspace geral não pode conter um repositório cadastrado nem ficar dentro dele. O D.A.N.T.E. bloqueia esse tipo de sobreposição para evitar que uma consulta geral modifique projetos reais.

No General Mode:

- nenhum repositório é escolhido por inferência;
- variáveis específicas de projetos não são carregadas;
- Codex executa com sandbox de workspace;
- Claude executa em perfil restrito de ferramentas de arquivo.

---

## Cadastrando repositórios

Os repositórios conhecidos pelo D.A.N.T.E. ficam em:

```text
~/.dante/repositories.json
```

### Adicionar

```text
/repo add @dante /mnt/c/Repos/dante juanverass/dante
```

Outro exemplo:

```text
/repo add @fitness_backend /mnt/c/Repos/fitnessapp_backend juanverass/fitnessapp_backend
```

O terceiro argumento, `owner/repo`, é opcional.

Para paths contendo espaços, utilize aspas:

```text
/repo add @meu_projeto "/mnt/c/Meus Projetos/app" usuario/repositorio
```

O D.A.N.T.E. valida:

- formato do alias;
- path absoluto;
- existência do diretório;
- se o diretório é a raiz de um repositório Git;
- compatibilidade do `origin` com `owner/repo`, quando informado.

Aliases são case-insensitive.

### Listar

```text
/repos
```

Exemplo:

```text
@dante: /mnt/c/Repos/dante (juanverass/dante)
@fitness_backend: /mnt/c/Repos/fitnessapp_backend (juanverass/fitnessapp_backend)
```

### Consultar

```text
/repo show @fitness_backend
```

### Remover

```text
/repo remove @fitness_backend
```

---

## Executando dentro de um repositório

Depois do cadastro:

```text
/claude @fitness_backend analise as entidades atuais do domínio
```

```text
/codex @dante implemente a próxima tarefa
```

O primeiro argumento após `/claude` ou `/codex` é tratado como alias somente quando começa com `@`.

Por exemplo:

```text
/codex explique o uso de @Transactional
```

continua sendo uma consulta geral, pois `@Transactional` não é o primeiro argumento.

Quando um alias é usado:

1. o D.A.N.T.E. resolve o cadastro;
2. fixa o working directory;
3. remove o `@alias` do prompt;
4. carrega o ambiente daquele repositório;
5. cria o job;
6. inicia a CLI no diretório resolvido.

O agente não é responsável por procurar o projeto no disco.

---

# Variáveis de ambiente por repositório

Cada repositório pode possuir seu próprio perfil de ambiente.

## Valor literal

Use apenas para valores não sensíveis:

```text
/repo env set @fitness_backend ASPNETCORE_ENVIRONMENT Development
```

Outro exemplo:

```text
/repo env set @fitness_backend API_BASE_URL https://localhost:5001
```

Não utilize `env set` para senhas, tokens, connection strings com credenciais ou outros segredos.

## Referência a uma variável do host

Para valores sensíveis, configure primeiro a variável no sistema operacional:

```bash
export FITNESS_DATABASE_PASSWORD="<valor>"
```

Depois faça apenas o vínculo:

```text
/repo env bind @fitness_backend DATABASE_PASSWORD FITNESS_DATABASE_PASSWORD
```

O valor real não é gravado no catálogo do D.A.N.T.E.

Ele é resolvido somente quando o job inicia.

Se a variável do host não existir, o agente não é iniciado.

## Listar configuração

```text
/repo env list @fitness_backend
```

Exemplo:

```text
ASPNETCORE_ENVIRONMENT (literal)
DATABASE_PASSWORD (host: FITNESS_DATABASE_PASSWORD)
```

Os valores não são exibidos.

## Remover

```text
/repo env remove @fitness_backend DATABASE_PASSWORD
```

Jobs que utilizam bindings sensíveis não retornam a saída do agente pelo Telegram, reduzindo o risco de um segredo aparecer acidentalmente na resposta.

---

# Agente padrão

O agente padrão fica salvo em `~/.dante/settings.json` e sobrevive a reinícios. Sem
configuração, é **Claude**.

Consultar:

```text
/agent
```

```text
Agente padrão: Claude
```

Alterar (aceita `claude` ou `codex`, sem diferenciar maiúsculas):

```text
/agent set codex
```

```text
Agente padrão alterado para Codex.
```

`/agent` não inicia job. `/claude` e `/codex` valem somente para a execução em que são
usados e não alteram o agente padrão.

Com uma sessão ativa, `/agent` e `/use` mostram também para onde vão as mensagens comuns,
porque a sessão mantém o agente e o contexto com que começou:

```text
Agente padrão: Codex
Mensagens comuns vão para a sessão ativa S000001 (Claude, General) até /session close.
```

O agente padrão vale para conversas e execuções novas. Uma sessão já aberta mantém o seu
agente, e a resposta avisa:

```text
Agente padrão alterado para Codex.
A sessão ativa S000001 (Claude, General) continua; envie /session start para conversar com Codex.
```

## Conversa (mensagens sem slash command)

Mensagens de texto que não começam com `/` são uma conversa com o agente padrão:

```text
Explique o padrão Strategy.
```

```text
E o padrão State?
```

Sem sessão ativa, a primeira mensagem abre uma sessão interativa do agente padrão no
contexto atual (repositório ativo ou General Mode), com o modo escolhido em `/mode`,
e vira o primeiro turno. As mensagens seguintes são novos turnos da mesma sessão, no mesmo
processo do agente, que mantém o contexto da conversa. Não é preciso `/session start`.
A abertura implícita não envia aviso adicional; o modo pode ser consultado em `/mode` e
`/status`, e é informado ao iniciar uma sessão explicitamente com `/session start`.

Em seguida, a resposta mostra essencialmente o texto do agente, com linhas curtas de progresso
(`→ dotnet test`, arquivos alterados, falha de ferramenta) e o indicador "digitando…"
enquanto o agente trabalha. A conversa não mostra `Job ID`, nem avisos de início e fim de
turno: IDs de sessão, turno e entrega ficam em `/status`, `/session list` e nos comandos
explícitos. Só pedidos que exigem ação — aprovação ou pergunta do agente — mostram IDs,
porque os comandos de resposta precisam deles. Uma mensagem enviada enquanto o agente ainda
responde entra na fila e é confirmada com um curto `Recebido`.

Um `@alias` no início da primeira mensagem abre a sessão naquele repositório, sem alterar o
repositório ativo:

```text
@dante revise o README
```

Com uma sessão ativa, a mensagem vai para ela como está: agente e contexto da sessão não
mudam.

A conversa continua até ser encerrada (`/session close`) ou trocada (`/session start`,
`/session select`). Depois de `/session close` ou `/session select none`, a próxima
mensagem abre uma sessão nova. Se a sessão falhar — por exemplo, o processo do agente
encerrar —, o D.A.N.T.E. avisa e recusa as mensagens seguintes até você iniciar outra com
`/session start`, em vez de abrir silenciosamente uma conversa nova sem o contexto anterior.
Erros na conversa são curtos; os detalhes ficam em `/status`.

`/claude` e `/codex` seguem disponíveis como execução avulsa (one-shot, com Job ID), sem
tocar na conversa. Mensagens iniciadas por `/` com comando desconhecido nunca são enviadas
ao agente: o D.A.N.T.E. responde `Comando desconhecido`.

## Modos de trabalho

O modo define quanta autonomia o agente tem numa sessão:

| Modo | Nome | Comportamento |
| --- | --- | --- |
| `manual` (ou `approval`) | aprovação | pede sua aprovação antes de ações fora dos limites da CLI (padrão recomendado) |
| `auto` | automático | a CLI avalia permissões automaticamente, com menos interrupções |
| `plan` | planejamento | analisa e planeja sem alterar arquivos |

```text
/mode           consulta o modo padrão, as opções e o suporte de cada agente
/mode auto      escolhe o modo padrão para as próximas sessões
/mode session manual    troca o modo da sessão ativa, mantendo o padrão
/session start codex @dante plan    inicia uma sessão num modo específico
```

O modo padrão é por usuário, sobrevive a reinícios e vale só para sessões novas.
`/mode <modo>` e `/permissions <modo>` alteram esse padrão; `/mode session <modo>` altera
somente a sessão ativa, sem mudar a preferência persistida. Um modo informado em
`/session start` vale somente para aquela sessão.

A troca exige sessão ociosa, sem mensagens enfileiradas, approvals ou input humano pendentes.
Durante um turno, aguarde a conclusão ou use `/session stop` explicitamente antes de solicitar a troca.
O D.A.N.T.E. nunca interrompe nem responde a requests automaticamente para trocar o modo.
Claude confirma a alteração por `set_permission_mode`. Codex agenda a troca para o próximo
turno da mesma thread: `/status` e `/mode` continuam mostrando o modo efetivo anterior e a
solicitação pendente até `thread/settings/updated` confirmar sandbox, aprovação, revisor e
colaboração. Envie sua próxima mensagem normalmente; não é necessário fechar a sessão.
Solicitar o modo efetivo atual cancela uma troca pendente do Codex.

Recusa upstream mantém o modo anterior. Se a CLI não confirmar as políticas ou a resposta
ficar incerta, a sessão é encerrada com erro para impedir execução com políticas divergentes;
inicie uma nova sessão explicitamente. Agente sem suporte orienta `/session start`, sem
reiniciar silenciosamente. A [investigação #108](docs/spikes/session-mode/README.md) registra
as evidências das CLIs e as limitações.

Cada agente declara os modos que suporta; pedir um modo não suportado é recusado com erro
claro antes de iniciar a sessão. Hoje Claude e Codex suportam os três, com mapeamentos
diferentes:

| Modo | Claude Code | Codex |
| --- | --- | --- |
| `manual` | `--permission-mode manual` | aprovação `on-request`, sandbox `workspace-write` |
| `auto` | `--permission-mode auto` | aprovação `on-request`, revisor `auto_review`, sandbox `workspace-write` |
| `plan` | `--permission-mode plan` | aprovação `on-request`, sandbox `read-only` e modo de colaboração `plan` |

No Codex, `auto` encaminha pedidos de acesso além do sandbox à revisão automática da CLI,
como o one-shot `--approve-for-me`. O revisor pode aprovar ou negar conforme o risco;
não equivale a acesso irrestrito. Requer uma CLI que confirme `approvalsReviewer=auto_review`.
A mudança vale para sessões novas; sessões abertas mantêm a configuração original.

No Codex, perguntas do agente ao usuário (input) só aparecem no modo `plan`. Nenhum modo
concede acesso irrestrito (`full`), e o D.A.N.T.E. nunca escolhe um modo por inferência.
`/permissions` continua disponível como interface de baixo nível e lê e grava o mesmo
padrão do `/mode`. As execuções one-shot (`/claude`, `/codex`) não usam modos.

## Repositório ativo

Cada usuário autorizado pode manter um repositório ativo para as próximas mensagens, sem
repetir `@alias`:

```text
/use @fitness_backend
```

```text
Implemente a issue 500.
```

A mensagem acima, assim como `/claude <prompt>` e `/codex <prompt>` sem alias, roda em
`@fitness_backend`. Um `@alias` explícito vale só para aquela execução:

```text
/codex @dante revise o README
```

Depois disso, o repositório ativo continua sendo `@fitness_backend`.

```text
/use            consulta o contexto ativo (General ou @alias)
/use general    volta ao General Mode
```

`/use @alias` só aceita repositórios cadastrados e não inicia job. O contexto é por
usuário e sobrevive a reinícios. Como no `/agent set`, uma sessão já aberta continua no
contexto em que começou, e a resposta indica `/session start` para conversar no novo
contexto. `/repo remove` limpa o repositório ativo de quem o usava;
se um repositório ativo deixar de existir por outro motivo, o D.A.N.T.E. recusa a
execução e pede `/use @alias` ou `/use general`, em vez de cair para General Mode.

## Conversa contínua e troca pontual de agente

Agente padrão e repositório ativo definem a conversa; `/claude`, `/codex` e um `@alias`
explícito são overrides de **uma execução** e não mudam nenhum dos dois.

1. Escolha o agente padrão:

   ```text
   /agent set claude
   ```

2. Escolha o repositório ativo:

   ```text
   /use @fitness_backend
   ```

3. Converse normalmente; a mensagem abre uma sessão do Claude em `@fitness_backend`:

   ```text
   Implemente a issue 500.
   ```

4. Peça uma revisão pontual ao Codex, como execução avulsa:

   ```text
   /codex revise o PR
   ```

   ```text
   Codex iniciado. Job ID: J000001 (@fitness_backend).
   Override só desta execução: o agente padrão continua Claude.
   ```

5. A próxima mensagem volta para a conversa com o Claude, na mesma sessão:

   ```text
   Agora corrija o que o Codex apontou.
   ```

Um `@alias` diferente do ativo avisa da mesma forma (`o contexto ativo continua
@fitness_backend`). Para conferir a qualquer momento:

```text
/agent     agente padrão e, havendo, a sessão que recebe as mensagens comuns
/use       contexto ativo e, havendo, a sessão que recebe as mensagens comuns
/status    agente e contexto de cada job e sessão
```

Um alias explícito desconhecido é recusado com `Repositório @alias não cadastrado`, e um
repositório ativo que deixou de existir é recusado com a instrução `/use @alias` ou
`/use general`; em nenhum dos casos o agente é iniciado.

---

## Imagens e prints

Fotos e prints enviados ao bot, inclusive como arquivo e em álbum, chegam ao agente junto com o pedido.

Com legenda, a legenda é o pedido: a foto (ou o álbum inteiro) vira um turno da conversa, como uma mensagem
comum — na sessão ativa ou abrindo uma nova (AD-23), e na fila se o agente ainda estiver respondendo. Uma legenda
`/claude [@alias] …` ou `/codex …` roda como execução avulsa com as imagens; outra legenda iniciada por `/` é
recusada e as imagens ficam pendentes.

Sem legenda, as imagens ficam **pendentes** do usuário, no contexto atual da conversa, até o pedido em texto:

```text
Recebi 2 imagens.
Envie o pedido em texto: as imagens vão junto com a próxima mensagem. Sem pedido, elas são apagadas em 10 min.
```

```text
Compare as duas telas e diga o que mudou.
```

A próxima mensagem que chega a um agente — mensagem comum, `/steer`, `/claude` ou `/codex` — leva todas as
imagens pendentes, na ordem em que foram enviadas. Claude recebe as imagens no próprio turno (sessão) ou as abre
com a ferramenta `Read` (one-shot, com acesso só ao diretório dos anexos); Codex as recebe como imagens locais do
turno, do steer ou do `codex exec -i`. Se o agente não puder receber imagens, a mensagem é recusada sem ser
enviada: a imagem nunca vira só um nome de arquivo, e o D.A.N.T.E. não troca de agente nem de modelo.

- formatos: JPEG, PNG, GIF e WebP, conferidos pelo conteúdo do arquivo; até 7 MB e 8000 px por lado;
- até 10 imagens e 20 MB pendentes por usuário; um álbum recebe uma única confirmação;
- voz, áudio e vídeo seguem as mesmas regras de pendência (ver [Áudio e vídeo](#áudio-e-vídeo)); outros tipos de
  arquivo e animações (GIF) são recusados com aviso, sem download;
- `/status` mostra as imagens pendentes; trocar de contexto (`/use`, `/agent set`, `/session`) as descarta com
  aviso, e após 10 min sem pedido elas são apagadas;
- os arquivos ficam em `~/.dante/attachments/<usuário>/`, com acesso só do dono: pendentes em `pending/`, e
  depois no diretório da sessão ou do job que os usa (`S000001/`, `J000001/`), apagados quando a sessão é
  encerrada ou o job termina; sobras com mais de 24 h são removidas quando o Worker inicia.

## Áudio e vídeo

Nenhuma das CLIs ouve áudio ou assiste vídeo. O D.A.N.T.E. processa esses arquivos **localmente**, antes de o
turno chegar ao agente, e envia o conteúdo derivado:

- **voz e áudio**: transcrição automática pelo `whisper.cpp`, com timestamps por trecho e o idioma detectado;
- **vídeo e vídeo redondo (video note)**: até 6 quadros amostrados em intervalos iguais, enviados como imagens,
  mais a transcrição da trilha de áudio.

O agente recebe o seu pedido seguido de um bloco que diz de onde veio cada parte e o que **não** foi analisado:

```text
[Anexos processados localmente pelo D.A.N.T.E.: o agente não recebe os arquivos de áudio ou vídeo, só o conteúdo
derivado abaixo. Transcrições são automáticas e podem conter erros.]
Áudio 1, duração 0:42. Transcrição (whisper.cpp, idioma detectado: pt):
[0:00–0:05] Preciso que você revise o relatório de vendas.
Vídeo 1 (demo.mp4), duração 1:00. 6 quadros amostrados, nas imagens 1 a 6 (em 0:05, 0:15, 0:25, 0:35, 0:45,
0:55). O que acontece entre os quadros não foi visto. Trilha de áudio: Transcrição (...)
```

Como as imagens, voz, áudio e vídeo sem legenda ficam pendentes até o pedido em texto; com legenda, a legenda é o
pedido. A próxima mensagem comum, `/claude` ou `/codex` os leva. `/steer` não leva áudio nem vídeo: com algum
pendente, a orientação é recusada e eles continuam esperando a próxima mensagem comum.

Na conversa, o turno começa na hora e mostra `→ Processando 1 áudio localmente (transcrição)`; mensagens enviadas
enquanto isso entram na fila, e `/session stop` cancela o processamento. Na execução avulsa, o processamento faz
parte do job, e `/cancel <jobId>` o interrompe.

Limites:

- até 20 MB por arquivo (limite de download do Telegram), conferido antes do download;
- até 10 min de cada áudio ou vídeo são transcritos e amostrados; o que passar disso é declarado como análise
  parcial;
- até 10 imagens por mensagem, contando os quadros: se não couberem, a mensagem diz que nenhum quadro foi enviado;
- até 10 min de processamento por mensagem; passou disso, as ferramentas são encerradas e o turno ou o job falha
  com o motivo;
- formatos conferidos pelo conteúdo: OGG/Opus, MP3, M4A, WAV, FLAC e WebM para áudio; MP4, MOV, WebM e OGG para
  vídeo.

O arquivo original fica no diretório da sessão ou do job, como as imagens, e é apagado com ele; os quadros
também. O áudio intermediário (WAV) e o JSON do `whisper.cpp` são apagados logo após a transcrição.

### Ferramentas

Nada é instalado ou contratado automaticamente. Sem as ferramentas, voz, áudio e vídeo são recusados **antes do
download**, com o que falta:

```bash
sudo apt install ffmpeg whisper.cpp
mkdir -p ~/.dante/models
curl -L -o ~/.dante/models/ggml-small.bin \
  https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin
```

- `ffmpeg` e `ffprobe` leem o arquivo e extraem quadros e áudio; sozinhos, bastam para os quadros dos vídeos (a
  trilha de áudio fica sem transcrição, e a mensagem diz isso);
- `whisper-cli` (pacote `whisper.cpp`) transcreve com o modelo em `~/.dante/models/ggml-small.bin`, ou no caminho
  absoluto de `DANTE_WHISPER_MODEL`. O modelo `small` é multilíngue e, na CPU, leva cerca de 25 s por minuto de
  fala.

As ferramentas rodam sem shell, com argumentos fixos, ambiente mínimo e acesso só a arquivos locais (AD-03);
áudio e vídeo nunca ampliam as permissões do agente nem o modo da sessão.

## Arquivos produzidos pelos agentes

Um arquivo só chega ao Telegram por um canal explícito; um caminho citado na resposta do agente nunca vira envio.

- **Imagem gerada pelo Codex numa sessão**: o Codex informa onde salvou a imagem, e ela é enviada sozinha. Só é
  aceita de `~/.codex/generated_images` (ou `$CODEX_HOME/generated_images`).
- **`/send <caminho>`**: envia um arquivo do diretório da sessão ativa (o repositório ou o workspace geral),
  com caminho relativo a ele:

```text
/send relatorios/vendas.csv
```

Cada arquivo recebe um id (`F000001`) na legenda. Imagens chegam como foto (prévia) e como documento (o original, sem
compressão); os demais arquivos, inclusive áudio e vídeo, como documento.

O arquivo é recusado, sem envio, quando:

- o caminho real, com links simbólicos resolvidos, sai do diretório permitido;
- não é um arquivo comum, está vazio ou passa de 50 MB;
- parece credencial ou configuração sensível (`.env*`, chaves `id_*`, `*.pem`, `*.key`, `.git/`, `.ssh/`,
  `.aws/`, `.netrc` e similares);
- a sessão tem segredos vinculados ao ambiente: arquivos binários não podem ser filtrados, então nenhum sai dela.

O envio fica em `/status` (`Entregas Telegram`). Se falhar, o bot avisa, e `/resend F000001` tenta de novo sem rodar o
agente: o D.A.N.T.E. guarda uma cópia em `~/.dante/artifacts/<usuário>/` no momento do envio, mantida enquanto o
registro está na janela de reenvio (os 50 arquivos mais recentes). Ao reiniciar o Worker, os registros e as cópias
são descartados.

---

# Jobs

Cada chamada a `/claude` ou `/codex` gera um job: uma execução one-shot, que inicia a CLI,
espera o resultado final e encerra. Mensagens comuns são conversa em sessão interativa e
não geram job.

Ao iniciar:

```text
Codex iniciado. Job ID: J000001 (General).
```

Ou:

```text
Claude iniciado. Job ID: J000002 (@fitness_backend).
```

## Consultar jobs

```text
/status
```

Exemplo:

```text
J000001 Codex General: Succeeded | criado 2026-09-28 12:00:00 UTC
J000002 Claude @fitness_backend: Running | criado 2026-09-28 12:03:00 UTC
```

Os estados possíveis são:

- `Queued`
- `Running`
- `Succeeded`
- `Failed`
- `Cancelled`

O histórico mantém os 20 jobs concluídos mais recentes em memória.

Ele é perdido quando o Worker reinicia.

## Cancelar

```text
/cancel J000002
```

O cancelamento é propagado ao processo e o D.A.N.T.E. encerra a árvore de processos iniciada pelo agente.

---

# Comandos disponíveis

| Comando | Descrição |
| --- | --- |
| `/ping` | Verifica se o bot está respondendo |
| `/help [comando]` | Mostra comandos por categoria, descrições e exemplos fictícios; `/help session` ou `/help /repo` detalha um comando |
| `/claude <prompt>` | Executa Claude uma vez (one-shot) no contexto ativo, sem alterar o agente padrão |
| `/codex <prompt>` | Executa Codex uma vez (one-shot) no contexto ativo, sem alterar o agente padrão |
| `/claude @alias <prompt>` | Executa Claude uma vez em um repositório, sem alterar o contexto ativo |
| `/codex @alias <prompt>` | Executa Codex uma vez em um repositório, sem alterar o contexto ativo |
| `/repos` | Lista repositórios cadastrados |
| `/repo add @alias <path> [owner/repo]` | Cadastra um repositório |
| `/repo show @alias` | Exibe um repositório |
| `/repo remove @alias` | Remove um repositório |
| `/repo env set @alias KEY VALUE` | Define configuração literal não sensível |
| `/repo env bind @alias KEY HOST_ENV` | Vincula uma variável a uma variável do host |
| `/repo env list @alias` | Lista nomes e origens das variáveis |
| `/repo env remove @alias KEY` | Remove uma configuração de ambiente |
| `/agent` | Exibe o agente padrão e a sessão ativa que recebe as mensagens comuns |
| `/agent set claude\|codex` | Altera o agente padrão |
| `<mensagem>` | Conversa: novo turno da sessão ativa; sem sessão ativa, abre uma com o agente padrão no contexto ativo |
| `/use` | Exibe o contexto ativo do usuário e a sessão ativa que recebe as mensagens comuns |
| `/use @alias` | Define o repositório ativo |
| `/use general` | Volta ao General Mode |
| `/status` | Exibe jobs, sessões próprias e estado da entrega recente ao Telegram |
| `/cancel <jobId>` | Solicita cancelamento de um job |
| `/session start [claude\|codex] [@alias] [manual\|auto\|plan] [model=<modelo>] [effort=<nível>]` | Inicia e seleciona uma sessão interativa; usa agente, contexto e modo selecionados se omitidos |
| `/session list` | Lista as suas sessões |
| `/session select <id\|none>` | Seleciona uma sessão; `none` faz a próxima mensagem abrir uma sessão nova |
| `/session stop [id]` | Interrompe o turno e descarta a fila, mantendo a sessão |
| `/session close [id]` | Encerra a sessão e seu processo |
| `/model` | Consulta os modelos padrão de Claude e Codex para o usuário |
| `/model claude\|codex [<modelo>\|default]` | Lista modelos da CLI instalada, escolhe um modelo ou volta ao padrão da CLI |
| `/effort` | Consulta esforço e níveis suportados por agente/modelo |
| `/effort claude\|codex [<nível>\|default]` | Escolhe o esforço das novas sessões e execuções one-shot ou volta ao padrão da CLI |
| `/mode` | Consulta o modo padrão, as opções e o suporte de cada agente |
| `/mode manual\|auto\|plan` | Escolhe o modo padrão para novas sessões do usuário (`approval` = `manual`) |
| `/mode session manual\|auto\|plan` | Troca somente na sessão ativa ociosa; Claude confirma agora, Codex no próximo turno |
| `/permissions [manual\|auto\|plan]` | Interface de baixo nível do `/mode`: consulta ou escolhe o mesmo padrão |
| `/approve <sessionId> <turnId> <requestId>` | Aprova a ação solicitada uma vez |
| `/approve-session <sessionId> <turnId> <requestId>` | Aprova para a sessão quando o agente oferece essa opção |
| `/deny <sessionId> <turnId> <requestId> [motivo]` | Nega a ação solicitada |
| `/input <sessionId> <turnId> <requestId> <resposta1> [ \| <resposta2> ...]` | Fallback técnico para responder às perguntas; prefira botões ou Reply à mensagem do pedido |
| `/steer <orientação>` | Orienta imediatamente o turno da sessão ativa; no Claude, interrompe o turno e prioriza a orientação |
| `/resend <jobId\|sessionId[/turnId]\|arquivo>` | Reenvia as partes pendentes da saída recente ou de um arquivo (`F000001`), sem executar o agente novamente |
| `/send <caminho>` | Envia um arquivo do diretório da sessão ativa |

Durante um turno interativo, mensagens comuns entram na fila. A sessão mantém o mesmo agente e
repositório até ser encerrada, mesmo que `/agent set` ou `/use` mudem depois. Eventos são
agrupados por cerca de 750 ms antes do envio, em mensagens de até 4000 caracteres; a saída da
sessão ativa chega sem prefixo e a de qualquer outra sessão é identificada por `[S…]` — inclusive
quando você troca de sessão no meio de um turno: a partir da troca, a anterior passa a aparecer
com `[S…]`. Partes de um mesmo turno saem com pelo menos 1,5 s entre si. Uma
falha de entrega aparece em `/status` separadamente do resultado da execução; `/resend` tenta
novamente as partes ainda não entregues. Resultados recentes ficam em memória enquanto o Worker
está vivo.

O modo `manual` é o padrão recomendado; os modos estão descritos em
[Modos de trabalho](#modos-de-trabalho). O acesso `full` não é oferecido, e o fluxo one-shot
continua independente dessas escolhas.

Pedidos de aprovação mostram os IDs da sessão, turno e solicitação. Perguntas de input usam
botões ou Reply sem exigir esses IDs; nos transportes sem suporte, mostram `/input` com os
IDs correlacionados. Uma solicitação pendente aparece em `/status` e expira após cinco minutos; a
aprovação é negada ao expirar e uma resposta de input vazia é enviada ao agente. Respostas de
outro usuário, duplicadas, tardias ou para outro turno são rejeitadas. Com segredos vinculados ao
ambiente do repositório, o bot omite detalhes da ação e das perguntas, sem oferecer opções
que revelem segredos. Reply, botões e `/input` mantêm a validação do dono, turno e request;
Reply e botões de input também conferem o chat e a mensagem original. Reply a mensagem
não correlacionada é recusado com orientação, sem resolver um request ou abrir novo turno.

---

# Exemplo completo

Cadastro inicial:

```text
/repo add @dante /mnt/c/Repos/dante juanverass/dante
/repo add @fitness_backend /mnt/c/Repos/fitnessapp_backend juanverass/fitnessapp_backend
```

Confira:

```text
/repos
```

Conversa no repositório:

```text
/use @dante
```

```text
Leia a issue #68 e me diga por onde começar.
```

```text
Pode seguir com o primeiro passo.
```

Se o agente pedir aprovação, a mensagem traz os comandos prontos:

```text
Aprovação pendente S000001 T000002 R000001: git push origin feat/issue-68
/approve S000001 T000002 R000001
/deny S000001 T000002 R000001 [motivo]
```

Encerrar a conversa:

```text
/session close
```

Pergunta geral avulsa (one-shot):

```text
/claude explique arquitetura hexagonal de forma simples
```

Trabalho no D.A.N.T.E.:

```text
/codex @dante revise o RepositoryRegistry e identifique possíveis problemas
```

Trabalho no fitness backend:

```text
/claude @fitness_backend analise as issues abertas e me explique a próxima tarefa
```

Acompanhar:

```text
/status
```

Cancelar se necessário:

```text
/cancel J000004
```

---

# Segurança

O D.A.N.T.E. foi desenhado para controlar agentes, não para fornecer um shell remoto.

Principais proteções atuais:

- somente Telegram User IDs autorizados executam comandos;
- prompts são tratados como dados;
- argumentos são enviados usando `ProcessStartInfo.ArgumentList`;
- `UseShellExecute=false`;
- executáveis suportados são definidos pelo D.A.N.T.E.;
- o usuário não escolhe arbitrariamente executáveis ou argumentos internos;
- paths de repositório são validados;
- General Mode é isolado dos repositórios cadastrados;
- ambientes de repositórios são aplicados somente ao processo filho;
- bindings de secrets não armazenam o valor no catálogo;
- valores de `OPENAI_API_KEY` e `ANTHROPIC_API_KEY`, quando existentes, são mascarados nas respostas do Telegram,
  inclusive em cada evento de sessão interativa e quando o valor chega dividido entre partes do streaming;
- sessões e jobs com segredos vinculados ao ambiente do repositório omitem a saída do agente;
- aprovações e respostas de input só são aceitas do dono da sessão, no turno e na solicitação certos, uma vez;
- o perfil de acesso irrestrito (`full`) não é oferecido;
- cancelamento encerra a árvore do processo.

Evite enviar qualquer segredo diretamente pelo Telegram.

---

# Persistência

Atualmente:

| Informação | Persistência |
| --- | --- |
| Configurações do assistente (agente padrão, repositório ativo e modo padrão por usuário) | `~/.dante/settings.json` |
| Repositórios cadastrados | `~/.dante/repositories.json` |
| Perfis de ambiente | `~/.dante/repositories.json` |
| Valores de bindings secretos | não são persistidos |
| Workspace geral | `~/.dante/workspaces/general` |
| Jobs | somente memória |
| Histórico de jobs | somente memória |
| Sessões interativas, turnos, filas e solicitações pendentes | somente memória (perdidas ao reiniciar o Worker) |
| Saídas recentes para `/resend` | somente memória |
| Imagens recebidas | `~/.dante/attachments/<usuário>/` enquanto pendentes e até o fim da sessão ou do job que as usa; registro somente em memória |
| Arquivos enviados ao Telegram | cópia em `~/.dante/artifacts/<usuário>/` enquanto o registro de entrega existe; registro somente em memória, cópias apagadas ao reiniciar |

A seleção de modelo é independente por usuário e agente:

```text
/model                        consulta as preferências de Claude e Codex
/model claude                 lista modelos oferecidos pelo Claude instalado
/model codex                  lista modelos oferecidos pelo Codex instalado
/model claude <modelo>        escolhe um modelo da lista
/model codex default          volta ao padrão da CLI
/session start claude model=<modelo>    override só para essa sessão
/session start codex model=default      usa o padrão da CLI nessa sessão
```

O modelo fica fixo durante toda a sessão e aparece em `/status` e `/session list`.
Mudar `/model` afeta as próximas conversas e execuções one-shot: `/claude` usa a
preferência de Claude; `/codex`, a de Codex. O override de `/session start` não altera
a preferência salva. Sem escolha, nenhum modelo é passado e a CLI usa seu padrão;
quando o Codex informa esse modelo, `/status` também mostra o nome reportado.

O catálogo vem das próprias CLIs, sem iniciar um turno: `initialize` do Claude e
`model/list` do Codex. Respostas válidas ficam em cache por até dez minutos. Modelos
inválidos, de outro agente ou indisponíveis são recusados antes da execução. Se uma
atualização da CLI remover um modelo salvo, novas execuções são recusadas após a
renovação do catálogo: escolha outro modelo listado ou use `/model <agente> default`.
Não há troca silenciosa para outro modelo. Se a consulta à CLI falhar, a seleção
explícita é recusada; voltar a `default` continua disponível sem consultar o catálogo.

O esforço de raciocínio também é independente por usuário e agente:

```text
/effort                              consulta preferências e opções por modelo
/effort claude                       lista os níveis do modelo escolhido para Claude
/effort codex <nível>                 escolhe um nível oferecido pelo Codex
/effort claude default               remove a preferência e usa o padrão nativo
/session start claude model=<modelo> effort=<nível>   override só para a sessão
/session start codex effort=default   ignora a preferência de esforço nesta sessão
```

Os nomes e o suporte vêm do catálogo da CLI instalada, por modelo; não há tradução
ou equivalência de níveis entre Claude e Codex. Sem modelo escolhido, a validação usa
o modelo que o catálogo identifica como padrão. Se não for possível identificá-lo,
escolha um modelo com `/model` antes de escolher esforço. Modelos sem suporte não
oferecem níveis explícitos. Ausência de escolha ou `default` omite o parâmetro de
esforço e conserva o padrão nativo da CLI.

O esforço fica fixo na sessão e aparece em `/status` e `/session list`. Alterar
`/effort` só afeta novas sessões e jobs one-shot: `/claude` e `/codex` usam a preferência
do agente nomeado. O override não muda a preferência persistida. Permissões e modo
`manual|auto|plan` continuam independentes; esforço não concede acesso adicional.

A combinação modelo × esforço é validada antes de iniciar a execução. Se mudar o
modelo ou atualizar a CLI tornar o esforço salvo incompatível, novas execuções são
recusadas após a renovação do catálogo, sem conversão silenciosa: escolha outro nível
ou `/effort <agente> default`. Falha na consulta também recusa esforço explícito;
voltar a `default` funciona sem consultar a CLI. Sessões existentes mantêm seu esforço.

`~/.dante/settings.json` guarda apenas preferências, nunca tokens ou segredos:

```json
{
  "DefaultAgent": "Claude",
  "ActiveRepositories": {
    "123456789": "@fitness_backend"
  },
  "SessionModes": {
    "123456789": "auto"
  },
  "Models": {
    "123456789": {
      "Claude": "opus"
    }
  },
  "Efforts": {
    "123456789": {
      "Claude": "high"
    }
  }
}
```

Enquanto o arquivo não existe, o agente padrão é **Claude**; o arquivo é criado na
primeira alteração, com escrita atômica. `DefaultAgent` aceita somente `Claude` ou
`Codex`; `ActiveRepositories` associa Telegram User IDs a aliases; `SessionModes`, quando
existe, associa Telegram User IDs ao modo padrão (`manual`, `auto` ou `plan`).
`Models` associa cada usuário às preferências separadas de `Claude` e `Codex`; ausência
da entrada significa padrão da CLI. `Efforts` guarda os níveis escolhidos no mesmo
formato por usuário e agente. A carga valida a sintaxe; a disponibilidade é
conferida no catálogo ao selecionar e ao iniciar uma execução. Arquivo corrompido
ou com valor desconhecido impede o Worker de iniciar com erro claro, em vez de
escolher um agente por conta própria.

---

# Arquitetura atual

```text
Telegram
   ↓
TelegramPollingService
   ↓
Command Parser
   ├──────────────→ RepositoryRegistry
   │                   ↓
   │              Repository Mode
   │
   └──────────────→ GeneralWorkspace
                       ↓
                  General Mode

Context resolvido
   ├─ mensagem comum ──→ SessionRegistry
   │                        ↓
   │                   ClaudeSessionDriver (stream-json) / CodexSessionDriver (app-server)
   │                        ↓
   │                   InteractiveAgentProcess (um processo vivo por sessão)
   │                        ↓ eventos
   │                   TelegramDeliveryService → Telegram
   │
   └─ /claude, /codex ──→ JobRegistry
                            ↓
                       ClaudeRunner / CodexRunner
                            ↓
                       AgentProcessExecutor
                            ↓
                       Claude Code / Codex CLI
```

Responsabilidades principais:

- **TelegramPollingService** — recebe comandos e responde ao Telegram;
- **TelegramUserAuthorizer** — controla usuários permitidos;
- **RepositoryRegistry** — mantém aliases, paths e ambientes dos projetos;
- **GeneralWorkspace** — fornece o workspace neutro;
- **SessionRegistry** — sessões interativas por usuário: dono, contexto fixo, turnos, fila e solicitações;
- **ClaudeSessionDriver / CodexSessionDriver** — traduzem os protocolos estruturados das CLIs em eventos neutros;
- **TelegramDeliveryService** — agrupa, redige e entrega eventos e resultados, com retry e `/resend`;
- **JobRegistry** — controla estado, contexto e cancelamento dos jobs;
- **ClaudeRunner / CodexRunner** — definem como cada CLI é iniciada;
- **AgentProcessExecutor** — executa processos sem shell e captura stdout/stderr.

---

# Limitações atuais

Ainda não fazem parte do projeto:

- worktrees automáticos por issue;
- seleção automática de issues pelo D.A.N.T.E.;
- fluxo automático issue → implementação → review → merge;
- handoff automático entre Claude, Codex e Tech Lead;
- fila persistente;
- persistência de jobs em banco de dados;
- execução concorrente isolada por worktree;
- comando `/ask` com agente padrão;
- sessões interativas persistentes: reiniciar o Worker encerra as conversas;
- botões inline para aprovação (os comandos textuais estão disponíveis);
- perfil de acesso irrestrito (`full`);
- pergunta do Codex ao usuário (input) fora do perfil `plan`, por limitação do `app-server`;
- áudio e vídeo chegam ao agente só como transcrição e quadros amostrados, nunca como o arquivo original; imagens
  geradas só são enviadas sozinhas nas sessões do Codex.

Esses pontos são candidatos naturais para os próximos MVPs.

---

# Desenvolvimento

Build:

```bash
dotnet build Dante.sln
```

Testes:

```bash
dotnet test Dante.sln
```

Executar Worker:

```bash
dotnet run --project src/Dante.Worker
```

Configurações locais e segredos não devem ser versionados.

## Desenvolvimento com agentes

Claude Code, Codex e desenvolvedores humanos seguem o mesmo
[contrato de desenvolvimento](docs/development/agent-contract.md). `CLAUDE.md` e
`AGENTS.md` são adaptadores finos que apontam para ele. O contexto persistente do
projeto (visão, estado atual, decisões e histórico) está em [`docs/context/`](docs/context/).

### Agent Harness

O Agent Harness é o conjunto de regras e registros que permite a qualquer agente
iniciar, continuar ou revisar uma Issue sem depender do histórico de uma conversa. Tudo
o que o próximo worker precisa vem de fontes persistidas:

| Fonte | O que informa |
| --- | --- |
| Labels `status:*` da Issue | em que fase a Issue está ([backlog](docs/development/backlog.md)) |
| Comentários `## TURNO ASSUMIDO`, `## HANDOFF`, `## TURNO FINALIZADO` na Issue | quem está no turno, branch, checkpoint, próximos passos e Decision Locks ([turnos](docs/development/handoff.md)) |
| Comentários `## REVIEW` no PR | veredito e correções solicitadas ([review](docs/development/review.md)) |
| Git e testes | o estado real do código — vence qualquer registro divergente |

Regra fixa: `1 Issue → 1 branch → 1 PR`, quantos turnos e agentes forem necessários.

#### Iniciar trabalho

Trabalho novo só sai de Issues `status:ready` que não sejam Epic nem tenham dependência
aberta:

```bash
gh issue list --state open --label status:ready
gh issue edit <numero> --remove-label status:ready --add-label status:in-progress
git switch -c <prefixo>/issue-<numero>-<slug>
gh issue comment <numero> --body-file <claim>   # ## TURNO ASSUMIDO, antes de alterar arquivos
```

Depois: baseline (`dotnet build` + `dotnet test`), implementação dentro do escopo,
validação e staging seletivo, conforme o [contrato](docs/development/agent-contract.md).

#### Continuar trabalho

Para uma Issue `status:in-progress`, use a skill `$continuar-turno`: ela recupera o
[handoff](docs/development/handoff.md#continuar-turno) nos comentários da Issue,
confirma ownership, branch e PR, compara o checkpoint com o Git e publica um novo
`## TURNO ASSUMIDO` antes de qualquer alteração. Se não houver handoff confiável, siga
o [RECOVERY MODE](docs/development/handoff.md#recovery-mode) preservando o trabalho
recebido. Em `status:review`, só continue se o PR pedir correção; use a mesma branch e
o mesmo PR.

#### Encerrar trabalho

Use a skill `$encerrar-turno` e registre a validação real. Se o trabalho estiver
inacabado, faça checkpoint e push, depois publique `## HANDOFF` na Issue. Se a
implementação estiver concluída, abra o PR, mova a Issue para `status:review` e publique
`## TURNO FINALIZADO` na Issue. Os [registros de turno](docs/development/handoff.md#onde-vivem-os-registros)
ficam nos comentários da Issue, nunca em arquivos do repositório.
