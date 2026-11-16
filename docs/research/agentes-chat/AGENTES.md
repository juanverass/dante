# Agentes: cadastro, identidade e ciclo de vida

## Como um agente é criado

- `POST /api/app/organizations/{orgId}/agents` com `name` e `handle` obrigatórios; opcionais `role`,
  `instructions`, `model`, `credentialId`, `ownerUserId`, `monthlyBudgetUsd`, `audience`, `appearance`,
  `brakes`, `approvalsLevel`, `autonomy`, `setup` **[C][O]** OpenAPI em `~/labs/agentes-chat/evidence/openapi.json`.
- Quem pode: permissão `create_agents` / `manage_agents` **[O]** (lista de permissões do dono do workspace).
- Escopos padrão de um agente novo vêm do "último setup" (ADR 0094) **[D]**.
- O agente nasce com um **checklist de configuração** no próprio chat (conectar IA, apps, canais, audiência,
  instruções/limite) em vez de um assistente de sete passos **[D]** ADR 0155 §2.
- O workspace já nasce com um agente embutido (`builtin = 'right_hand'`, "Lodi"), que não pode ser arquivado
  **[O][D]** ADR 0137.

## Identidade

| Atributo | Regra |
| --- | --- |
| `handle` | único entre agentes e pessoas, inclusive arquivados; renomear move o handle e guarda o antigo em `previous_handles`, que continua resolvendo menções **[D]** ADR 0008 adendo |
| `role` + `instructions` | texto livre; entram na seção de instruções do agente no prompt **[O]** |
| Contexto da organização | entra em todo prompt, numa seção própria **[O]** |
| Dono (`owner_user_id`) | recebe aprovações e alertas de orçamento **[D]** |
| Audiência | `everyone` / `people` / `owner` + papéis; decide quem conversa e quem o vê nas listas **[C]** |
| `consultable` | se outros agentes podem consultá-lo **[C]** |

**[I]** Identidade e permissões são colunas do banco, nunca derivadas do prompt. O prompt só *descreve* o que o
agente e os colegas podem fazer, e o gateway confere de novo a cada chamada.

## Modelo e "cérebro"

- `model`: id do catálogo, id OpenRouter, `custom:<credentialId>:<modelo>` ou `auto` (cada run escolhe nível e
  esforço) **[O]** — a API recusou `stub-1` e aceitou `custom:…:stub-1`.
- `brain`: `model` (API) ou `claude_code`, `codex`, `cursor`, `opencode`, `grok`, `antigravity` (CLI no computador,
  com plano/assinatura) **[C]** `brains/types.ts`. Conta por agente: pasta `CLAUDE_CONFIG_DIR`/`CODEX_HOME` no
  computador; o servidor nunca vê o token **[D]** ADR 0041.
- Credenciais de provider ficam na organização, cifradas por envelope com chave derivada de `AGENTES_SECRET`; o
  agente aponta para uma **[D]** ADR 0007.

## Ferramentas e permissões

- Escopos em `agent_scopes (agent_id, connection_id, scope)`; `connection_id` nulo = escopo embutido
  (`context:read`, `memory:write`, `drive:*`, `issues:write`, `sandbox:run`…) **[C]** ADR 0015.
- Conceder escopo de conector é de admin; o dono do agente pode no máximo reduzir **[D]** ADR 0016.
- Nível de aprovações por agente: `always` / `risky` (padrão) / `never` **[C]** `agents.approvals_level`, ADR 0173.
- Freios de autonomia por agente (`autonomy_brakes`) **[O]**: `costly`, `maxUnrequested: 2`, `maxPrsPerHour: 3`,
  `callAgents: "all"`, `project`, `otherProjects`.

## Orçamento

- `monthly_budget_nano_usd` por agente; contador por mês em `agent_budget_periods (spent, reserved)`; antes de cada
  chamada o gateway reserva o pior caso com `SELECT … FOR UPDATE`, encolhe `maxTokens` para caber e recusa abaixo
  de 256 tokens de saída **[C][D]** `gateway.ts`, ADR 0012.
- Chamadas por plano (Claude Code/Codex) custam zero e são medidas pelos limites do plano **[D]** ADR 0032/0063.

## Habilitar, desabilitar, remover

- Arquivar: o agente sai das listas e não é candidato; a coordenação é limpa **[D]** ADR 0082/0093.
- Excluir (ADR 0082) libera handles antigos; regras "aprovar sempre" do agente são apagadas **[D]** ADR 0015.
- Suspensão operacional (`hold`) para o workspace ou agente é aplicada no gateway, hub, executor, rotinas, roteador
  e juiz **[D]** `CLAUDE.md` deles, `standing.ts`.
- Runs em andamento: cancelar é `POST …/runs/{id}/cancel`; o agente é avisado com motivo (`run_stopped`) para não
  retomar sozinho **[D]** ADR 0128.

## Lições para o D.A.N.T.E. [R]

1. Separar **definição do agente** (linha persistente, versionada por campos) de **execução** (run) e de
   **sessão upstream** (processo da CLI). No D.A.N.T.E. hoje só existem `AgentKind { Codex, Claude }`, preferências
   por usuário (AD-13/24/25/26) e sessões em memória.
2. Handle estável e único, com histórico de nomes, é o que torna menções e delegações confiáveis.
3. Ferramentas e aprovações por agente devem ser dados, consultados a cada chamada, não texto do prompt.
4. Orçamento em USD é secundário para quem usa assinatura; o que importa são limites de concorrência, tempo,
   turnos e uso de plano (já lido pelo `/uso`, AD-31).
