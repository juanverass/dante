# Glossário do D.A.N.T.E.

## D.A.N.T.E.

**Distributed Agent Network for Task Execution.**

Orquestrador local que usa Telegram para controlar Claude Code e Codex CLI executando na máquina do usuário.

---

## Worker

O processo `Dante.Worker`, baseado no Generic Host do .NET.

Não confundir com “worker/agente de desenvolvimento” usado nos documentos do harness.

---

## Agent

No produto, Claude ou Codex.

Representado por `AgentKind`.

---

## General Mode

Execução fora de um repositório cadastrado.

Workspace padrão:

```text
~/.dante/workspaces/general
```

ou `DANTE_GENERAL_WORKSPACE`.

---

## Repository Mode

Execução dentro de um repositório cadastrado no `RepositoryRegistry`.

Identificado por alias:

```text
@dante
```

---

## RepositoryRegistry

Catálogo persistente de repositórios autorizados para execução.

Arquivo padrão:

```text
~/.dante/repositories.json
```

---

## AssistantSettingsStore

Store persistente das preferências do usuário.

Arquivo:

```text
~/.dante/settings.json
```

---

## Job

Execução **one-shot**.

Exemplos:

```text
/claude ...
/codex ...
```

Um job não é uma sessão.

---

## Job ID

Prefixo:

```text
J...
```

Identifica uma execução one-shot.

---

## Session

Conversa interativa de longa duração com um agente.

Uma sessão possui exatamente um driver/processo de agente durante sua vida.

---

## Session ID

Prefixo:

```text
S...
```

---

## Turn

Uma interação individual dentro de uma sessão.

Exemplo:

```text
Sessão
  ├─ pergunta 1 → turno
  ├─ pergunta 2 → turno
  └─ pergunta 3 → turno
```

---

## Turn ID

Prefixo:

```text
T...
```

---

## Request

Pedido pendente de resposta humana.

Pode ser:

- approval;
- input.

---

## Request ID

Prefixo:

```text
R...
```

---

## Upstream ID

ID pertencente à CLI/protocolo externo.

Exemplos:

- Claude `session_id`;
- Codex `thread.id`;
- Codex `turn.id`;
- JSON-RPC request id.

O D.A.N.T.E. tenta manter esses detalhes encapsulados no driver.

---

## Driver

Adapter que traduz entre o contrato neutro do D.A.N.T.E. e o protocolo de uma CLI.

Implementações:

- `ClaudeSessionDriver`;
- `CodexSessionDriver`.

---

## IAgentSessionDriver

Contrato comum dos drivers interativos.

---

## AgentEvent

Evento neutro emitido por drivers.

É a linguagem comum das sessões.

Exemplos:

- message delta;
- tool started;
- approval requested;
- turn completed.

---

## AgentSession

Máquina de estados lógica de uma sessão.

Não é o processo e não é o Telegram.

---

## SessionRegistry

Orquestrador e catálogo das sessões durante a vida do Worker.

Possui ownership, active session, drivers, pump de eventos e roteamento.

---

## InteractiveAgentProcess

Wrapper de processo filho de longa duração.

Controla stdin, stdout/stderr, shutdown e árvore de processos.

---

## AgentProcessExecutor

Executor de processo one-shot.

Captura resultado final em vez de produzir stream contínuo.

---

## Runner

Adapter do caminho one-shot.

- `ClaudeRunner`;
- `CodexRunner`.

---

## Queue

Política padrão quando chega uma nova mensagem durante um turno.

A mensagem aguarda e vira um novo turno depois.

---

## Steer

Tentativa explícita de orientar o trabalho corrente.

Codex possui suporte nativo.

Claude usa fallback por interrupção + novo turno.

---

## Approval

Decisão humana necessária para determinada ação.

Valores conceituais:

- aprovar uma vez;
- aprovar na sessão, quando suportado;
- negar.

---

## Permission profile / Mode

Perfil de operação da sessão.

Nomes de produto:

- `manual`;
- `auto`;
- `plan`.

Os drivers traduzem esses modos para capacidades de cada CLI.

---

## Model

Modelo selecionado para Claude ou Codex.

A lista é consultada da própria CLI pelo `AgentModelCatalog`.

---

## Effort

Nível de esforço/raciocínio exposto pela CLI para um modelo.

É independente do modo de permissão.

---

## Delivery

Estado da entrega de conteúdo ao Telegram.

É separado do estado do agente.

Estados:

- Pending;
- Delivered;
- Failed.

---

## /resend

Tenta entregar novamente partes recentes que falharam.

Não executa o agente outra vez.

---

## Long polling

Estratégia usada para receber updates do Telegram.

O Worker chama repetidamente `getUpdates`.

Não há webhook.

---

## Offset

Número usado pelo Telegram polling para não consumir o mesmo update novamente.

O D.A.N.T.E. avança o offset antes do dispatch do update.

---

## BotToken

Credencial do bot do Telegram.

Configuração:

```text
Telegram__BotToken
```

Nunca deve ser versionada ou logada.

---

## AllowedUserIds

Allowlist dos Telegram User IDs autorizados.

Configuração:

```text
Telegram__AllowedUserIds
```

A autorização usa `message.from.id`, não username.

---

## Host binding

Configuração de variável de repositório em que o catálogo guarda somente o **nome** da variável do host.

Exemplo:

```text
OPENAI_API_KEY ← OPENAI_API_KEY do host
```

O valor real é lido somente em runtime.

---

## Redaction

Substituição de valores sensíveis antes de enviar conteúdo ao Telegram.

Representação:

```text
[segredo omitido]
```

---

## Secret holdback

Mecanismo do streaming que segura uma cauda de texto que ainda pode completar um secret conhecido.

Evita vazar secret dividido entre vários deltas.

---

## Backpressure

Quando o consumidor de um stream está lento, o produtor também é desacelerado.

No processo interativo isso é obtido com `Channel` bounded.

---

## Process tree

Processo do agente + subprocessos iniciados por ele.

O D.A.N.T.E. tenta encerrar a árvore inteira no shutdown/cancelamento.

---

## ProcessProbe

Projeto de testes que simula Claude/Codex como processos reais.

Permite testar protocolo/processos sem chamar modelos reais.

---

## Sink

Destino neutro de eventos de sessão.

`IAgentSessionEventSink` atualmente aponta para o `TelegramDeliveryService`.

---

## Snapshot

Representação somente-leitura de um estado mutável.

Exemplos:

- `JobSnapshot`;
- `AgentSessionSnapshot`;
- `TelegramDeliverySnapshot`.

---

## Fail-closed

Política em que configuração inválida resulta em recusa/falha em vez de tentativa de adivinhação.

Aplicações:

- allowlist;
- settings;
- aliases;
- modelos;
- contextos.

---

## One-shot

Execução que inicia um processo para uma tarefa e o encerra ao terminar.

No D.A.N.T.E.:

```text
/claude
/codex
```

---

## Session-first

Decisão de UX em que mensagem comum significa conversa interativa.

Jobs one-shot exigem comando explícito.

---

## CURRENT_STATE

Documento de estado consolidado da implementação.

Não deve armazenar estado transitório de uma Issue em andamento.

---

## Architecture Decision / AD

Decisão arquitetural numerada em:

```text
docs/context/ARCHITECTURE_DECISIONS.md
```

Uma AD superada não é apagada; é marcada como substituída para preservar histórico.

---

## Handoff

No processo de desenvolvimento por agentes, é o registro que permite outro agente continuar uma Issue.

Não é uma feature runtime do D.A.N.T.E.

---

## Decision Lock

Decisão tomada durante desenvolvimento que agentes seguintes não devem reabrir por preferência pessoal.

## Vocabulário alvo do Brain (AD-33, #134)

Contratos conceituais, ainda não componentes implementados; veja
[Arquitetura alvo](ARCHITECTURE.md#16-arquitetura-alvo-do-dante-brain-133-134).

| Termo | Significado |
| --- | --- |
| Brain | Núcleo de conhecimento do D.A.N.T.E., independente da vida de sessões, de agentes, do canal e do provider físico. |
| Conversation History | Mensagens/eventos de conversa; pode fornecer evidência selecionada, não é Knowledge automaticamente. |
| Working Memory | Estado operacional limitado para retomar tarefa, sem chain-of-thought nem restauração de processos/requests. |
| Working Context Snapshot | Representação persistida, curta e substituível de Working Memory, por Space/Project, com origem/revisão/timestamp/validade. |
| KnowledgeSpace | Limite lógico de organização/isolamento, semelhante conceitualmente a um vault; pode conter Projects e conhecimento sem Project. |
| Project | Contexto persistente de trabalho dentro de um Space; não equivale a repositório Git ou sessão. |
| KnowledgeItem | Conhecimento consolidado com ID, escopo, conteúdo/tipo, revisão, status, sensibilidade, proveniência e validade. |
| KnowledgeRelation | Ligação semântica entre itens com origem e tipo; permite navegar Incident → Solution → Lesson sem impor banco de grafos. |
| SourceDocument / Source | Evidência identificada logicamente, com revisão/origem/trecho/hash quando aplicável; bruto não é fato confirmado. |
| Provenance | Origem e responsáveis por captura/confirmação/correção, com timestamps e referência à evidência. |
| MemoryCandidate / KnowledgeCandidate | Proposta com evidência antes da consolidação; rejeitar não polui o Knowledge Core. |
| Context Builder | Responsabilidade central de selecionar contexto autorizado/relevante dentro do budget. |
| Context Pack | Conjunto derivado para um pedido, com IDs/revisões/origens/status, justificativa e tokens estimados, não armazenamento canônico. |
| Sensitivity | Public, Personal, Work, Confidential ou Secret; policy decide leitura/injeção/export, Secret não é enviado automaticamente ao agente. |
| Knowledge status | confirmed, inferred, temporary, superseded ou inactive; separado de confidence e validade temporal. |
| Knowledge validity | Intervalo validFrom/validUntil quando aplicável; item confirmado pode expirar ou ser substituído. |
| TenantId / UserId | Identidades persistentes do acesso ao Brain; não são os IDs de chat/sessão/CLI. |
| Índice derivado | Embedding, chunk/indexação lexical ou resumo automático reconstruível a partir do conteúdo canônico e sua versão. |

## Vocabulário canônico do Brain na arquitetura hexagonal (AD-36)

Os termos ingleses da arquitetura conceitual descrevem conceitos; código novo de
domínio/aplicação usa os nomes PT-BR abaixo, sem renomear o legado nesta fundação.

| Conceito | Nome de código canônico |
| --- | --- |
| KnowledgeSpace | EspacoDeConhecimento |
| Project | Projeto |
| KnowledgeItem | Conhecimento |
| KnowledgeRelation | RelacaoDeConhecimento |
| KnowledgeCandidate | CandidatoDeConhecimento |
| SourceDocument | DocumentoFonte |
| WorkingContextSnapshot | ContextoDeTrabalho |
| Sensitivity | Sensibilidade |
| Base de entidade persistente | EntidadeBase, com Guid Id |

Tipos de conhecimento: Fato, Decisao, Preferencia, Instrucao, Nota, Referencia,
Incidente, Solucao, Aprendizado, Procedimento, Resumo e Inferencia.
Exemplos de contratos: IEspacoDeConhecimentoRepository,
EspacoDeConhecimentoAppService, EspacoDeConhecimentoDto e
EspacoDeConhecimentoSearchDto. Sufixos técnicos não mudam. IDs/FKs têm Id no início.

Domain é o núcleo de invariantes; Application reúne casos de uso/portas;
Infrastructure implementa adapters; Worker/WebApi são hosts de entrada e composição.
