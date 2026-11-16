# Plano de migração incremental

Princípio: cada fase entrega algo utilizável, mantém os fluxos atuais idênticos para quem não usa o recurso novo e
pode ser desligada por configuração até virar padrão. Sem reescrita.

## Chave de ativação

`Orquestracao:Habilitada` (padrão `false` até a fase 2 ser validada no homelab) + `ConnectionStrings:Dante`.

| Banco | Chave | Comportamento |
| --- | --- | --- |
| ausente | qualquer | Exatamente o de hoje; comandos novos respondem "indisponível sem banco" |
| presente | `false` | Exatamente o de hoje |
| presente | `true` | Ledger como fonte de verdade para jobs, sessões e execuções novas |

Migrations nunca rodam no startup (AD-53); aplicadas por comando administrativo explícito.

## Fases por componente

### 1. Jobs existentes (`/claude`, `/codex`)

| Fase | Mudança | Compatibilidade |
| --- | --- | --- |
| 1a — espelho | `JobRegistry` publica criação/início/fim numa porta `IRegistroDeExecucoes` (execução `Avulsa`); falha de gravação loga e não bloqueia | `J…`, `/status`, `/cancel` iguais |
| 1b — fila | `/claude` e `/codex` criam execução `Avulsa` + trabalho; o processador roda o mesmo `ClaudeRunner`/`CodexRunner`; `JobRegistry` vira índice do processo vivo (cancelamento) | O usuário ainda vê `J…` (= `E…` com prefixo legado) e o mesmo texto; reinício mostra "interrompido" em vez de sumir |
| 1c — AD-06 substituída | Persistência obrigatória com a chave ligada; falha de gravação recusa o job (fail-closed) | Registrado na AD-66 |

Risco: duplicar estado entre `JobRegistry` e ledger. Mitigação: a partir de 1b o registry não guarda estado de
negócio, só `CancellationTokenSource` por execução viva.

### 2. Sessões interativas

| Fase | Mudança | Compatibilidade |
| --- | --- | --- |
| 2a | Porta `IRegistroDeSessoes` no `SessionRegistry` (implementação nula sem banco); metadados persistidos; códigos `S…` de sequência do banco | Nenhuma mudança visível; contadores não reiniciam |
| 2b | Recuperação: sessões de boot anterior → `Interrompida`; `/session list` mostra-as como encerradas pelo reinício; varredura de scopes `dante-agent-*` órfãos | Substitui o "não encontrada" da AD-20 por "interrompida pelo reinício" |
| 2c | `/session start <handle>` com agente registrado | `claude`/`codex` continuam válidos |

Conversa session-first (AD-23), fila/steer (AD-16), clear/compact (AD-32), modo (AD-24) não mudam.

### 3. Agent Harness

Nada muda no processo de engenharia do repositório (`docs/development`). O produto passa a oferecer o **roteiro**
equivalente (implementar → revisar → corrigir → aprovação) como grupo configurável. O harness é a especificação; o
roteiro é a execução. Dogfooding: usar o roteiro do D.A.N.T.E. numa Issue real do próprio repositório antes de
torná-lo padrão.

### 4. Drivers Claude/Codex

Sem mudança de protocolo. Entradas novas usam campos que já existem (`ContextInstructions`, `ToolServers`,
`AdditionalDirectories`, `Profile`, `ModelSelection`). `dante_coordenacao` é mais um `IProvedorDeFerramentas`.
Retomada de conversa upstream (`--resume`, thread não efêmera) fica para spike posterior.

### 5. Aprovações

| Fase | Mudança |
| --- | --- |
| 5a | Approvals da CLI registradas no ledger (auditoria), fluxo AD-22 intacto |
| 5b | `PedidoDeAprovacao` do D.A.N.T.E. para gate humano de roteiro e plano do coordenador |
| 5c | Gateway com hash/escopo/execução única para ferramentas do D.A.N.T.E.; `dante_brain` e `dante_planilhas` migram para trás dele sem mudar a experiência |

### 6. Worktrees

Só execuções novas (`Tarefa`, `Roteiro`, `Coordenada`, `Delegada` com escrita) usam worktree. Sessões comuns e
`/claude @repo` continuam no checkout principal. Requer a AD-71 (complemento da AD-58).

### 7. Telegram

Continua a interface principal. Comandos novos, mínimos: `/agentes`, `/agente …` (cadastro), `/grupos`,
`/grupo …`, `/tarefa`, `/execucoes`, `/execucao E… [cancelar|pausar|retomar]`, `/aguardando`. Eventos de execução
chegam como mensagens curtas (etapa iniciada/concluída, pedido de aprovação, resultado), agrupados pelo mesmo
`TelegramDeliveryService`. `/status` ganha uma seção de execuções ativas.

### 8. Chat WebApi

Último: lê o ledger e os eventos. Nenhuma fase anterior depende dele.

## Riscos de compatibilidade

| Risco | Mitigação |
| --- | --- |
| Postgres vira obrigatório para quem só usa Telegram | Chave + ausência de banco preservam o comportamento atual (mesma postura do Brain, AD-54) |
| Ids `J…`/`S…` mudam de formato ou colidem após reinício | Sequências do banco com o mesmo formato |
| Latência extra do banco no caminho da conversa | Conversa comum não grava por turno no caminho síncrono: o projetor grava eventos em segundo plano, por lote |
| Banco indisponível no meio de uma sessão | Sessão continua (runtime em memória); eventos ficam num buffer limitado e o usuário é avisado; execuções novas recusam |
| `SessionRegistry` com mais responsabilidades | Só uma porta nova e opcional; nenhuma regra de orquestração entra nele |
| Contenção AD-63 ainda não ativa no serviço do homelab | Paralelismo (fase 11 do roadmap) depende de ativar o modo estrito; até lá, limite global = 1 processo de execução por vez além da conversa |
| Testes atuais | Toda fase roda a suíte completa; regras novas com testes de domínio, de fila contra `dante-postgres-test` (opt-in, como `PersistenciaTests`) e E2E com `FakeSessionDriver`/`Dante.ProcessProbe` |
| AD-34 escopo "não migrar estado volátil" | A AD-66 proposta registra a decisão nova explicitamente; a AD-34 não é editada |
