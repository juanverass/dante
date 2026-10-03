# Spike: consulta real das cotas de Claude e Codex (#115)

Validação, contra as CLIs e contas reais da máquina, de como o `/uso claude|codex` da Epic #114 obtém
o consumo da **janela de sessão**, o consumo **semanal** e o tempo até a janela de sessão renovar.

"Janela de sessão" aqui é a janela curta de cota definida pelo provedor (5 h nas duas contas
validadas), não a sessão `S000001` do D.A.N.T.E. nem o contexto/tokens de uma conversa. Os valores
pertencem à **conta autenticada** na CLI e incluem uso feito fora do D.A.N.T.E.

Versões e contas validadas em 2026-10-02 (WSL2, Linux):

```text
claude --version   2.1.287 (Claude Code)    conta claude.ai, plano Pro (OAuth da CLI)
codex --version    codex-cli 0.159.3        conta ChatGPT, plano Plus (login da CLI)
```

## Como reproduzir

Scripts auxiliares (Python 3, sem dependências), fora do Worker. Sem `--with-turn` nenhum deles envia
mensagem ao modelo; com `--with-turn` há um turno curto, que consome cota real. Rode num diretório
descartável:

```bash
mkdir -p /tmp/spike && cd /tmp/spike && git init -q
python3 <repo>/docs/spikes/usage-quotas/codex_rate_limits_spike.py /tmp/spike [--with-turn]
python3 <repo>/docs/spikes/usage-quotas/claude_usage_spike.py /tmp/spike [--with-turn]
```

Os scripts omitem e-mail, ids de conta/organização/instalação e o custo/comportamento local da sessão;
os payloads abaixo já estão assim.

## Codex — `account/rateLimits/read` no `app-server`

Fonte oficial: [App Server, seção *Rate limits (ChatGPT)*](https://learn.chatgpt.com/docs/app-server)
(antes `developers.openai.com/codex/app-server`) e o schema da versão instalada
(`codex app-server generate-json-schema --out <dir>`, `v2/GetAccountRateLimitsResponse.json`).
Método estável: não exige `capabilities.experimentalApi`.

```text
codex app-server --listen stdio://
>> initialize → initialized
>> {"id":2,"method":"account/read","params":{}}
<< {"id":2,"result":{"account":{"type":"chatgpt","email":"<redacted>","planType":"plus"},"requiresOpenaiAuth":true,...}}
>> {"id":3,"method":"account/rateLimits/read","params":{"excludeResetCreditDetails":true}}
<< {"id":3,"result":{"ordinaryUsageAllowed":true,
     "rateLimits":{"limitId":"codex","limitName":null,
       "primary":  {"usedPercent":2,"windowDurationMins":300,  "resetsAt":1790998535},
       "secondary":{"usedPercent":0,"windowDurationMins":10080,"resetsAt":1791585335},
       "credits":{"hasCredits":false,"unlimited":false,"balance":"0"},"planType":"plus","rateLimitReachedType":null},
     "rateLimitsByLimitId":{"codex":{ ...mesmo snapshot... }},
     "rateLimitResetCredits":{"availableCount":2,"credits":null},"accountId":"<redacted>"}}
```

| Aspecto | Resultado |
| --- | --- |
| consulta sem turno | ok: `initialize` + `account/rateLimits/read`, sem `thread/start`; nenhum modelo é chamado |
| latência | `account/read` 0,3–0,8 s; `account/rateLimits/read` 0,4–0,6 s (lê o serviço a cada chamada) |
| durante turno | ok: a leitura no mesmo processo, com turno ativo, respondeu em 0,4 s e o turno terminou `completed` |
| `account/rateLimits/updated` | notificação **esparsa** emitida durante turnos (ex.: depois de `thread/tokenUsage/updated`); o schema manda mesclar ou reler. Não serve para consulta sob demanda |
| sem login | `account/read` → `account: null`; `account/rateLimits/read` → erro `-32600` "codex account authentication required to read rate limits" |
| API key | `OPENAI_API_KEY` sem login também cai em "authentication required"; uma conta `apiKey` (`account.type`) não tem cota de assinatura ChatGPT. Login real por API key não foi exercitado |
| método ausente | erro `-32600` "Invalid request: unknown variant ..." (como responderia uma CLI antiga) |

Semântica comprovada:

- cada **bucket** é um `limitId` (`codex` é o da conta; a doc oficial mostra também `codex_other`) com
  até duas janelas, `primary` e `secondary`. A doc oficial exemplifica janelas de 15 e 60 min: posição
  **não** define tipo. A janela é identificada pela duração (`windowDurationMins`): 300 = 5 h (sessão),
  10080 = 7 dias (semana), como os rótulos "5-hour" / "Weekly" da própria CLI;
- `usedPercent` é inteiro 0–100 do limite já usado; `resetsAt` é Unix em segundos;
- buckets diferentes nunca se somam nem se substituem: só o `limitId` `codex` é a cota geral, e os demais
  (inclusive um bucket diferente que venha sozinho) são exibidos à parte pelo `limitName`/`limitId`;
- só as durações comprovadas classificam janelas: 300 min é a sessão e 10080 min é a semana. Uma janela curta de
  outra duração (como os 15 e 60 min do exemplo oficial) não é tratada como sessão: fica à parte;
- `rateLimitResetCredits`, `credits` e `individualLimit` são créditos/gastos, não consumo da cota.

## Claude — `get_usage` no stream-json (experimental)

O Claude Code tem três caminhos; só um serve à consulta sob demanda:

| Caminho | Status | Conclusão |
| --- | --- | --- |
| `rate_limit_event` | documentado no [Agent SDK](https://code.claude.com/docs/en/agent-sdk/python) (`RateLimitInfo`) | só aparece **durante turnos**, "quando o status muda"; cada evento descreve uma janela (`rateLimitType`), com `utilization` em **fração 0–1** (o observado trouxe também `unifiedWindows`, não documentado). Não serve para consultar sem turno |
| `/usage` enviado como prompt | documentado (SDK: a saída de `/usage` chega como `SDKAssistantMessage`) | comando local: `num_turns: 0`, custo 0, sem modelo. Mas o conteúdo é texto para humanos, com data no fuso local ("resets Oct 3, 2:19am (America/Sao_Paulo)"); o campo estruturado `usage_report` da mensagem não é documentado. Frágil para parsing |
| `control_request` `get_usage` | no protocolo da CLI e no SDK TypeScript como `usage_EXPERIMENTAL_MAY_CHANGE_DO_NOT_RELY_ON_THIS_API_YET()`; schema com descrições, marcado **Experimental — the response shape may change**; ausente da documentação pública | estruturado, sem turno, sem endpoint privado chamado pelo D.A.N.T.E. **Escolhido**, com as salvaguardas abaixo |

```text
claude --print --input-format stream-json --output-format stream-json --verbose ...
>> {"type":"control_request","request_id":"dante-1","request":{"subtype":"initialize"}}
>> {"type":"control_request","request_id":"dante-2","request":{"subtype":"get_usage","skip_behaviors":true}}
<< {"type":"control_response","response":{"subtype":"success","request_id":"dante-2","response":{
     "subscription_type":"pro","rate_limits_available":true,
     "rate_limits":{
       "five_hour":{"utilization":6, "resets_at":"2026-10-03T05:19:59.531475+00:00"},
       "seven_day":{"utilization":66,"resets_at":"2026-10-03T04:59:59.531502+00:00"},
       "seven_day_opus":null,"seven_day_sonnet":null,"seven_day_oauth_apps":null,
       "extra_usage":{"is_enabled":false,...},
       "limits":[{"kind":"session","group":"session","percent":6,"resets_at":"2026-10-03T05:19:59.531475+00:00"},
                 {"kind":"weekly_all","group":"weekly","percent":66,"resets_at":"2026-10-03T04:59:59.531502+00:00"}],
       ...},
     "session":{...custo local...},"behaviors":null}}}
```

| Aspecto | Resultado |
| --- | --- |
| consulta sem turno | ok: `initialize` + `get_usage`, sem mensagem `user`; nenhum `result`, nenhum modelo chamado |
| `skip_behaviors: true` | evita a varredura dos transcripts locais de 7 dias (`behaviors: null`) |
| latência | processo + `initialize` 0,4–0,8 s; `get_usage` 0,2–0,3 s quando lê o serviço |
| durante turno | ok: a leitura com turno ativo respondeu em 31 ms e o turno terminou normalmente |
| mesmos argumentos do catálogo de modelos | ok com `--permission-mode plan --restricted --strict-mcp-config --tools Read` (General Mode) |
| API key (`ANTHROPIC_API_KEY`) | `subscription_type: null`, `rate_limits_available: false`, `rate_limits: null`; `initialize.account.apiKeySource` = `ANTHROPIC_API_KEY` |
| sem login | mesmo `get_usage` da API key; `initialize.account.tokenSource` = `none` distingue |
| assinatura | `initialize.account.subscriptionType` (ex.: "Claude Pro") e `get_usage.subscription_type` |
| subtipo desconhecido | `control_response` `subtype: error`, "Unsupported control request subtype: ..." (CLI antiga) |

Semântica comprovada:

- `five_hour` é a janela de sessão: tem o mesmo `resets_at` do item `limits[]` com `kind: session`, e
  o `/usage` interativo mostra o mesmo valor como "Current session". `seven_day` é a semana geral
  (`weekly_all`, "Current week (all models)");
- `utilization` é **percentual 0–100** no `get_usage` (6, 66), mas **fração 0–1** no `rate_limit_event`
  (0,07, 0,66) — escalas diferentes para o mesmo dado;
- `seven_day_opus`, `seven_day_sonnet` e `model_scoped[]` são semanas por modelo, documentadas no schema:
  exibidas à parte, nunca somadas nem no lugar da semana geral. As demais chaves de `rate_limits`
  (`limits`, `seven_day_breakdown`, codinomes) não estão no schema e são ignoradas;
- a resposta não traz a duração da janela: a de 5 h e a de 7 dias vêm do nome do campo documentado.

**Frescor.** A própria CLI guarda a última leitura em `~/.claude.json` (`cachedUsageUtilization`) e
responde dela, sem perguntar ao serviço, se tiver menos de **60 s** (por isso uma consulta num processo
novo levou 3 ms). Se o serviço falhar (inclusive 429), a CLI ainda responde com dados dos headers da
sessão ou da leitura guardada com até **1 h**, sem marcar isso na resposta. O D.A.N.T.E. não consegue
saber a idade exata do dado do Claude; registra o horário da própria consulta e documenta o limite.

## Matriz por agente

| Requisito | Codex 0.159.3 (ChatGPT Plus) | Claude Code 2.1.287 (Pro) |
| --- | --- | --- |
| consumo da janela de sessão | **ok** — `primary` de 300 min, `usedPercent` | **ok** — `five_hour.utilization` |
| consumo semanal | **ok** — `secondary` de 10080 min, `usedPercent` | **ok** — `seven_day.utilization` |
| reset da janela de sessão | **ok** — `resetsAt` (Unix s) | **ok** — `resets_at` (ISO 8601) |
| duração da janela | informada (`windowDurationMins`) | implícita no nome do campo |
| consulta sem turno / sem sessão | **ok** — processo efêmero do `app-server` | **ok** — processo efêmero do stream-json |
| não altera sessão/turno/requests | **ok** — processo próprio, sem `thread/start` | **ok** — processo próprio, sem mensagem `user`, `--no-session-persistence` |
| autenticação suportada | login ChatGPT da CLI | login claude.ai da CLI (assinatura) |
| estabilidade da fonte | método documentado e estável | **experimental**: pode mudar entre versões |
| idade do dado | leitura do serviço a cada chamada | até 60 s (cache da CLI); até 1 h em falha do serviço, sem sinalização |

Nenhum requisito fica pendente nas contas validadas. Pendências condicionais: conta Codex por API key ou
Claude por API key/Bedrock/Vertex não têm cota de assinatura e respondem "indisponível"; a forma do
`get_usage` precisa ser revalidada a cada versão do Claude Code.

## Contrato neutro

O D.A.N.T.E. pergunta à CLI do agente pedido, num processo efêmero no General workspace com o ambiente
do General Mode (como o catálogo de modelos da #77), e recebe:

```text
UsageReport
  Agent          claude | codex
  QueriedAt      instante (relógio do D.A.N.T.E.) em que a resposta chegou
  Session        QuotaWindow | indisponível(motivo)
  Weekly         QuotaWindow | indisponível(motivo)
  Additional[]   janelas de outros buckets/modelos, com rótulo do provedor (nunca somadas)
QuotaWindow
  UsedPercent    0–100, do provedor
  ResetsAt       instante | ausente
  Duration       duração da janela | ausente
UsageQueryFailure (nenhum dado)
  NotAuthenticated | NoSubscription | Unsupported | Timeout | Upstream | Cancelled
```

Regras:

- **valor × indisponível × erro.** Métrica ausente é `indisponível` com motivo; nunca 0 %, 100 % ou
  estimativa por tokens/custo local. Erro de consulta não traz métricas. Falha de uma métrica não
  derruba as outras;
- **identificação.** Codex: a cota geral é só o bucket de `limitId` `codex` (na visão por bucket ou na visão
  única); sem ele, sessão e semana ficam indisponíveis e todo bucket presente vai para `Additional`. Sessão =
  janela de 300 min, semana = janela de 10080 min; qualquer outra duração, ou janela sem duração, vai para
  `Additional`, e duas janelas com a mesma duração comprovada tornam a métrica indisponível. Claude:
  `five_hour` e `seven_day`;
- **domínio.** Percentual válido é 0–100 na escala da fonte (`usedPercent` do Codex, `utilization` do
  `get_usage`); fora disso a resposta é "CLI sem suporte", sem corte nem normalização. No Claude, o
  discriminador `rate_limits_available` precisa ser booleano: só `false` significa conta sem cota de
  assinatura; ausente, nulo ou de outro tipo é "CLI sem suporte";
- **tempo restante** = `ResetsAt − agora`, com relógio injetável, em unidade humana ("2h 13min",
  "menos de 1 min", "3d 4h"). Reset no passado não é renovação confirmada nem duração negativa: vira
  "horário de renovação já passou; consulte de novo";
- **sem cache no D.A.N.T.E.**: cada `/uso` consulta a CLI; a resposta mostra o horário da consulta.
  O frescor interno do Claude fica documentado;
- **erros acionáveis**: sem login → orientar login na CLI do host; API key/sem assinatura → "cota de
  assinatura indisponível para este tipo de autenticação"; método ausente → "atualize a CLI"; timeout
  (20 s) e falha do serviço → tentar de novo; cancelamento → nada exibido;
- **confidencialidade**: e-mail, ids de conta/organização e créditos não aparecem na resposta nem em
  log.

As decisões estão em [AD-31](../../context/ARCHITECTURE_DECISIONS.md).
