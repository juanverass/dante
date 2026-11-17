# ADRs propostas

> **Status de todas: PROPOSTA — não aprovada.** Nenhuma foi registrada em
> [`ARCHITECTURE_DECISIONS.md`](../../context/ARCHITECTURE_DECISIONS.md). Cada uma entra lá pelo PR da Issue que a
> implementa, depois da aprovação humana desta proposta. As ADs existentes não são editadas retroativamente: quando
> uma for superada, o PR de implementação muda só o seu `Status:` para "substituída por AD-NN".
>
> A numeração (AD-66 em diante) é provisória e segue a última AD vigente (AD-65).

## AD-66 — Execução persistente como unidade de trabalho; PostgreSQL como fonte de verdade e memória como runtime

> Registrada em `ARCHITECTURE_DECISIONS.md` pela #299 (fase 1a, espelho). O texto vigente é o de lá.

- **Contexto**: jobs (AD-06) e sessões (AD-20) existem só em memória; delegação, aprovação e acompanhamento
  precisam atravessar reinícios. A AD-34 restringiu o banco ao Brain "nesta Epic".
- **Decisão**: `Execucao` é um terceiro conceito, distinto de sessão (AD-15/16) e de turno. Execuções, etapas,
  eventos-chave, delegações, aprovações do D.A.N.T.E., reservas de worktree e metadados de sessão vivem no PostgreSQL
  do D.A.N.T.E., schema `orquestracao`, com `OrquestracaoDbContext` próprio. Processos, drivers, fila de turnos,
  deltas e approvals da CLI continuam em memória. Sem banco, o comportamento atual é preservado.
- **Relação**: substitui a AD-06 quando a persistência de jobs for obrigatória; complementa a AD-20 (registro e
  estado `Interrompida` após reinício; runtime inalterado); complementa a AD-11 (contexto congelado também na
  execução e etapa); decide explicitamente o uso do banco fora do Brain, sem editar a AD-34.
- **Consequências**: Postgres passa a ser requisito para recursos multiagente; migrations do schema novo;
  códigos `J/S/E` por sequência; o Worker ganha recuperação na inicialização.

## AD-67 — Fila durável no PostgreSQL com lease, heartbeat e idempotência, sem broker

> Registrada em `ARCHITECTURE_DECISIONS.md` pela #300. O texto vigente é o de lá.

- **Decisão**: tabela `trabalhos_da_fila` como outbox transacional; reivindicação por `FOR UPDATE SKIP LOCKED`;
  lease de 60 s com heartbeat de 20 s e *fencing* por `geracao`; `chave_de_deduplicacao` única; backoff;
  `disponivel_em` para timers; `LISTEN/NOTIFY` + polling; nenhuma transação aberta durante a chamada à CLI.
  Recuperação na inicialização para os scopes `dante-agent-*` registrados no ledger como de boot anterior, marca
  etapas interrompidas e aplica a política
  de retomada (`Automatica`, `ComConfirmacao`, `Nunca`).
- **Alternativas rejeitadas**: só memória; lease na própria execução; Hangfire/Quartz; broker externo
  ([DURABLE_QUEUE](DURABLE_QUEUE.md#alternativas-comparadas)).
- **Consequências**: WebApi e Telegram enfileiram pelo mesmo caminho; etapa de escrita nunca é refeita sem
  confirmação; complementa a AD-63 com a varredura de órfãos.

## AD-68 — Agente registrado e versionado, separado de sessão e execução

> Registrada em `ARCHITECTURE_DECISIONS.md` pela #301. O texto vigente é o de lá.

- **Decisão**: `Agente` (handle imutável sem `@`, estado) + `VersaoDoAgente` imutável (driver, modelo/esforço
  validados pela CLI, instruções, capacidades, perfil padrão e máximo, concessões, limites, hash). Execuções e
  sessões referenciam a versão. `claude` e `codex` são agentes embutidos materializados das preferências do usuário
  (AD-24/25/26). Desativar não afeta execuções ativas.
- **Relação**: complementa AD-24/25/26/27 (handle como nova entrada do resolvedor; `@` continua sendo repositório).
- **Consequências**: identidade e permissões de agente são dados; provider derivado do driver enquanto só houver CLIs.

## AD-69 — Grupos versionados com coordenação explícita: roteiro determinístico primeiro, coordenador por papel depois

- **Decisão**: `GrupoDeAgentes` + `VersaoDoGrupo` imutável (participantes por agente, com papel, responsabilidades e
  `PodeEscrever`; política; modo `Roteiro` ou `Coordenador`). A versão de cada participante é congelada na criação
  da execução. Roteiro é executado pelo orquestrador (código). O
  coordenador é um papel atribuído a um agente registrado, com plano estruturado validado pelo orquestrador e
  aprovado pelo humano conforme a política. Nada de coordenador implícito ou permanente.
- **Consequências**: o pipeline implementar → revisar → corrigir → aprovação vira um roteiro; o Brain não coordena.

## AD-70 — Delegação rastreada e comunicação mediada por `dante_coordenacao`

- **Decisão**: delegação é linha persistente ligada a uma execução subordinada; ferramentas MCP no padrão da AD-56
  (stdio + IPC exclusivo da sessão); retorno por novo turno, sem bloquear; validações determinísticas (sem
  autoconsulta, sem ciclo, profundidade, deduplicação, orçamento da raiz, interseção de permissões); resultados
  entram como dado não confiável com autor; sem mensagens livres entre agentes.
- **Consequências**: sessões de execução recebem um terceiro provedor de ferramentas; a conversa comum não.

## AD-71 — Worktree por execução de escrita, gerida pelo D.A.N.T.E. com conjunto fechado de comandos Git

- **Contexto**: a AD-58 determina que o D.A.N.T.E. não executa `git` de escrita e não bloqueia checkouts.
- **Decisão**: o D.A.N.T.E. passa a executar somente `git worktree add|remove|prune|list` (ArgumentList, sem shell,
  `core.fsmonitor=false`, prazo, nunca `--force`), em `~/.dante/worktrees/<alias>/E<n>`, com branch prefixada pelo agente; um
  escritor por worktree; execuções novas nunca escrevem no checkout principal: etapa de escrita sem worktree reservada
  é recusada (fail-closed), e antes desta AD ser implementada as execuções novas são somente leitura. Push, PR e
  merge continuam fora:
  push/PR pelo agente (contrato multirrepositório), merge sempre humano.
- **Relação**: complementa a AD-58 sem substituí-la para os fluxos existentes; altera o limite "worktrees
  automáticos" de PROJECT_CONTEXT.

## AD-72 — Gateway único para ferramentas do D.A.N.T.E. e aprovações persistidas vinculadas a hash

- **Decisão**: toda ferramenta do D.A.N.T.E. passa pelo `GatewayDeFerramentas` (identidade pela sessão, catálogo
  que nega o desconhecido, concessão por versão, schema, política, aprovação, execução única, registro).
  `PedidoDeAprovacao` com hash SHA-256 de ferramenta + parâmetros canônicos + execução, escopo `UmaVez` ou
  `EstaExecucao`, validade, decisor, `executada_em` por CAS e resultado `Incerto` sem repetição automática.
  Ferramentas nativas da CLI continuam governadas por perfil, approvals da AD-22 e contenção AD-63, só auditadas.
  Entrega em duas partes: gateway mínimo com o `dante_coordenacao`; política de aprovação com hash depois.
- **Relação**: complementa AD-22 (dois tipos de aprovação coexistem) e AD-55/56 (Brain e planilhas atrás do gateway).

## AD-73 — WebApi como interface de leitura e comando via fila, com eventos persistidos e SSE

- **Decisão**: a WebApi lê o ledger e enfileira comandos; nunca inicia processo de agente. Eventos persistidos com
  sequência global; `NOTIFY` após commit; SSE com retomada por `Last-Event-ID`. Deltas não passam pelo ledger.
  Autenticação própria da WebApi é pré-requisito.
- **Relação**: complementa AD-35; altera o limite "entrada HTTP funcional para executar agentes" de PROJECT_CONTEXT
  no sentido de que comandos HTTP passam a existir, mas a execução continua só no Worker.
