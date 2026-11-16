# Comparação agentes.chat × D.A.N.T.E.

Base: D.A.N.T.E. em `main` @ `79c1d01` (2026-11-16), lido em código, testes, `docs/context` e Issues #296, #297,
#32, #60, #234, #290. Semelhança funcional não é equivalência arquitetural: a coluna "Notas" diz onde diferem.

## Legenda

| Classe | Significado |
| --- | --- |
| **A** | já existe no D.A.N.T.E. — não implementar de novo |
| **B** | existe parcialmente — lacuna e como completar |
| **C** | não existe e é necessária — implementação nativa |
| **D** | pode ser integrada externamente (API, MCP, GitHub) |
| **E** | não vale a pena agora — motivo |
| **F** | exige atenção jurídica |

## Diferença de natureza

| | agentes.chat | D.A.N.T.E. |
| --- | --- | --- |
| Produto | chat de equipe multi-tenant onde agentes são membros | orquestrador local, pessoal, de agentes de código via Telegram |
| Execução padrão | modelo por API no servidor; CLIs (Claude Code/Codex) opcionais via runner remoto | CLIs locais, autenticadas por assinatura, como processo filho do Worker |
| Unidade de trabalho | `agent_run` persistente, retomável | sessão interativa em memória (AD-15/16/20) ou job one-shot em memória (AD-06) |
| Interface | web, celular, desktop | Telegram (WebApi ainda scaffold, AD-35) |
| Stack | Bun/TypeScript, Drizzle, Postgres | .NET 10, hexagonal (AD-35), EF Core opcional (AD-43), Postgres para o Brain (AD-34) |

## Matriz por funcionalidade

### Chat e experiência

| Funcionalidade | Classe | D.A.N.T.E. hoje (referência) | Lacuna / proposta |
| --- | --- | --- | --- |
| Conversa com agente, multi-turno, sessão viva | **A** | `SessionRegistry`, `AgentSession`, drivers (AD-15/16/20/23); `InteractiveSessionEndToEndTests`, `AgentSessionTests` | — |
| Fila e steer durante o turno | **A** | AD-16 (queue padrão, steer explícito) | o deles lê mensagens no próximo passo; o nosso já é mais explícito |
| Eventos neutros de execução | **A** | `AgentEvent.cs` (turno, delta, ferramenta, arquivo, approval, input, uso) | — |
| Entrega com retry, `/resend`, digitação | **A** | `TelegramDeliveryService` (AD-21) | — |
| Canais, DMs, threads, menções de agentes | **B** | Telegram privado; agente escolhido por `/claude`, `/codex`, `/agent` (AD-27) | menção a **agente registrado** (handle) quando houver cadastro; tópicos de grupo/fórum do Telegram como "thread de tarefa" **[R]** |
| Chat web próprio + WebSocket | **C** (tardio) | WebApi só com health (`src/Dante.WebApi/Program.cs`) | após o ledger de execuções; SSE ou SignalR sobre eventos persistidos **[R]** |
| Recibos de roteamento ("quem decidiu") | **C** | — | mostrar no Telegram quem foi acionado e por qual regra |
| Notificações/caixa única | **B** | Telegram é a caixa | lista de "esperando você" (aprovações + perguntas pendentes) por comando |
| Mobile/desktop/voz/GIF/14 idiomas | **E** | — | fora do propósito; Telegram cobre celular |

### Agentes

| Funcionalidade | Classe | D.A.N.T.E. hoje | Lacuna / proposta |
| --- | --- | --- | --- |
| Registro persistente de agentes (handle, papel, instruções, dono) | **C** | só `AgentKind { Codex, Claude }` (`src/Dante.Domain/Agentes/AgentKind.cs`) | entidade `AgenteRegistrado` (PT-BR, Guid, AD-36) com driver, modelo, esforço, perfil, instruções, estado |
| Modelo e esforço por agente | **B** | por usuário + agente da CLI, validado e fixado ao iniciar (AD-25/26), `AgentModelCatalog` | mover a escolha para o agente registrado, mantendo a validação da CLI |
| Perfil de permissão por agente | **B** | perfil por sessão, padrão por usuário (AD-24) | perfil padrão no agente registrado; sessão herda e pode restringir |
| Orçamento em USD com reserva | **E** | cotas de assinatura pelo `/uso` (AD-31) | CLIs por assinatura não têm custo por chamada; usar limites de concorrência, tempo e turnos |
| Habilitar/desabilitar com execuções em andamento | **C** | — | desabilitar impede novas execuções; as ativas terminam ou são canceladas explicitamente |
| Audiência por pessoa/papel | **E** (por ora) | allowlist única (AD-05) | só se houver multiusuário |
| Agente embutido "braço direito" | **E** | — | conversa natural do Brain (#157) cobre parte; não criar agente mágico |
| Templates de time e checklist | **E** | — | baixo valor para um usuário |

### Roteamento e coordenação

| Funcionalidade | Classe | D.A.N.T.E. hoje | Lacuna / proposta |
| --- | --- | --- | --- |
| Resolução determinística de agente/contexto | **A** | `AgentContextResolver` (AD-08/14/27), sem inferência por prompt | — |
| Candidatos por código + política + decisão gravada | **C** | — | regra para grupos: mensagem ao grupo vai ao coordenador; menção explícita vai ao agente; gravar decisão |
| Roteador por LLM/classificador (Jev, modelo pequeno) | **E** (agora) | AD-08 proíbe inferência de repositório; #32 deixou seleção por IA fora | só depois de medir necessidade; se vier, só pontua, nunca concede |
| Juiz de destinatários em várias menções | **E** | — | regra simples: cada menção explícita é destinatária |
| Coordenador de grupo | **C** | — | explícito, por grupo, visível; não "pega o que sobrar" (lição ADR 0093→0137) |
| Grupos de agentes | **C** | — | o deles é visual; o nosso precisa ser de execução (coordenador, participantes, papéis, política, versão) |

### Execução

| Funcionalidade | Classe | D.A.N.T.E. hoje | Lacuna / proposta |
| --- | --- | --- | --- |
| Drivers Claude Code (stream-json) e Codex (app-server) | **A** | `ClaudeSessionDriver`, `CodexSessionDriver` (AD-15/18/19); testes de driver | — |
| Approvals da CLI roteadas ao humano | **A** | `--permission-prompt-tool stdio`, `can_use_tool`, requests do app-server (AD-15/22); `TelegramInlineApprovalTests` | o deles usa um MCP (`permission_prompt`) porque o processo é remoto |
| Contenção de processo | **A** (superior) | cgroup v2 por sessão estrita (AD-63), `ContencaoNativaTests` | — |
| Ambiente filtrado e segredos por binding | **A** | AD-09/10 | — |
| Execuções persistentes e retomáveis (estado, passos, transcript) | **C** | sessões e jobs em memória (AD-06/20); "jobs e histórico somente em memória" em `CURRENT_STATE` | ledger de execuções no Postgres: execução, passos, eventos-chave, decisões |
| Fila durável com lease e idempotência | **C** | — | tabela de fila com `FOR UPDATE SKIP LOCKED`, lease, `dedupe`, retries; workers no Worker |
| Execução paralela com limites | **B** | uma sessão ativa por usuário (AD-20); várias sessões possíveis | limite global e por agente; slot por repositório |
| Worktree por tarefa | **C** | não há código de worktree em `src/`; os `dante-wt-*` do homelab são prática manual do harness | worktree por tarefa de escrita, criada e removida pelo Worker; leitura pode compartilhar checkout |
| Identidade Git e prefixo de branch por agente | **C** | AD-58: o D.A.N.T.E. não executa git de escrita | `GIT_CONFIG_GLOBAL` por sessão e prefixo de branch; push protegido |
| Repositórios vinculados | **A** | AD-58 | o concorrente não tem equivalente |
| Trabalho longo em segundo plano | **B** | sessão continua viva; fila (AD-16) | liberar a conversa enquanto a execução segue, com atualizações periódicas |
| Missões (plano vivo, revisões) | **C** (depois) | Agent Harness é processo do repositório, não produto (PROJECT_CONTEXT) | plano da tarefa como checklist visível; revisões como aprovações |
| Computadores remotos (runner) | **E** | Worker local; #296 veta workers remotos sem necessidade | adiar; o contrato de driver já isola o canal |
| Navegador/desktop do agente | **E** | — | fora do foco |
| Agendamento de rotinas (cron, eventos, webhook) | **C** | sem agendador no produto | rotinas como execuções do ledger, com autoridade "rotina" e entrega explícita |

### Comunicação, aprovações, observabilidade

| Funcionalidade | Classe | D.A.N.T.E. hoje | Lacuna / proposta |
| --- | --- | --- | --- |
| Pergunta ao humano (input) | **A** | AD-30, `TelegramInputInteractionTests` | — |
| Delegação rastreada (`ask`/`tell`) | **C** | — | entidade de delegação + ferramentas MCP do D.A.N.T.E. |
| Anti-ciclo, profundidade, anti-duplicidade, retorno único | **C** | — | regras determinísticas no caso de uso de delegar |
| Aprovação de ações do D.A.N.T.E. (não da CLI) | **C** | — | hash dos parâmetros, validade, decisor, execução única, persistidas |
| "Aprovar sempre" por tipo de ação | **E** (agora) | — | depois de existir aprovação persistida |
| Pedido de aprovação de negócio (`request_approval`) | **C** (baixo) | — | útil para rotinas; depende do anterior |
| Task Manager (o que roda, parar com motivo) | **B** | `/status`, `JobRegistry` em memória | visão de execuções ativas e encerradas, por agente, com motivo de parada informado ao agente |
| Auditoria de execuções | **B** | auditoria do Brain (#149); execução sem trilha | trilha no ledger |
| Limites de autonomia (trecho, máximo de repasses, PRs/h) | **C** | — | junto com delegação |
| Métricas | **B** | métricas do Brain em JSONL (AD-47) | métricas de execução no ledger |

### Integrações e conhecimento

| Funcionalidade | Classe | D.A.N.T.E. hoje | Lacuna / proposta |
| --- | --- | --- | --- |
| Servidores MCP por sessão | **A** | `AgentToolServer`, `dante_brain` (AD-56), `dante_planilhas` (AD-55) | — |
| Conectores em duas camadas (global + por agente) | **B** | conjunto fixo por sessão (`IAgentToolServers`) | concessões por agente registrado; catálogo que nega o desconhecido |
| Expor o D.A.N.T.E. como servidor MCP externo | **D** | — | só se houver cliente; o SDK MCP .NET cobre |
| Integração com o agentes.chat | **D** | — | por `/mcp` ou API como cliente, nunca por código |
| Skills da plataforma | **B** | skills do Claude Code nos repositórios; skills do harness em `.claude/skills` | registro próprio só para agentes sem repositório |
| Memória, busca, contexto | **A** | Braúna (AD-33…65), MCP `dante_brain` | não duplicar; ver [BRAIN-E-MEMORIA](BRAIN-E-MEMORIA.md) |
| Aprendizado com recusa/correção | **B** | captura em candidato (#157) | recusa de aprovação e correção explícita viram candidatos |
| Wiki, Drive, dashboards | **E** | repositório + Brain | fora do foco |
| Issues/quadro de tarefas | **D** | GitHub Issues é a fonte (harness, #234) | tarefa do D.A.N.T.E. referencia Issue/PR, não replica o quadro |
| CI | **D** | Epic #234 (porta de provedor de CI) | o concorrente não tem; seguir a #234 |
| Multi-tenant, billing, operador | **E** | AD/#296 vetam SaaS agora | — |
| Código, prompts, assets, catálogo deles | **F** | — | ver [LICENCAS](LICENCAS.md) |

## Principais diferenças arquiteturais

1. **Persistência da execução.** Lá, toda execução é uma linha com estado e passos; aqui, sessões e jobs morrem com
   o Worker. Multiagente com delegação e aprovação que atravessam reinícios depende de fechar essa lacuna.
2. **Onde roda o agente.** Lá, o cérebro padrão é a API e as CLIs são remotas (runner); aqui, as CLIs locais são o
   caminho principal. Por isso o D.A.N.T.E. não precisa de Model Gateway com orçamento em USD nem de bridge
   remoto, mas precisa controlar **concorrência de processos** e **conflito em disco**.
3. **Quem decide.** Lá, uma cadeia de modelos pontua e o código decide; aqui, tudo é explícito (AD-08/27). Para
   grupos, manter o explícito e adicionar só regras de grupo.
4. **Arquitetura de código.** Lá, módulos funcionais com SQL junto da regra; aqui, hexagonal com Application em
   PT-BR (AD-35…54). Os conceitos se transportam; a forma não.
5. **Isolamento.** O nosso é mais forte em contenção (AD-63); o deles é mais forte em identidade Git, prefixo de
   branch, proxy Git e política por tipo de run.
6. **Memória.** A Braúna é mais rigorosa em proveniência e confirmação; o concorrente aprende mais
   automaticamente. Nenhum dos dois usa a memória para coordenar.
