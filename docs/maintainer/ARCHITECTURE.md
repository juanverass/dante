# Arquitetura do D.A.N.T.E.

## 1. Qual arquitetura o projeto utiliza?

A melhor definição para a arquitetura atual é:

> **Monólito modular local, orientado a serviços e eventos, com adapters explícitos nas bordas e máquinas de estado para sessões interativas.**

Isso significa que o D.A.N.T.E. **não é um conjunto de microserviços** e também **não implementa arquitetura hexagonal estrita**.

Existe um único executável principal:

```text
Dante.Worker
```

Ele é um `Generic Host` do .NET 10. Todos os módulos principais vivem no mesmo processo e são compostos por injeção de dependência em `Program.cs`.

### Classificação por dimensão

| Dimensão | Escolha atual |
|---|---|
| Deploy | monólito: um único Worker |
| Organização interna | modular por responsabilidade |
| Integrações externas | adapters explícitos para Telegram e CLIs |
| Comunicação interna | chamadas de serviço + fluxo de eventos |
| Estado interativo | máquinas de estado em memória |
| Persistência | arquivos JSON locais; sem banco |
| Concorrência | Tasks, Channels, locks, semáforos e CancellationToken |
| Processos externos | Claude Code e Codex CLI como processos filhos |
| Interface remota | Telegram Bot API via long polling |

## 2. Por que chamar de monólito modular?

Porque há **um único processo implantável**, mas o código não está organizado como um bloco único.

Os limites são visíveis pelas pastas:

```text
Agents
Jobs
Repositories
Sessions
Settings
Telegram
```

Cada módulo possui uma responsabilidade clara e reduz o acoplamento entre detalhes.

Exemplo:

- `SessionRegistry` não precisa conhecer JSON específico do Claude ou do Codex;
- `AgentSession` não fala com Telegram;
- `TelegramDeliveryService` não inicia processos;
- `RepositoryRegistry` não decide como uma mensagem será roteada.

Portanto, o projeto tem modularidade arquitetural mesmo estando no mesmo executável.

## 3. Há elementos de Ports & Adapters?

Sim, mas o projeto **não deve ser descrito como arquitetura hexagonal completa**.

Existem abstrações que funcionam como ports:

- `ITelegramBotApi`
- `IAgentSessionDriver`
- `IAgentSessionDriverFactory`
- `IAgentSessionEventSink`
- `IAgentProcessExecutor`
- `IInteractiveAgentProcessLauncher`
- `IClaudeRunner`
- `ICodexRunner`
- `IAgentModelCatalog`

E implementações que funcionam como adapters:

- `TelegramBotApi`
- `ClaudeSessionDriver`
- `CodexSessionDriver`
- `AgentProcessExecutor`
- `InteractiveAgentProcessLauncher`

Essa separação traz benefícios típicos de Ports & Adapters:

- os testes conseguem trocar integrações por fakes;
- Telegram não conhece o protocolo das CLIs;
- sessões não conhecem HTTP;
- os detalhes de Claude e Codex ficam encapsulados em drivers.

Porém o projeto não possui uma divisão formal em `Domain/Application/Adapters` nem aplica todas as regras de dependência de uma arquitetura hexagonal clássica.

A descrição correta é:

> **monólito modular com fronteiras inspiradas em Ports & Adapters.**

## 4. Visão de alto nível

```text
                        ┌───────────────────────────┐
                        │         Telegram          │
                        └─────────────┬─────────────┘
                                      │ Bot API
                                      ▼
                        ┌───────────────────────────┐
                        │ TelegramPollingService    │
                        │ - long polling            │
                        │ - comandos                │
                        │ - roteamento inicial      │
                        └──────┬──────────┬─────────┘
                               │          │
                  mensagem     │          │ /claude /codex
                   comum       │          │
                               ▼          ▼
                    ┌────────────────┐  ┌───────────────┐
                    │ SessionRegistry│  │  JobRegistry  │
                    └───────┬────────┘  └──────┬────────┘
                            │                  │
                            ▼                  ▼
                    ┌───────────────┐   ┌───────────────┐
                    │ AgentSession  │   │ ClaudeRunner   │
                    │ state machine │   │ CodexRunner    │
                    └──────┬────────┘   └──────┬────────┘
                           │                   │
                    IAgentSessionDriver        │
                     ┌─────┴─────┐             ▼
                     ▼           ▼       AgentProcessExecutor
                 Claude       Codex              │
                 driver       driver             │
                     │           │               │
                     └─────┬─────┘               │
                           ▼                     ▼
                  InteractiveAgentProcess   processo one-shot
                           │
                       stdin/stdout
                           │
                 Claude Code / Codex CLI
                           │
                           ▼
                      AgentEvent
                           │
                           ▼
                 TelegramDeliveryService
                           │
                           ▼
                        Telegram
```

## 5. Os dois caminhos de execução

O sistema mantém deliberadamente **dois modelos de execução**.

### 5.1 Sessão interativa

É o caminho padrão para mensagens comuns.

```text
mensagem
  ↓
SessionRegistry
  ↓
AgentSession
  ↓
IAgentSessionDriver
  ↓
processo Claude/Codex de longa duração
  ↓
AgentEvent
  ↓
TelegramDeliveryService
```

Características:

- um processo por sessão;
- vários turnos no mesmo processo;
- streaming;
- queue;
- steer;
- approvals;
- input humano;
- interrupção de turno;
- contexto e modelo fixados no início da sessão.

### 5.2 Job one-shot

É usado por comandos explícitos:

```text
/claude ...
/codex ...
```

Fluxo:

```text
comando
  ↓
JobRegistry
  ↓
ClaudeRunner / CodexRunner
  ↓
AgentProcessExecutor
  ↓
processo curto
  ↓
resultado final
  ↓
TelegramDeliveryService
```

Características:

- um processo por execução;
- não há continuidade de conversa;
- resultado é acumulado até o processo terminar;
- há Job ID;
- pode ser cancelado via `/cancel`.

Essa coexistência é intencional. Sessões não substituem jobs; elas resolvem um problema diferente.

## 6. Núcleo de sessão como máquina de estados

`AgentSession` é uma máquina de estados neutra.

Ela não:

- abre processos;
- acessa Telegram;
- conhece JSON-RPC do Codex;
- conhece stream-json do Claude.

Ela mantém o estado lógico da sessão.

Estados relevantes:

```text
Starting
   ↓
Idle
   ↓
Running
   ↕
WaitingForApproval / WaitingForInput
   ↓
Idle

Closing
   ↓
Closed

qualquer falha terminal
   ↓
Failed
```

A ideia central é separar:

```text
estado lógico da conversa
        ≠
estado do processo
        ≠
estado da entrega Telegram
```

Por isso existem:

- `AgentSessionState`
- `InteractiveAgentProcessState`
- `TelegramDeliveryState`

Cada um responde a uma pergunta diferente.

## 7. Arquitetura orientada a eventos dentro das sessões

Os drivers convertem protocolos específicos em um contrato neutro:

```text
Claude stream-json
        │
        ▼
ClaudeSessionDriver
        │
        ├─ MessageDeltaEvent
        ├─ ToolStartedEvent
        ├─ FileChangeEvent
        ├─ ApprovalRequestedEvent
        ├─ UserInputRequestedEvent
        └─ TurnCompletedEvent
```

e:

```text
Codex app-server
        │
        ▼
CodexSessionDriver
        │
        └─ mesmos AgentEvent
```

O restante do sistema trabalha com `AgentEvent`, não com o protocolo original.

Esse é um ponto arquitetural central.

## 8. Contexto é resolvido antes da execução

O D.A.N.T.E. não permite que uma sessão ou job “descubra sozinho” em qual repositório atuar.

O contexto é resolvido antes:

```text
General Mode
ou
Repository Mode (@alias)
```

A execução recebe um `JobExecutionContext` pronto.

Isso reduz risco de:

- atuar no projeto errado;
- trocar de diretório durante a execução;
- inferir contexto com base no prompt.

Em sessões, o contexto é **imutável durante toda a vida da sessão**.

## 9. Persistência híbrida

O sistema não usa banco de dados.

Existem dois tipos de estado.

### Persistente

Guardado em arquivos JSON:

- preferências de usuário;
- aliases de repositórios;
- ambiente configurado por repositório.

### Volátil

Guardado apenas em memória:

- jobs;
- sessões;
- turnos;
- requests pendentes;
- estado de entrega;
- retries recentes.

Consequência:

> reiniciar o Worker encerra a realidade operacional atual e cria uma nova.

Isso é uma decisão arquitetural atual, não um bug.

## 10. Concorrência

A arquitetura usa primitivas explícitas em vez de um framework de atores.

Principais mecanismos:

### `lock`

Protege estruturas em memória como:

- catálogos;
- registros;
- estado de sessão.

### `SemaphoreSlim`

Usado quando uma operação assíncrona precisa ser serializada.

Exemplos:

- escrita no stdin de um agente;
- operações upstream de uma sessão;
- envio/edição de mensagens relacionadas.

### `Channel<T>`

Usado para streaming com backpressure.

Exemplo:

`InteractiveAgentProcess` mantém um channel limitado para stdout/stderr.

Se o consumidor ficar lento, o produtor também desacelera em vez de consumir memória indefinidamente.

### `CancellationToken`

É o mecanismo padrão para:

- shutdown;
- cancelamento de job;
- interrupção de waits;
- timeout.

## 11. Segurança como decisão arquitetural

Segurança não está concentrada em uma única classe. Ela aparece em várias fronteiras.

### Entrada

`TelegramUserAuthorizer` aplica allowlist fail-closed.

### Processo

`AgentProcessStartInfo`:

- não usa shell;
- fixa o executável;
- usa `ArgumentList`;
- trata prompt como dado.

### Diretório

Working directory precisa ser absoluto e existente.

### Repositório

`RepositoryRegistry` valida aliases e raiz Git.

### Ambiente

Em General Mode o ambiente herdado é filtrado.

Bindings sensíveis são resolvidos a partir do host.

### Saída

`TelegramDeliveryService` redige segredos antes de enviar conteúdo.

### Approval

Request é correlacionado a:

- usuário;
- sessão;
- turno;
- request;
- mensagem Telegram.

## 12. As bordas do sistema

As bordas principais são:

```text
                  ┌──────────────┐
                  │   Telegram   │
                  └──────┬───────┘
                         │
                         ▼
                 ITelegramBotApi
                         │
             ┌───────────┴───────────┐
             │     núcleo DANTE      │
             └───────────┬───────────┘
                         │
             IAgentSessionDriver /
             IAgentProcessExecutor
                         │
                         ▼
                 Claude / Codex CLI
```

Outras bordas:

- filesystem;
- Git;
- arquivos `~/.dante/*.json`;
- variáveis de ambiente.

## 13. O que NÃO existe hoje

Para não criar um modelo mental errado:

- não há banco relacional;
- não há API HTTP própria;
- não há frontend web;
- não há message broker;
- não há microserviços;
- não há Kubernetes;
- não há Docker como requisito arquitetural;
- não há scheduler persistente;
- não há fila durável;
- não há event sourcing;
- não há CQRS formal;
- não há arquitetura hexagonal formal.

## 14. Decisões arquiteturais formais

O projeto mantém decisões em:

`docs/context/ARCHITECTURE_DECISIONS.md`

As decisões mais importantes para compreender a arquitetura atual são:

- AD-01 — Telegram via long polling;
- AD-02/03 — processos locais e sem shell arbitrário;
- AD-07–11 — repositórios, contexto e ambiente;
- AD-13/14 — settings persistidas e repo ativo;
- AD-15–20 — sessões, processos interativos, drivers e registry;
- AD-21 — entrega Telegram independente da execução;
- AD-22–24 — approval, session-first e modos;
- AD-25/26 — modelo e esforço.

Essas decisões são a justificativa histórica. Este documento é o mapa consolidado.

## 15. Regra prática para futuras mudanças

Antes de adicionar uma funcionalidade, identifique em qual responsabilidade ela pertence.

Exemplos:

| Necessidade | Lugar provável |
|---|---|
| novo comando Telegram | `TelegramPollingService` |
| nova forma de enviar mensagem | `TelegramDeliveryService` |
| novo detalhe da Bot API | `TelegramBotApi` |
| comportamento comum de sessão | `AgentSession` / `SessionRegistry` |
| comportamento específico do Claude | `ClaudeSessionDriver` |
| comportamento específico do Codex | `CodexSessionDriver` |
| processo one-shot | `Agents/*Runner` / `AgentProcessExecutor` |
| repo/alias/env | `RepositoryRegistry` |
| preferência persistente | `AssistantSettingsStore` |
| seleção de modelo | `AgentModelCatalog` |

Se uma alteração começar a atravessar muitos desses limites ao mesmo tempo, é um sinal para revisar a responsabilidade antes de continuar.
