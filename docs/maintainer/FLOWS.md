# Fluxos internos do D.A.N.T.E.

Este documento acompanha uma ação do usuário de ponta a ponta.

---

# 1. Fluxo de inicialização

Quando você executa:

```bash
dotnet run --project src/Dante.Worker
```

o fluxo é:

```text
Program.cs
  ↓
Host.CreateApplicationBuilder
  ↓
registra serviços
  ↓
builder.Build()
  ↓
host.Run()
  ↓
Hosted services iniciam
  ├─ Worker
  └─ TelegramPollingService
```

## Worker

Apenas mantém o processo vivo e registra lifecycle.

## TelegramPollingService

Começa o long polling se houver `Telegram__BotToken`.

Se o token estiver ausente:

```text
Host continua vivo
Telegram polling encerra
```

Por isso o processo pode parecer “rodando” sem o bot responder.

---

# 2. Fluxo de `/ping`

É o teste mais curto possível.

```text
Telegram
  ↓
TelegramBotApi.GetUpdatesAsync
  ↓
TelegramPollingService
  ↓
HandleMessageAsync
  ↓
detecta /ping
  ↓
SendReplyAsync
  ↓
TelegramBotApi.SendMessageAsync
  ↓
pong
```

Se `/ping` não funciona, não investigue Claude/Codex primeiro.

---

# 3. Fluxo de uma mensagem comum sem sessão ativa

Exemplo:

```text
revise o projeto e me diga o que falta
```

## Etapa 1 — entrada

`TelegramPollingService` recebe o update.

Valida:

```text
message.Text != null
AND
TelegramUserAuthorizer.IsAuthorized(message.From)
```

## Etapa 2 — não é slash command

A mensagem chega em `ConverseAsync`.

## Etapa 3 — procura sessão ativa

```text
SessionRegistry.GetActive(userId)
```

Não existe.

## Etapa 4 — resolve contexto

`AgentContextResolver` (AD-27) decide agente e contexto. O contexto pode vir de:

1. `@alias` explícito;
2. repositório ativo salvo;
3. General Mode.

Exemplo:

```text
@dante revise o projeto
```

remove `@dante` do prompt e resolve o path pelo `RepositoryRegistry`.

## Etapa 5 — resolve preferências

Obtém:

- agente padrão;
- modo;
- modelo;
- esforço.

As preferências vêm de `AssistantSettingsStore`.

## Etapa 6 — abre sessão

```text
SessionRegistry.StartAsync
```

O Registry:

1. cria ID `S...`;
2. escolhe driver pelo agente;
3. valida modo suportado;
4. cria `AgentSession`;
5. inicia o driver;
6. recebe PID + upstream session/thread;
7. marca a sessão como iniciada;
8. torna a sessão ativa do usuário;
9. inicia o pump de eventos.

## Etapa 7 — registra entrega

`TelegramDeliveryService.RegisterSession` associa:

- session ID;
- Telegram user ID;
- chat ID;
- política de hide output.

## Etapa 8 — submete primeiro turno

```text
SessionRegistry.SubmitAsync
  ↓
AgentSession.Submit
  ↓
SubmitOutcome.TurnStarted
  ↓
driver.StartTurnAsync
```

## Etapa 9 — agente trabalha

O processo já está vivo.

A mensagem é enviada por stdin usando o protocolo do driver.

## Etapa 10 — eventos voltam

Exemplo:

```text
CLI stdout
  ↓
InteractiveAgentProcess
  ↓
ClaudeSessionDriver
  ↓
MessageDeltaEvent
  ↓
SessionRegistry.PumpAsync
  ↓
AgentSession.Apply
  ↓
TelegramDeliveryService.PublishAsync
```

## Etapa 11 — entrega

O Delivery agrupa e envia partes para o Telegram.

O usuário vê a resposta como conversa, sem Job ID.

---

# 4. Mensagem comum com sessão ativa e ociosa

```text
segunda pergunta
```

Fluxo:

```text
ConverseAsync
  ↓
GetActive
  ↓
sessão Idle
  ↓
SubmitAsync
  ↓
novo T...
  ↓
StartTurnAsync
  ↓
mesmo processo do agente
```

A vantagem é que a CLI mantém o contexto upstream da conversa.

---

# 5. Mensagem durante um turno em andamento

Mensagem comum usa política `Queue`.

```text
turno está Running
   +
nova mensagem
   ↓
AgentSession.Submit(... Queue)
   ↓
LinkedList queue
   ↓
SubmitOutcome.Queued
```

O Telegram responde algo equivalente a:

```text
Recebido; envio ao agente quando a resposta atual terminar.
```

Quando o turno termina:

```text
TurnCompletedEvent
  ↓
SessionRegistry
  ↓
AgentSession volta a Idle
  ↓
StartNextQueuedAsync
  ↓
abre novo turno
```

---

# 6. Fluxo de `/steer`

```text
/steer não altere esse arquivo
```

## Codex

Como há steer nativo:

```text
Telegram
 ↓
SessionRegistry.SubmitAsync(... Steer)
 ↓
AgentSession → Steered
 ↓
CodexSessionDriver.SteerAsync
 ↓
turn/steer
```

O turno continua.

## Claude

Como não há steer nativo:

```text
Telegram
 ↓
AgentSession → SteerByInterrupt
 ↓
ClaudeSessionDriver.InterruptTurnAsync
 ↓
turno termina
 ↓
mensagem steer inicia antes da fila normal
```

A UX é semelhante, mas a semântica de protocolo é diferente.

---

# 7. Approval

Imagine que o agente precisa executar uma ação que exige autorização.

## Etapa 1 — CLI solicita

Driver recebe o request.

## Etapa 2 — driver registra upstream request

O driver guarda os dados necessários para responder depois.

## Etapa 3 — emite evento

```text
ApprovalRequestedEvent
```

Ainda sem IDs D.A.N.T.E. completos.

## Etapa 4 — AgentSession aplica

`AgentSession.Apply` cria o request lógico e associa:

```text
S...
T...
R...
```

## Etapa 5 — TelegramDeliveryService

Monta uma mensagem com inline keyboard:

```text
[Aprovar uma vez]
[Aprovar na sessão]
[Negar]
```

quando as capacidades permitirem.

## Etapa 6 — usuário clica

O Telegram envia `callback_query`.

## Etapa 7 — validações

O sistema valida:

- callback parseável;
- usuário autorizado;
- request pendente;
- owner;
- chat;
- message ID;
- session;
- turn;
- decisão suportada;
- request não expirado.

## Etapa 8 — resposta

```text
SessionRegistry.RespondAsync
  ↓
AgentSession.Resolve
  ↓
driver.RespondAsync
```

O driver converte a decisão para o protocolo da CLI.

## Etapa 9 — UI

A mensagem original é editada e os botões deixam de estar ativos.

---

# 8. Expiração de approval/input

Request possui deadline.

O Registry agenda expiração.

Quando chega:

```text
ExpireRequestAsync
  ↓
AgentSession.TryExpire
  ↓
responde upstream de forma segura
  ↓
RequestExpiredEvent
  ↓
TelegramDeliveryService
  ↓
mensagem atualizada
```

Uma resposta humana tardia é rejeitada.

---

# 9. Fluxo de input humano

Exemplo conceitual:

```text
O agente pergunta:
Qual abordagem você prefere?
```

Driver emite:

```text
UserInputRequestedEvent
```

O request recebe `R...`.

Usuário responde com comando de input apropriado.

O Registry valida ownership e correlação.

O driver converte a resposta para o protocolo da CLI.

---

# 10. Interrupção de turno

```text
/session stop
```

ou fluxo equivalente chama:

```text
SessionRegistry.InterruptAsync
```

O Registry serializa essa operação com respostas/expirações para evitar races.

A sessão:

- expira requests pendentes;
- descarta mensagens da fila;
- solicita interrupção ao driver.

A sessão continua existindo e pode receber outro turno.

Isso é diferente de fechar.

---

# 11. Fechamento de sessão

```text
/session close
```

Fluxo conceitual:

```text
AgentSession.TryClose
  ↓
impede novos submits
  ↓
resolve/encerra pendências
  ↓
driver.CloseAsync
  ↓
stdin fecha
  ↓
processo termina
  ↓
AgentSession.MarkClosed
```

Se o processo não terminar no grace period, a árvore é morta.

---

# 12. Falha inesperada do processo

Se stdout quebra ou o processo morre fora do encerramento esperado:

```text
driver event stream falha
  ↓
SessionRegistry.PumpAsync detecta
  ↓
FailAsync
  ↓
AgentSession.MarkFailed
  ↓
ErrorEvent
  ↓
driver/process disposed
```

A sessão fica terminal.

Ela não é silenciosamente substituída por uma nova porque isso poderia trocar contexto sem o usuário perceber.

---

# 13. Fluxo one-shot `/claude`

Exemplo:

```text
/claude @dante revise o README
```

## Etapa 1

Polling identifica `/claude`.

## Etapa 2

Resolve:

- alias/contexto;
- environment;
- modelo;
- esforço.

## Etapa 3

`JobRegistry.Create` gera `J...`.

## Etapa 4

Telegram recebe confirmação com Job ID.

## Etapa 5

Background task chama:

```text
ClaudeRunner.RunAsync
  ↓
AgentProcessExecutor.ExecuteAsync
```

## Etapa 6

O processo termina.

## Etapa 7

`JobRegistry.Complete`.

## Etapa 8

Resposta final entra no `TelegramDeliveryService`.

Não há continuidade desse processo.

---

# 14. Fluxo one-shot `/codex`

Igual ao anterior, trocando:

```text
ClaudeRunner
```

por:

```text
CodexRunner
```

O executor de processos continua compartilhado.

---

# 15. Fluxo de cancelamento de job

```text
/cancel J000001
```

`JobRegistry.TryCancel` sinaliza o CancellationToken do job.

O executor recebe cancelamento:

```text
WaitForExitAsync token cancela
  ↓
KillProcessTree
  ↓
resultado Cancelled
```

A árvore inteira é encerrada.

---

# 16. Fluxo de entrega

O `TelegramDeliveryService` recebe eventos ou resultado de job.

Internamente:

```text
texto/evento
  ↓
redaction
  ↓
buffer
  ↓
formatter
  ↓
chunks
  ↓
scheduler
  ↓
SendPartAsync
  ↓
TelegramBotApi
```

## Batching

Eventos próximos são acumulados para evitar uma mensagem por token/evento.

## Split

Partes respeitam limite de mensagem.

## Retry

Falhas transitórias recebem backoff.

## 429

`retry_after` do Telegram é respeitado.

## HTML recusado

```text
TelegramMarkupException
  ↓
marca chunk como plain
  ↓
reenvia sem parse_mode
```

## Falha permanente

A entrega vira:

```text
Failed
```

mas o turno/job não é reexecutado.

`/resend` tenta somente a entrega.

---

# 17. Fluxo de redaction de segredo

Considere um secret vindo de host binding.

O Delivery conhece os valores sensíveis relevantes.

Para texto completo:

```text
secret real
  ↓
Redact
  ↓
[segredo omitido]
```

Para streaming há um caso mais difícil:

```text
delta 1: "tok_abc"
delta 2: "123..."
```

Se o primeiro delta puder ser prefixo de um segredo conhecido, a cauda é retida.

Só depois de saber se ela forma o segredo completo o conteúdo é liberado.

Isso evita vazar metade de um token antes de a outra metade chegar.

Comandos pendentes também não podem ultrapassar essa região retida.

---

# 18. Fluxo de contexto

## Alias explícito

```text
@dante faça X
```

vence o repositório ativo apenas para aquela resolução.

## Repositório ativo

```text
/use @dante
```

persiste a escolha para o usuário.

## General

```text
/use general
```

remove o repo ativo.

### Sessão já aberta

Alterar `/use` não muda o contexto da sessão viva.

Essa imutabilidade impede que uma conversa mude de workspace no meio.

---

# 19. Fluxo de modelo e esforço

Preferência:

```text
AssistantSettingsStore
```

Validação:

```text
AgentModelCatalog
```

A seleção é resolvida **antes** de iniciar o processo.

Depois que uma sessão começa:

- modelo fica fixo;
- esforço fica fixo.

Uma alteração de preferência vale para novas sessões/jobs.

---

# 20. Fluxo de shutdown do Worker

Quando o Host encerra:

```text
stoppingToken cancela
  ↓
TelegramPollingService sai do loop
  ↓
SessionRegistry.DisposeAsync
  ↓
sessões vivas são encerradas/falhadas
  ↓
drivers disposed
  ↓
árvores de processo encerradas
```

Jobs e sessões não são restaurados na próxima inicialização.

---

# 21. Mapa rápido de diagnóstico por fluxo

| Sintoma | Comece por |
|---|---|
| `/ping` não responde | `TelegramPollingService.ExecuteAsync`, token, allowlist |
| bot recebe, mas não envia | `TelegramBotApi.SendMessageCoreAsync` |
| mensagem comum não abre conversa | `ConverseAsync` |
| contexto errado | `AgentContextResolver`, settings, repository registry |
| sessão abre e morre | driver + `InteractiveAgentProcess` |
| resposta fora de ordem | `TelegramDeliveryService` |
| approval não funciona | callback + `SessionRegistry.RespondAsync` |
| job one-shot trava | runner + `AgentProcessExecutor` |
| subprocesso fica vivo | `InteractiveAgentProcess` / `ProcessTree` |
| `/resend` não encontra saída | retention do `TelegramDeliveryService` |

# 22. Fluxos alvo do Brain (#134, ainda não implementados)

Estes fluxos concretizam a [AD-33](../context/ARCHITECTURE_DECISIONS.md#ad-33--brain-como-núcleo-de-conhecimento-com-recuperação-seletiva-separado-da-sessão-e-do-histórico).
Os nomes representam responsabilidades conceituais; nenhum caminho abaixo existe
no Worker atual. Detalhes físicos pertencem à #135/#160.

## Capturar e consolidar

```text
"guarde esta solução" / fonte selecionada
  → identidade + Space/Project + autorização
  → classificação de sensibilidade e evidência
  → KnowledgeCandidate (MemoryCandidate)
  → deduplicação / conflitos
  → confirmação, rejeição ou correção pelo usuário/policy
  → Knowledge Core valida status, origem e revisão
  → commit do item + proveniência + relações
  → índices derivados atualizados (ou marcados para reconstrução)
  → confirmação ao usuário somente após commit canônico
```

Falha de gravação não confirma captura; falha de índice não perde conhecimento.
Nenhum turno inteiro é consolidado automaticamente como fato. Uma inferência pode
ser registrada como inferred, nunca silenciosamente confirmada pelo agente.

## Retomar em outra sessão ou agente

```text
pedido novo → resolver identidade/Space/Project
  → carregar snapshot operacional elegível
  → Brain Search com autorização e filtros antes do ranking
  → relações autorizadas, expansão limitada
  → priorizar / deduplicar / aplicar budget ao conjunto enviado
  → Context Pack com IDs, revisões, status, origem e tokens estimados
  → revalidar elegibilidade / sensibilidade
  → adapter injeta contexto como dado, preservando permissões
  → nova sessão Claude/Codex executa o pedido
```

A sessão anterior não é restaurada. Snapshot não é histórico infinito e não cria
Knowledge Items automaticamente. `/clear` e `/compact` controlam a conversa upstream,
sem apagar o Brain. Pack vazio ou busca indisponível não causam fallback ao chat
inteiro nem acesso implícito a outro Space; comunicar a diferença ao usuário.

## Corrigir, excluir e exportar

Identidade/escopo → policy → localizar IDs/revisões autorizados → operação explícita.
Correção/substituição registra proveniência e supersession; revisão concorrente
produz conflito, não sobrescrita silenciosa. Exclusão invalida derivados/caches e
impede que snapshots/relações reintroduzam conteúdo excluído. Export aplica policy
por item/fonte e explica omissões, preservando IDs/origens dos dados permitidos.

O Context Builder registra candidatos, selecionados e efetivamente injetados. A
validação da #147 compara continuidade entre Claude/Codex e contexto enviado versus
baseline de histórico; logs de métricas não carregam conteúdo sensível.
