# Spike: protocolos interativos de Claude Code e Codex (#61)

Validação, contra as versões instaladas, das interfaces estruturadas que a Epic #60 usará
para sessões interativas. Nada aqui usa TUI, PTY ou ANSI: os dois agentes foram
dirigidos por JSONL em stdin/stdout, com um único processo por sessão.

Versões validadas em 2026-09-30 (WSL2, Linux):

```text
claude --version   2.1.284 (Claude Code)
codex --version    codex-cli 0.157.1
```

## Como reproduzir

Os scripts são auxiliares de engenharia (Python 3, sem dependências), não fazem parte do
Worker e consomem cota real das CLIs. Rode num diretório vazio e descartável:

```bash
mkdir -p /tmp/spike && cd /tmp/spike && git init -q
python3 <repo>/docs/spikes/interactive-protocols/claude_stream_json_spike.py /tmp/spike
python3 <repo>/docs/spikes/interactive-protocols/codex_app_server_spike.py /tmp/spike
python3 <repo>/docs/spikes/interactive-protocols/codex_user_input_spike.py /tmp/spike <modelo>
```

`<modelo>` é o `model` de `~/.codex/config.toml`: o modo `plan` exige o modelo explícito.

Cada script imprime todas as mensagens enviadas (`>>`) e recebidas (`<<`).

## Claude Code — `--print` com `stream-json` bidirecional

```text
claude --print --input-format stream-json --output-format stream-json --verbose
       --permission-mode manual --permission-prompt-tool stdio --no-session-persistence
```

| Capacidade | Como | Resultado |
| --- | --- | --- |
| iniciar | processo acima; `control_request` `initialize` opcional | ok; `session_id` chega no `system/init` de cada turno (a flag `--session-id <uuid>` existe para fixá-lo; não exercitada) |
| turno | `{"type":"user","message":{"role":"user","content":"..."}}` em stdin | ok; o turno termina com uma mensagem `result` (`subtype` `success` ou `error_*`) |
| multi-turno | nova mensagem `user` no mesmo processo | ok; mesmo `session_id`, contexto preservado |
| eventos | `system/init`, `assistant` (blocos `text`, `thinking`, `tool_use`), `user` (`tool_result`), `result`; deltas com `--include-partial-messages` (não exercitado) | ok |
| approval | `control_request` `can_use_tool` (tool, input, `permission_suggestions`) → `control_response` com `behavior: allow` (+ `updatedInput`) ou `deny` (+ `message`) | ok, allow e deny |
| input humano | `AskUserQuestion` chega como `can_use_tool`; a resposta volta em `updatedInput.answers` (`{pergunta: resposta}`) | ok |
| interrupt | `control_request` `interrupt` | ok; o turno termina com `result` `error_during_execution` e o processo segue vivo |
| mensagem durante turno | nova mensagem `user` enquanto há turno ativo | enfileirada pela CLI: vira o turno seguinte, com `result` próprio |
| steer nativo | — | **não existe**; só interrupt + nova mensagem |
| encerrar | fechar stdin | ok; exit code 0 |

Achados que viram regra:

- `--permission-prompts host` sozinho **não** entrega os pedidos ao host: em modo
  `manual` a ferramenta é negada automaticamente ("you haven't granted it yet"). É
  preciso `--permission-prompt-tool stdio`, e só então `AskUserQuestion` aparece entre as
  ferramentas.
- `--permission-mode` aceita `acceptEdits`, `auto`, `bypassPermissions`, `manual`,
  `dontAsk` e `plan`. O mapeamento para os perfis do D.A.N.T.E. é da #67.
- "aprovar na sessão" é oferecido em `permission_suggestions` (ex.: `setMode
  acceptEdits`, destino `session`); o formato da resposta que aplica a sugestão não foi
  exercitado e fica para o driver (#63).

## Codex — `app-server` via stdio (JSON-RPC em JSONL)

```text
codex app-server --listen stdio://
```

Sequência: `initialize` → notificação `initialized` → `thread/start` (`cwd`,
`approvalPolicy`, `sandbox`, `ephemeral`) → `turn/start` (`threadId`, `input`).
O schema completo da versão instalada sai de `codex app-server generate-json-schema --out
<dir>`.

| Capacidade | Como | Resultado |
| --- | --- | --- |
| iniciar | `initialize` + `initialized` + `thread/start` | ok; `thread.id` na resposta |
| turno | `turn/start` → resposta com `turn.id` | ok; termina com notificação `turn/completed` (`status`: `completed`, `interrupted`, `failed`) |
| multi-turno | novo `turn/start` no mesmo `threadId` | ok |
| eventos | `turn/started`, `item/started`, `item/completed` (`agentMessage`, `commandExecution`, `fileChange`, `reasoning`…), `item/agentMessage/delta`, `turn/diff/updated`, `thread/status/changed`, `warning`, `error` | ok |
| approval | request do servidor `item/commandExecution/requestApproval` (e `item/fileChange/requestApproval`) → resposta `{"decision": "accept" \| "acceptForSession" \| "decline" \| "cancel"}` | ok com `decline`; `thread/status/changed` sinaliza `waitingOnApproval` e `serverRequest/resolved` confirma |
| input humano | request do servidor `item/tool/requestUserInput` (`questions`: `id`, `header`, `question`, `options`, `isOther`) → `{"answers": {<id>: {"answers": ["..."]}}}` | ok, **EXPERIMENTAL** (`codex_user_input_spike.py`): request chegou ao host, a resposta voltou (`serverRequest/resolved`) e o modelo respondeu com o texto enviado; `thread/status/changed` sinaliza `waitingOnUserInput`. Exige `capabilities.experimentalApi` no `initialize` e `collaborationMode` `plan` no `turn/start` |
| interrupt | `turn/interrupt` (`threadId`, `turnId`) | ok; `turn/completed` com `status: interrupted` |
| steer | `turn/steer` (`threadId`, `expectedTurnId`, `input`) | ok; a mensagem entra no turno ativo, mas **só no próximo boundary do modelo** (não preempta a resposta em curso) |
| `turn/start` com turno ativo | — | **não cria turno novo**: devolve o mesmo `turn.id` e a mensagem é absorvida pelo turno ativo, como um steer |
| encerrar | fechar stdin | ok; exit code 0 |

Achados que viram regra:

- input humano no Codex é API experimental e, na 0.157.1, só existe no modo `plan`: a
  ferramenta `request_user_input` no modo padrão depende da feature
  `default_mode_request_user_input`, listada como *under development* e desligada. O
  driver (#64) precisa de `experimentalApi` e decide quando usar o modo `plan`; a
  capacidade `UserInput` do Codex vale com essa condição e deve ser revalidada a cada
  versão da CLI;
- o comportamento de `turn/start` durante um turno ativo torna a fila responsabilidade
  do D.A.N.T.E.: o driver só inicia turno com a sessão ociosa;
- `approvalPolicy` aceita `untrusted`, `on-request`, `never` ou granular; `sandbox`
  aceita `read-only`, `workspace-write`, `danger-full-access`. Mapeamento para perfis: #67.

## Diferenças encapsuladas

| Aspecto | Claude | Codex |
| --- | --- | --- |
| id upstream da sessão | `session_id` | `thread.id` |
| id upstream do turno | não há | `turn.id` |
| correlação de approval/input | `request_id` do `control_request` | `id` do request JSON-RPC do servidor |
| steer | não nativo | `turn/steer`, aplicado no próximo boundary |
| fim de turno | mensagem `result` | notificação `turn/completed` |

Essas diferenças ficam dentro de `IAgentSessionDriver`
(`src/Dante.Worker/Sessions/IAgentSessionDriver.cs`); o resto do D.A.N.T.E. vê só
`AgentSession`, `AgentEvent` e `AgentDriverCapabilities`. As decisões resultantes estão em
[AD-15 e AD-16](../../context/ARCHITECTURE_DECISIONS.md).
