# Orquestração: grupos, coordenação, comunicação

## Grupos

- `agent_groups` pertence ao workspace; um agente está em no máximo um grupo (`agents.group_id`,
  `group_position`); até 50 grupos **[C][D]** ADR 0083.
- Grupo é **organização visual e de listas** (setor com cor, ícone, líder e substituto humanos — ADR 0148), não
  unidade de execução: não tem coordenador próprio, política de participação, plano nem estado **[C][O]**
  (`POST …/agent-groups` devolveu `leadUserId`/`backupUserId` de pessoas).
- **[I]** O concorrente não tem "tarefa para um grupo". O trabalho coletivo nasce da conversa num canal com
  vários agentes e das consultas entre eles.

## Coordenador: o que tentaram e o que ficou

1. **ADR 0093 (31/10)**: um agente por workspace com `agents.coordinator`; recebia o que o roteador não dava a
   ninguém e repassava por @menção. **Substituída em 3 dias.**
2. **ADR 0137 (3/11)**: um agente embutido, "braço direito", por workspace: monta e opera o time por conversa,
   posta **cartões de ação** (criar agente, mudar configuração, convidar pessoas) que a pessoa aplica com um clique,
   vê o status do time e repassa trabalho. Não ganha permissões extras: o gateway é o mesmo **[D][O]**.
3. Roteador continua decidindo quem responde mensagens sem menção (ADR 0074).

**[I]** Lição: coordenação implícita ("o que sobrar vai para o coordenador") ficou invisível e foi rejeitada.
O que sobreviveu é coordenação **explícita e visível**: menções, consultas rastreadas, cartões, recibos.

## Comunicação entre agentes

| Mecanismo | Semântica | Rastro |
| --- | --- | --- |
| `ask_agent` | pergunta privada; o solicitante não bloqueia; resposta volta como aviso e acorda a run | `agent_consultations(kind=question)` |
| `tell_agent` | repasse de trabalho ou notícia; o resultado volta uma vez, consolidado | `agent_consultations(kind=note)` |
| @menção de agente em resposta | só acorda se o juiz ler como pedido de ação (crédito/referência não acorda) | `routing_decisions` |
| `hand_off` | rotina em segundo plano entrega relatório a uma run normal | `routine_runs` |
| `follow_up` | lembrete para o próprio agente voltar a algo | `follow-through.ts` |

Regras **[C][D]** (`consultations.ts`, ADR 0034, 0095, 0097):

- a consulta roda numa DM privada `a:<id>|a:<id>` com os **escopos do consultado**; nada é concedido;
- a autoridade segue a pessoa na raiz da cadeia (`personBehindRun`), conferida elo a elo pelas linhas de consulta;
- sem ciclos, profundidade `MAX_CONSULTATION_CHAIN = 4`; dentro da DM entre agentes `ask_user` é recusado;
- não repassar o que a pessoa já pediu diretamente ao outro agente (`sameRequestRun`, checagem de duplicidade);
- nada expira enquanto o colega trabalha; o delegante vê, a cada turno, uma seção com o trabalho que repassou (até 8 itens);
- sub-repasses se consolidam na resposta (`handBack`): "[@diogo, a quem @rita repassou parte]: …".

## Execução sequencial e paralela

- Várias menções ou "mensagem para todos" → até 4 runs em paralelo, uma por agente **[C]** `maxAgents`.
- Uma run ativa por agente por conversa (`startOrJoinRun`); mensagens extras entram na caixa da run **[D]** ADR 0121.
- Trabalho em segundo plano libera a conversa; limites de trabalho paralelo por agente fazem parte dos freios
  **[D]** ADR 0121/0131 §7.
- Computador: slots por máquina (padrão 3) e fila de turnos ("calm waits") **[D]** ADR 0029 §4, 0032.
- Missões de código: **um worktree e uma branch por missão**; itens que escrevem rodam em sequência, leitura pode
  ser paralela, porque branches paralelas viram conflito no fim **[D]** ADR 0044.

## Consolidação e conflitos

- Não há etapa de "merge de resultados" com modelo: o delegante recebe as respostas como avisos e escreve a resposta
  final; as partes aparecem como bloco de consultas sob a resposta, cada uma abrindo a conversa entre os agentes
  (somente leitura) **[D]** ADR 0034 adendos 2026-10-29/30.
- Conflito explícito: o bloco de turno orienta o agente a não repetir nem endossar a resposta de outro agente e a
  corrigir um colega em particular, por `tell_agent` **[O]** prompt capturado (paráfrase; texto fora do repositório).
- **Team actions**: toda ação externa vira linha `team_actions` com alvo normalizado; antes da próxima ação no mesmo
  alvo o gateway segura a chamada uma vez e avisa o agente — evita dois agentes atuarem no mesmo PR/ticket
  **[D]** ADR 0064.

## Responsáveis por cada ação

`agent_runs.agent_id` + `trigger_message_id` + `stretch_id`/`hop`, `agent_run_steps` numerados,
`approvals.decided_by_user_id`, `audit_log` (`tool.call` com `run_id`, `channel_id`, `on_behalf_of_user_id`,
`outcome`) **[C][O]**.

## Limites contra loops e ruído

| Limite | Valor/regra | Fonte |
| --- | --- | --- |
| Trecho (`stretch`) | trabalho autônomo desde a última ação de uma pessoa; qualquer ação humana reinicia | ADR 0089 **[D]** |
| `AGENT_MAX_HOPS` | 100, só como rede de segurança | config **[O]** |
| `AGENT_MAX_TOOL_TURNS` | 200 (rede de segurança) + detecção de "andando em círculos" | config **[O]**, `stuck.ts` |
| Turnos extras pelo controlador | ≤ 3 por run | ADR 0097 |
| "Perguntou, espera" | turno que termina em pergunta não continua sozinho | ADR 0131 §1 |
| Trabalho não pedido | `maxUnrequested: 2`, `maxPrsPerHour: 3` por agente | **[O]** freios |
| Alertas | ruído vai para digest; item já em trabalho junta-se ao trabalho | ADR 0131 |

## Lições para o D.A.N.T.E. [R]

1. Modelar **delegação como entidade persistente** (pedido, executor, estado, resposta, cadeia), não como texto.
2. Grupo útil para nós é diferente do deles: precisamos de **grupo de execução** (coordenador + participantes +
   papéis + política), que eles não têm. Tomar deles a parte de comunicação e limites, não o modelo de grupo.
3. Coordenador deve ser **explícito** (configurado no grupo e visível nos recibos), nunca um "pega o que sobrar".
4. Paralelismo só para leitura/análise; escrita no mesmo repositório em sequência, num worktree por tarefa.
5. Limites determinísticos desde o primeiro incremento (profundidade, trecho, máximo de repasses, timeout).
