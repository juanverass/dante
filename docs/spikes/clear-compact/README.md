# Spike: limpar e compactar o contexto das sessões de Claude e Codex (#119)

Validação, contra as CLIs reais e nos protocolos que os drivers já usam (Claude stream-json e Codex
`app-server`), de como implementar `/clear` (#120) e `/compact` (#121) da Epic #118.

Versões e contas validadas em 2026-10-03 (WSL2, Linux):

```text
claude --version   2.1.287 (Claude Code)    login claude.ai, modelo padrão claude-opus-5-5
codex --version    codex-cli 0.159.3        login ChatGPT, modelo padrão gpt-6.1-sol
```

## Como reproduzir

Scripts auxiliares (Python 3, sem dependências), fora do Worker; consomem cota real. Usam os mesmos
argumentos/parâmetros dos drivers para uma sessão General em modo manual. Rode num diretório descartável:

```bash
mkdir -p /tmp/spike && cd /tmp/spike && git init -q
python3 <repo>/docs/spikes/clear-compact/claude_clear_compact_spike.py /tmp/spike
python3 <repo>/docs/spikes/clear-compact/codex_clear_compact_spike.py /tmp/spike
```

Cada script memoriza um marcador exclusivo (`ZEBRA-4712`) e uma instrução ("responda em MAIÚSCULAS"),
compacta, pergunta pelos dois, limpa e pergunta de novo, além dos casos de borda abaixo. Os ids impressos
são de sessões descartáveis; não há dado de conta nos registros.

## Matriz

| Operação | Fonte oficial | Mecanismo | Confirmação | Evidência real |
| --- | --- | --- | --- | --- |
| **Claude compact** | [Agent SDK — slash commands](https://code.claude.com/docs/en/agent-sdk/slash-commands) ("Compact history with `/compact`") | mensagem `user` com o texto exato `/compact` no stream-json (comando local da CLI, não vai ao modelo como pedido) | `system/compact_boundary` com `compact_metadata` (`trigger: manual`, `pre_tokens`, `post_tokens`), antes do `result` `success` (`num_turns: 0`) | `pre_tokens` 5201 → `post_tokens` 592 em 12,1 s; mesmo `session_id`; o turno seguinte respondeu "THE CODE WORD IS ZEBRA-4712. THE INSTRUCTION IS TO ALWAYS ANSWER IN UPPERCASE." |
| **Claude clear** | mesma página ("Reset context with `/clear`") | mensagem `user` com o texto exato `/clear` | mensagem `conversation_reset` (`trigger: clear`, `new_conversation_id`) e `result` `success` (`num_turns: 0`); o `system/init` seguinte traz **outro** `session_id` | marcador irrecuperável (resposta "UNKNOWN"); modelo, `permissionMode`, `cwd` e ferramentas iguais; modo trocado em runtime (`plan`) mantido |
| **Codex compact** | [App Server — Trigger thread compaction](https://learn.chatgpt.com/docs/app-server) | `thread/compact/start` (`threadId`), estável | resposta `{}` imediata; depois um turno próprio: `turn/started` → `item/started` e `item/completed` do item `contextCompaction` → `turn/completed` `status: completed` | 11,9–12,7 s; mesmo `threadId`; o turno seguinte respondeu "ZEBRA-4712. ALWAYS ANSWER IN UPPERCASE." |
| **Codex clear** | mesma página ("Start a fresh thread when you need a new Codex conversation"); não há reset nativo de uma thread | novo `thread/start` no **mesmo processo** com os parâmetros da sessão; depois `thread/unsubscribe` da thread antiga | resposta do `thread/start` com o novo `thread.id` e as políticas efetivas | marcador irrecuperável ("UNKNOWN"); `model`, `approvalPolicy`, `sandbox` e `cwd` iguais; `thread/unsubscribe` → `{"status":"unsubscribed"}` |

`thread/rollback` está descontinuado e só remove os últimos N turnos; `thread/fork` copia histórico. Nenhum dos
dois é "clear".

### Casos de borda observados

| Caso | Claude | Codex |
| --- | --- | --- |
| compactar conversa vazia | assistente "Error: No messages to compact", `result` `success`, **sem** `compact_boundary` | completa como compactação normal (24–32 s), sem sinal de "nada a compactar" |
| compactar logo após clear com uma troca | funciona (`pre_tokens` 5121 → 553) | funciona (8,9 s) |
| interromper a compactação | `interrupt` → `system/status` `compact_result: failed` ("Request was aborted"), assistente "Compaction canceled.", `result` `success`; histórico intacto | `turn/interrupt` no turno da compactação → `turn/completed` `interrupted`; histórico intacto |
| compactar com turno ativo | não exercitado (o protocolo enfileira mensagens `user`; ver regras) | `thread/compact/start` responde `{}` e **interrompe o turno ativo** (`interrupted`, sem resposta), depois compacta. Não há recusa upstream |
| texto `/clear` dentro de um turno comum | **é executado como comando** (o SDK despacha texto que começa com `/`) | é só texto para o modelo (respondeu "OK"; marcador mantido) |

### Métricas de contexto

- **Claude**: `compact_metadata.pre_tokens`/`post_tokens` vêm da própria compactação e são a métrica
  confiável. O `get_context_usage` (`totalTokens`) inclui prompt de sistema e ferramentas: numa conversa
  curta ele **subiu** de 5190 para 5830 depois da compactação (o resumo é maior que a conversa) e caiu para
  4580 depois do clear. Não serve para afirmar redução.
- **Codex**: não há métrica da compactação. O `thread/tokenUsage/updated` emitido durante a compactação traz
  `last.inputTokens: 0`, e o `last.inputTokens` dos turnos é dominado pelo prompt fixo (~17,6–18 mil tokens
  antes e depois). Nenhuma redução deve ser exibida.

## Contrato neutro mínimo dos drivers

```text
IAgentSessionDriver
  ClearContextAsync(ct)    → ContextCleared(UpstreamSessionId)
  CompactContextAsync(ct)  → ContextCompacted(PreTokens?, PostTokens?)   // métricas só quando o upstream informa
AgentDriverCapabilities
  ClearContext, CompactContext   (as duas CLIs: sim)
Falhas
  NothingToCompact | Failed(motivo, contexto anterior intacto) | Uncertain(motivo) | cancelamento
```

Tradução por driver:

- **Claude clear**: escreve `{"type":"user","message":{"role":"user","content":"/clear"}}`; sucesso só com
  `conversation_reset` (`trigger: clear`) seguido do `result` `success`. O id upstream novo é o
  `session_id` do `result`/`system/init` seguinte (o `new_conversation_id` do reset não é o `session_id`
  usado depois). `result` sem `conversation_reset` → `Failed` com contexto anterior presumido; tempo
  esgotado → `Uncertain`.
- **Claude compact**: escreve `/compact`; sucesso só com `compact_boundary` (ou `system/status`
  `compact_result: success`) antes do `result`. `compact_result: failed` → `Failed` (histórico intacto);
  `result` sem boundary → `NothingToCompact`. As mensagens do processo (texto "Error: No messages to
  compact", "Compaction canceled.", o resumo reinjetado como `user`) não são entregues como resposta do agente.
- **Codex compact**: `thread/compact/start`; o turno aberto pela compactação é do driver, não do usuário:
  correlacionado pelo `turn.id` do `turn/started` seguinte, sem `TurnStartedEvent`/`TurnCompletedEvent` de
  conversa. `turn/completed` `completed` com item `contextCompaction` concluído → sucesso; `interrupted`/
  `failed` → `Failed` (histórico intacto).
- **Codex clear**: `thread/start` com `cwd`, `ephemeral`, modelo e as políticas do **perfil efetivo**
  (`approvalPolicy`, `approvalsReviewer`, `sandbox`), conferindo a resposta como na abertura da sessão
  (inclusive o revisor do modo `auto`). Só depois do sucesso o driver troca o `threadId` e faz
  `thread/unsubscribe` da antiga (melhor esforço). Falha no `thread/start` → `Failed`, thread anterior
  continua em uso. Notificações passam a ser filtradas pelo `threadId` atual. O esforço vai em cada
  `turn/start` e não muda; uma troca de modo pendente continua pendente.

## Regras para #120 e #121

- **Só com a sessão ociosa**: sem turno ativo, sem fila, sem approval/input pendente, sem preparação de
  mídia em curso e sem troca de modo aguardando confirmação. O Codex não recusa compactação durante turno —
  ele interrompe o turno —, então a recusa é do D.A.N.T.E., antes de qualquer chamada.
- **Concorrência**: clear/compact entram na mesma serialização dos turnos, troca de modo e fechamento no
  `SessionRegistry`. Enquanto a operação roda, mensagens comuns são recusadas ou enfileiradas como um turno,
  nunca enviadas no meio dela; dono e sessão ativa são conferidos como nos demais comandos.
- **Slash command nunca como prompt**: o Claude executa qualquer texto que comece com `/`. Hoje o
  `/steer /clear` (ou `/steer /compact`) chegaria ao Claude como comando e limparia/compactaria sem o
  D.A.N.T.E. saber. O driver do Claude deve recusar texto de usuário que comece com `/` em turno e steer;
  o `/clear`/`/compact` do driver é a única origem desses textos.
- **Timeout**: clear 30 s (local nas duas CLIs; o `thread/start` levou < 1 s). Compactação depende do
  tamanho do contexto (6–32 s nas conversas curtas do spike): limite generoso (10 min), com
  `interrupt`/`turn/interrupt` ao estourar ou em `/session stop`; o resultado é `Failed` com histórico intacto.
- **Falha parcial e incerteza**: o sucesso só é anunciado após a confirmação upstream acima. Estado
  `Uncertain` (sem confirmação nem falha) encerra a sessão com erro explícito, como a troca de modo incerta
  da #108 (AD-24); nunca se continua mandando turnos a um contexto de estado desconhecido.
- **Nada a compactar**: o D.A.N.T.E. responde "nada a compactar" sem chamar o upstream quando a sessão não
  tem turno concluído desde o início ou o último clear (o Codex compactaria "com sucesso" uma thread vazia).
- **Identidade**: compact mantém `session_id`/`threadId`. Clear muda o id upstream nas duas CLIs: o id local
  (`S000001`) continua, `AgentSession.UpstreamSessionId` passa a ser o novo id e a resposta do `/clear` informa
  que a conversa upstream foi trocada (hoje `/status` não mostra o id upstream).
- **Configurações**: clear e compact mantêm processo, agente, `cwd`/repositório, modelo, esforço e modo
  efetivo (verificado em `system/init` e no `thread/start`); preferências persistidas não mudam.
- **Arquivos e anexos**: nenhuma operação toca arquivos do projeto. No clear, anexos pendentes do contexto
  anterior são descartados; arquivos da sessão e registros de entrega para `/resend` seguem o ciclo da sessão.
  O Claude mantém em disco a conversa anterior (`~/.claude/projects`), como já faz hoje; a thread efêmera do
  Codex não deixa arquivo.
- **Cotas**: compactar chama o modelo (consome cota); limpar não. Nenhuma das duas renova limites de uso.

Nenhum requisito da Epic fica sem mecanismo suportado. A decisão está em
[AD-32](../../context/ARCHITECTURE_DECISIONS.md).
