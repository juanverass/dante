# Plataforma multiagente — proposta arquitetural (#297)

> **Status: PROPOSTA para revisão humana.** Nada aqui está aprovado nem implementado. Spike #297, Epic #296.
> As ADRs propostas ([ADRS_PROPOSTAS](ADRS_PROPOSTAS.md)) não estão em `ARCHITECTURE_DECISIONS.md`.

Base: `main` @ `79c1d01` (2026-11-16), lida em código, testes, ADRs e Issues; investigação do concorrente em
[`docs/research/agentes-chat/`](../../research/agentes-chat/README.md), referenciada e não duplicada.

## Arquitetura recomendada em uma página

1. **Três conceitos separados.** *Agente* (identidade versionada, configurável), *sessão de agente* (uma conversa
   upstream viva com um agente — o `SessionRegistry` de hoje) e *execução* (unidade de trabalho persistente com
   etapas, dependências, delegações, aprovações e resultado). Turno não é execução; conversa comum continua leve.
2. **PostgreSQL é a fonte de verdade do trabalho; a memória é o runtime.** Schema `orquestracao` no mesmo banco do
   Brain, com `OrquestracaoDbContext` próprio. Processos, drivers, fila de turnos e deltas continuam em memória.
   Sem banco, o D.A.N.T.E. funciona exatamente como hoje.
3. **Fila durável no próprio PostgreSQL**: outbox transacional, `FOR UPDATE SKIP LOCKED`, lease com heartbeat e
   *fencing*, deduplicação, timers. Recuperação na inicialização para órfãos e aplica política de retomada por
   etapa — escrita nunca é refeita sem confirmação.
4. **Grupos são unidades de execução versionadas.** Primeiro **roteiro determinístico** (o pipeline implementar →
   revisar → corrigir → aprovação humana, executado por código); depois **coordenador como papel** de um agente
   registrado, com plano estruturado validado pelo orquestrador.
5. **Delegação é uma linha rastreada**, exposta às CLIs por um servidor MCP `dante_coordenacao` no padrão do
   `dante_brain` (AD-56), com limites determinísticos (anti-ciclo, profundidade, dedupe, orçamento) e resultado como
   dado não confiável com autor.
6. **Uma worktree por execução de escrita**, um escritor por worktree, leitura em paralelo; o D.A.N.T.E. ganha só
   `git worktree add/remove/prune/list`. Push/PR pelo agente; merge sempre humano.
7. **Gateway único para as ferramentas do D.A.N.T.E.** e aprovações persistidas vinculadas a hash, escopo, validade e
   execução única. Ferramentas nativas das CLIs seguem governadas por perfil, approvals da CLI e contenção.
8. **Chat é interface**: a WebApi lê o ledger, enfileira comandos e transmite eventos por SSE; nunca executa agente.
9. **Brain fornece conhecimento; não coordena.** Estado de execução vem do ledger.

## Documentos

| Documento | Conteúdo |
| --- | --- |
| [INVENTARIO](INVENTARIO.md) | estado real de cada componente, o que reutilizar e o que muda; situação das ADs |
| [DOMAIN_MODEL](DOMAIN_MODEL.md) | agente, sessão, execução, grupo, conversa, autoridade; versionamento; diagrama de entidades |
| [PERSISTENCE_AND_EXECUTIONS](PERSISTENCE_AND_EXECUTIONS.md) | persistência híbrida, tabelas, estados, ciclo de vida, recuperação, idempotência, worktrees, pipeline |
| [DURABLE_QUEUE](DURABLE_QUEUE.md) | alternativas, modelo recomendado, SQL de reivindicação, respostas às 10 perguntas |
| [AGENT_GROUPS](AGENT_GROUPS.md) | grupos versionados, invariantes, roteiro × coordenador, endereçamento, matriz de falhas |
| [DELEGATION](DELEGATION.md) | contrato, fluxo, validações, `dante_coordenacao`, comunicação, consolidação |
| [SECURITY_AND_APPROVALS](SECURITY_AND_APPROVALS.md) | limite real de controle, gateway, aprovações com hash, least privilege, riscos |
| [ARCHITECTURE](ARCHITECTURE.md) | componentes, camadas, interfaces C#, eventos, tempo real, chat, fronteira com o Brain |
| [MIGRATION_PLAN](MIGRATION_PLAN.md) | migração por componente, chave de ativação, riscos de compatibilidade |
| [IMPLEMENTATION_ROADMAP](IMPLEMENTATION_ROADMAP.md) | 11 etapas verificáveis, dependências, paralelismo, Issues sugeridas |
| [ADRS_PROPOSTAS](ADRS_PROPOSTAS.md) | AD-66 a AD-73 propostas, não aprovadas |

## Conflitos encontrados com a arquitetura atual

| Conflito | Onde | Tratamento proposto |
| --- | --- | --- |
| Jobs só em memória | AD-06 | Substituída pela AD-66 na implementação |
| Sessões "não encontradas" após reinício | AD-20 | Complementada: registro persistido, estado `Interrompida`; runtime igual |
| "Não migrar estado volátil de sessões/jobs" | AD-34 (escopo da Epic #133) | Nova decisão explícita na AD-66; AD-34 não é editada |
| `DanteDbContext` exige autorização e filtros do Brain | AD-43/54, `Data/DanteDbContext.cs` | DbContext próprio no mesmo banco |
| D.A.N.T.E. não executa `git` de escrita; sem bloqueio de checkout | AD-58 | AD-71: conjunto fechado de comandos de worktree; push/merge continuam fora |
| "Worktrees automáticos", "fila persistente" e "entrada HTTP que executa agentes" fora do sistema | PROJECT_CONTEXT, limites | Alterados pelas AD-66/67/71/73 quando implementadas |
| `@` já significa repositório | AD-27 | Handles de agente sem `@`; agente/grupo por comando explícito |
| Scope de contenção pode sobreviver ao Worker morto | AD-63 (sem varredura na inicialização) | Varredura de `dante-agent-*` órfãos na recuperação |
| Approval da CLI expira em 5 min | AD-22 | Mantido para a CLI; portões longos viram aprovações do D.A.N.T.E. persistidas |
| Contenção estrita ainda não ativa no serviço do homelab | AD-63 | Paralelismo condicionado à ativação (autorização operacional própria) |
| #297 sem label `status:*` | backlog.md | Registrado no claim de turno; a correção da label é decisão humana |

## Pontos que precisam de aprovação

1. **PostgreSQL como requisito dos recursos multiagente** (sem banco, nada de agentes/grupos/execuções; legado intacto).
   Recomendado: sim. Impacto: o banco do Brain passa a ser também operacional; backup cobre o schema novo.
2. **Mesmo banco, schema `orquestracao` e DbContext próprio** (em vez de banco separado). Recomendado: mesmo banco.
3. **Execuções novas sempre em worktree; legado continua no checkout principal.** Recomendado: sim.
4. **D.A.N.T.E. passa a executar `git worktree`** (AD-71), mantendo push/PR com o agente e merge humano.
5. **Roteiro determinístico antes do coordenador LLM.** Recomendado: sim; coordenador só na etapa 8.
6. **Sintaxe**: handles sem `@`; comandos `/tarefa <handle>`, `/grupo <nome>`, `/session start <handle>`.
7. **Sem retomada da conversa upstream** na primeira fase (Codex efêmero); continuidade por ledger + Brain.
8. **Limites padrão**: 3 processos globais, profundidade 2 (máx. 3), 8 delegações por raiz, 2 rodadas de revisão,
   aprovação do D.A.N.T.E. válida por 24 h (gate de roteiro sem expiração).
9. **Custo por dinheiro fora**; limites por turnos, tempo e tokens informados pela CLI.
10. **Lista de Issues** do [roadmap](IMPLEMENTATION_ROADMAP.md#issues-filhas-sugeridas-lista-enxuta) e criação só
    das três primeiras após a aprovação.
11. **Autenticação da WebApi** antes do chat web (decisão própria, na etapa 10).
