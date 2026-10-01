# Operação e debugging do D.A.N.T.E.

Este documento é o manual operacional do projeto.

---

# 1. Pré-requisitos

No mesmo usuário e ambiente em que o Worker roda:

- .NET 10 SDK;
- Git;
- acesso à internet;
- Claude Code e/ou Codex CLI instalados;
- CLIs autenticadas;
- token de bot do Telegram;
- Telegram User ID autorizado.

Valide as CLIs diretamente antes de culpar o D.A.N.T.E.

Exemplos:

```bash
which claude
which codex
claude --version
codex --version
```

---

# 2. Configuração local persistente

O D.A.N.T.E. usa dois grupos de configuração:

1. variáveis de ambiente;
2. arquivos em `~/.dante`.

## Variáveis principais

```text
Telegram__BotToken
Telegram__AllowedUserIds
DANTE_GENERAL_WORKSPACE   (opcional)
OPENAI_API_KEY            (opcional)
ANTHROPIC_API_KEY         (opcional)
```

As API keys não são necessárias quando as próprias CLIs já estão autenticadas por login local.

---

# 3. Por que `export` desaparece?

Ao executar:

```bash
export Telegram__BotToken="..."
```

a variável é adicionada ao ambiente daquele shell e dos processos filhos dele.

Ao fechar o terminal, aquele shell deixa de existir.

Um novo terminal começa com outro ambiente.

Portanto:

> `export` sozinho não é persistência.

---

# 4. Configuração para execução manual no WSL

Para rodar como serviço (systemd do usuário + inicialização no logon do Windows), siga
[Executando como serviço](../../README.md#executando-como-servi%C3%A7o). O serviço lê
`~/.config/dante/dante.env` (formato `CHAVE=valor`, sem `export`); `deploy/dante-service.sh install` o cria
a partir do `~/.config/dante/env` abaixo, se ele existir.

Crie:

```bash
mkdir -p ~/.config/dante
nano ~/.config/dante/env
```

Conteúdo:

```bash
export Telegram__BotToken="SEU_TOKEN"
export Telegram__AllowedUserIds="SEU_ID"
```

Proteja:

```bash
chmod 600 ~/.config/dante/env
```

Carregue no `~/.bashrc`:

```bash
if [ -f "$HOME/.config/dante/env" ]; then
    source "$HOME/.config/dante/env"
fi
```

Recarregue:

```bash
source ~/.bashrc
```

Teste o token sem imprimi-lo:

```bash
echo "${Telegram__BotToken:+TOKEN_OK}"
echo "$Telegram__AllowedUserIds"
```

Nunca registre o token no Git.

O serviço carrega os secrets de `~/.config/dante/dante.env` (permissão `600`), nunca do repositório.

---

# 5. Arquivos persistidos pelo D.A.N.T.E.

## Settings

```text
~/.dante/settings.json
```

Contém preferências como:

- default agent;
- active repositories por usuário;
- session mode;
- model por agente;
- effort por agente.

## Repositórios

```text
~/.dante/repositories.json
```

Contém:

- alias;
- path;
- GitHub opcional;
- configuração de ambiente.

Secrets ligados por host binding não são gravados como valor.

## Workspace geral

Por padrão:

```text
~/.dante/workspaces/general
```

Pode ser substituído por:

```text
DANTE_GENERAL_WORKSPACE
```

O path precisa ser absoluto.

---

# 6. Inicialização manual

Na raiz do repositório:

```bash
dotnet build Dante.sln
dotnet test Dante.sln
dotnet run --project src/Dante.Worker
```

A primeira validação remota deve ser:

```text
/ping
```

Resposta esperada:

```text
pong
```

---

# 7. O que o log “DANTE iniciado” realmente prova?

Somente que:

- o Host subiu;
- o `Worker` iniciou.

Não prova que:

- BotToken existe;
- allowlist está correta;
- Telegram long polling funciona;
- Claude funciona;
- Codex funciona.

`TelegramPollingService` é um hosted service separado.

---

# 8. Diagnóstico em camadas

Use sempre esta ordem.

## Camada A — processo está vivo?

```bash
pgrep -af 'Dante.Worker|dotnet run'
```

Esperado: uma instância intencional.

Se houver duas instâncias usando o mesmo token, ambas podem competir pelo long polling.

---

## Camada B — configuração existe?

```bash
echo "${Telegram__BotToken:+TOKEN_OK}"
echo "$Telegram__AllowedUserIds"
```

Esperado:

```text
TOKEN_OK
<seu user id>
```

Se não houver token, o polling é desativado.

---

## Camada C — Telegram responde `/ping`?

Se não:

investigue somente:

- BotToken;
- AllowedUserIds;
- polling;
- conectividade;
- instâncias duplicadas;
- erros HTTP da Bot API.

Ainda não faz sentido investigar SessionRegistry ou drivers.

---

## Camada D — comandos administrativos respondem?

Teste:

```text
/status
/agent
/use
/session
```

Se `/ping` responde e esses não, o problema já está no parsing/handler.

---

## Camada E — mensagem comum abre sessão?

Se comandos funcionam, mas conversa não:

investigue:

- `ConverseAsync`;
- contexto ativo;
- `RepositoryRegistry`;
- `AssistantSettingsStore`;
- `AgentModelCatalog`;
- `SessionRegistry.StartAsync`.

---

## Camada F — driver inicia?

Verifique:

```bash
which claude
which codex
```

e execute as CLIs diretamente.

Problemas típicos:

- CLI não está no PATH do usuário do Worker;
- autenticação existe no Windows, mas não no WSL;
- configuração da CLI pertence a outro usuário;
- versão da CLI mudou protocolo/capacidade.

---

## Camada G — agente trabalha mas Telegram não mostra?

Consulte:

```text
/status
```

O sistema exibe estado de entrega.

Se execução terminou e entrega está `Failed`, investigue:

- `TelegramDeliveryService`;
- formatação;
- HTTP;
- rate limit.

Use `/resend` quando houver delivery recente recuperável.

---

# 9. Logs importantes

## Token ausente

Mensagem:

```text
Telegram__BotToken não configurado; polling desativado.
```

Significa que o processo pode continuar vivo, mas o bot não recebe mensagens.

---

## Falha de polling

Formato:

```text
Falha no polling do Telegram (<Tipo>); tentando novamente.
```

O código deliberadamente não registra a URL completa porque ela contém o bot token.

---

## Falha de runner

Indica erro inesperado durante uma execução one-shot.

O detalhe sensível não deve ser enviado cru para o Telegram.

---

## Falha de delivery

Não conclua imediatamente que o agente falhou.

Delivery e execução têm estados separados.

---

# 10. Instâncias duplicadas

Verifique:

```bash
pgrep -af 'Dante.Worker|dotnet run'
systemctl --user is-active dante
```

Com o serviço ativo, pare-o com `systemctl --user stop dante` antes de rodar o Worker manualmente.

Para encerrar instâncias manuais:

```bash
pkill -f Dante.Worker
pkill -f "dotnet run"
```

Use com cuidado para não matar outro `dotnet run` que você queria manter.

Depois inicie somente uma instância do D.A.N.T.E.

---

# 11. Debug de repositórios

Liste pelo Telegram:

```text
/repos
```

Confira um alias:

```text
/repo show @dante
```

Problemas possíveis:

- alias malformado;
- path não absoluto;
- diretório inexistente;
- path não é exatamente a raiz Git;
- remote incompatível com `owner/repo`.

No terminal:

```bash
git -C /caminho/do/repo rev-parse --show-toplevel
git -C /caminho/do/repo remote -v
```

---

# 12. Debug de ambiente do repositório

Liste configuração sem revelar valor de secret:

```text
/repo env list @alias
```

Para binding:

```text
/repo env bind @alias OPENAI_API_KEY OPENAI_API_KEY
```

O primeiro nome é a variável que o processo filho verá.

O segundo é a variável existente no host.

Teste no host sem imprimir:

```bash
echo "${OPENAI_API_KEY:+OPENAI_API_KEY_OK}"
```

Se o binding apontar para variável ausente, o agente não inicia.

---

# 13. Debug de contexto

Perguntas:

1. existe `@alias` explícito?
2. qual `/use` está ativo?
3. já existe uma sessão ativa?
4. a sessão ativa foi aberta em qual contexto?

Uma sessão mantém seu contexto original.

Portanto:

```text
/use @repoA
mensagem → abre sessão A

/use @repoB
mensagem seguinte
```

não move a sessão A para B.

Para aplicar o novo contexto, abra uma nova sessão.

---

# 14. Debug de sessão

Comece por:

```text
/status
/session list
```

Observe:

- session ID;
- agent;
- contexto;
- state;
- active turn;
- queue;
- requests pendentes;
- modo;
- modelo;
- esforço;
- erro.

## Sessão `Failed`

É terminal.

Uma mensagem comum não deve criar silenciosamente uma sessão substituta enquanto a encerrada continua selecionada.

Abra outra explicitamente.

---

# 15. Debug de fila

Se o agente está respondendo e você envia outra mensagem comum:

a resposta:

```text
Recebido; envio ao agente quando a resposta atual terminar.
```

é comportamento esperado.

Não é travamento.

Use `/steer` apenas quando quiser alterar o trabalho corrente.

---

# 16. Debug de approval

Se os botões parecem mortos, verifique:

- request ainda está pendente?
- expirou?
- é a mensagem Telegram original?
- o usuário é dono?
- a sessão/turno continuam válidos?
- outra resposta já consumiu o request?

Clique duplicado é intencionalmente recusado.

Comandos textuais permanecem fallback.

---

# 17. Debug de formatação

Se uma mensagem HTML válida para o D.A.N.T.E. for recusada pela Bot API:

```text
TelegramMarkupException
      ↓
fallback plain text
```

O agente não é executado novamente.

Se o fallback também falhar, a delivery fica `Failed`.

---

# 18. Debug de processo órfão

Depois de fechar/interromper o Worker, procure processos inesperados:

```bash
pgrep -af 'claude|codex'
```

A infraestrutura tenta encerrar a árvore inteira.

Se houver órfão reproduzível, investigue:

- `InteractiveAgentProcess.TrackDescendants`;
- `ProcessTree`;
- timing de shutdown.

Há teste dedicado para esse comportamento.

---

# 19. Debug do caminho one-shot

Se:

```text
/claude ...
```

ou:

```text
/codex ...
```

falha, siga:

```text
TelegramPollingService
   ↓
ResolveModel
   ↓
JobRegistry.Create
   ↓
Runner
   ↓
AgentProcessExecutor
   ↓
AgentProcessStartInfo
```

Se a conversa funciona, mas one-shot não, o problema provavelmente está nesse caminho separado.

---

# 20. Debug do caminho interativo

Se one-shot funciona, mas conversa não:

```text
ConverseAsync
 ↓
SessionRegistry
 ↓
AgentSessionDriverFactory
 ↓
ClaudeSessionDriver/CodexSessionDriver
 ↓
InteractiveAgentProcess
```

Isso reduz muito a área de busca.

---

# 21. Arquivos corrompidos

Os stores são fail-closed.

Um JSON inválido pode impedir startup.

Confira:

```bash
cat ~/.dante/settings.json
cat ~/.dante/repositories.json
```

Não edite às cegas se houver configuração importante.

Faça backup antes:

```bash
cp ~/.dante/settings.json ~/.dante/settings.json.bak
cp ~/.dante/repositories.json ~/.dante/repositories.json.bak
```

---

# 22. Estratégia de debugging em IDE

Pontos de breakpoint úteis:

## Entrada Telegram

`TelegramPollingService.HandleMessageAsync`

## Conversa

`TelegramPollingService.ConverseAsync`

## Abertura

`SessionRegistry.StartAsync`

## Submit

`SessionRegistry.SubmitAsync`

## Estado

`AgentSession.Submit`
`AgentSession.Apply`

## Claude

`ClaudeSessionDriver.HandleAsync`

## Codex

`CodexSessionDriver.HandleAsync`

## Delivery

`TelegramDeliveryService.PublishAsync`
`TelegramDeliveryService.DeliverAsync`

## One-shot

`AgentProcessExecutor.ExecuteAsync`

---

# 23. Como saber se uma falha é estado, protocolo ou entrega?

Use esta regra:

### Estado

`AgentSession` / `SessionRegistry`

Sintomas:

- transição inválida;
- turno preso;
- fila errada;
- ownership;
- request incorreto.

### Protocolo

Driver Claude/Codex.

Sintomas:

- JSON inesperado;
- request upstream perdido;
- turn ID incorreto;
- CLI mudou contrato.

### Transporte/processo

`InteractiveAgentProcess`.

Sintomas:

- stdin fechado;
- stdout não chega;
- processo morre;
- filho órfão.

### Entrega

`TelegramDeliveryService` / `TelegramBotApi`.

Sintomas:

- agente terminou mas mensagem não chegou;
- ordem/formatação;
- retry;
- rate limit.

---

# 24. Rotina recomendada antes de deixar o D.A.N.T.E. sozinho

```bash
echo "${Telegram__BotToken:+TOKEN_OK}"
echo "$Telegram__AllowedUserIds"

which claude
which codex

pgrep -af 'Dante.Worker|dotnet run'
```

No Telegram:

```text
/ping
/status
```

Depois envie uma tarefa pequena.

Só então deixe o processo executando por longos períodos.

---

# 25. Limitação operacional atual

No estado documentado, o Worker ainda é iniciado manualmente.

A execução automática como serviço está separada como trabalho próprio.

Até isso ser implementado, abrir um terminal novo exige que o ambiente necessário esteja carregado — por isso a configuração persistente do shell é importante.
