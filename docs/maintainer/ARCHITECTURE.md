# Arquitetura do D.A.N.T.E.

## 1. Qual arquitetura o projeto utiliza?

A melhor definição para a arquitetura atual é:

> **Monólito modular local, orientado a serviços e eventos, com adapters explícitos nas bordas e máquinas de estado para sessões interativas.**

Isso significa que o D.A.N.T.E. **não é um conjunto de microserviços** e também **não implementa arquitetura hexagonal estrita**.

Existe um único executável principal:

```text
Dante.Worker
```

Ele é um `Generic Host` do .NET 10. Todos os módulos principais vivem no mesmo processo e são compostos por injeção de dependência em `Program.cs`.

### Classificação por dimensão

| Dimensão | Escolha atual |
|---|---|
| Deploy | monólito: um único Worker |
| Organização interna | modular por responsabilidade |
| Integrações externas | adapters explícitos para Telegram e CLIs |
| Comunicação interna | chamadas de serviço + fluxo de eventos |
| Estado interativo | máquinas de estado em memória |
| Persistência | arquivos JSON locais; sem banco |
| Concorrência | Tasks, Channels, locks, semáforos e CancellationToken |
| Processos externos | Claude Code e Codex CLI como processos filhos |
| Interface remota | Telegram Bot API via long polling |

## 2. Por que chamar de monólito modular?

Porque há **um único processo implantável**, mas o código não está organizado como um bloco único.

Os limites são visíveis pelas pastas:

```text
Agents
Jobs
Repositories
Sessions
Settings
Telegram
```

Cada módulo possui uma responsabilidade clara e reduz o acoplamento entre detalhes.

Exemplo:

- `SessionRegistry` não precisa conhecer JSON específico do Claude ou do Codex;
- `AgentSession` não fala com Telegram;
- `TelegramDeliveryService` não inicia processos;
- `RepositoryRegistry` não decide como uma mensagem será roteada.

Portanto, o projeto tem modularidade arquitetural mesmo estando no mesmo executável.

## 3. Há elementos de Ports & Adapters?

Sim, mas o projeto **não deve ser descrito como arquitetura hexagonal completa**.

Existem abstrações que funcionam como ports:

- `ITelegramBotApi`
- `IAgentSessionDriver`
- `IAgentSessionDriverFactory`
- `IAgentSessionEventSink`
- `IAgentProcessExecutor`
- `IInteractiveAgentProcessLauncher`
- `IClaudeRunner`
- `ICodexRunner`
- `IAgentModelCatalog`

E implementações que funcionam como adapters:

- `TelegramBotApi`
- `ClaudeSessionDriver`
- `CodexSessionDriver`
- `AgentProcessExecutor`
- `InteractiveAgentProcessLauncher`

Essa separação traz benefícios típicos de Ports & Adapters:

- os testes conseguem trocar integrações por fakes;
- Telegram não conhece o protocolo das CLIs;
- sessões não conhecem HTTP;
- os detalhes de Claude e Codex ficam encapsulados em drivers.

Porém o projeto não possui uma divisão formal em `Domain/Application/Adapters` nem aplica todas as regras de dependência de uma arquitetura hexagonal clássica.

A descrição correta é:

> **monólito modular com fronteiras inspiradas em Ports & Adapters.**

## 4. Visão de alto nível

```text
                        ┌───────────────────────────┐
                        │         Telegram          │
                        └─────────────┬─────────────┘
                                      │ Bot API
                                      ▼
                        ┌───────────────────────────┐
                        │ TelegramPollingService    │
                        │ - long polling            │
                        │ - comandos                │
                        │ - roteamento inicial      │
                        └──────┬──────────┬─────────┘
                               │          │
                  mensagem     │          │ /claude /codex
                   comum       │          │
                               ▼          ▼
                    ┌────────────────┐  ┌───────────────┐
                    │ SessionRegistry│  │  JobRegistry  │
                    └───────┬────────┘  └──────┬────────┘
                            │                  │
                            ▼                  ▼
                    ┌───────────────┐   ┌───────────────┐
                    │ AgentSession  │   │ ClaudeRunner   │
                    │ state machine │   │ CodexRunner    │
                    └──────┬────────┘   └──────┬────────┘
                           │                   │
                    IAgentSessionDriver        │
                     ┌─────┴─────┐             ▼
                     ▼           ▼       AgentProcessExecutor
                 Claude       Codex              │
                 driver       driver             │
                     │           │               │
                     └─────┬─────┘               │
                           ▼                     ▼
                  InteractiveAgentProcess   processo one-shot
                           │
                       stdin/stdout
                           │
                 Claude Code / Codex CLI
                           │
                           ▼
                      AgentEvent
                           │
                           ▼
                 TelegramDeliveryService
                           │
                           ▼
                        Telegram
```

## 5. Os dois caminhos de execução

O sistema mantém deliberadamente **dois modelos de execução**.

### 5.1 Sessão interativa

É o caminho padrão para mensagens comuns.

```text
mensagem
  ↓
SessionRegistry
  ↓
AgentSession
  ↓
IAgentSessionDriver
  ↓
processo Claude/Codex de longa duração
  ↓
AgentEvent
  ↓
TelegramDeliveryService
```

Características:

- um processo por sessão;
- vários turnos no mesmo processo;
- streaming;
- queue;
- steer;
- approvals;
- input humano;
- interrupção de turno;
- contexto e modelo fixados no início da sessão.

### 5.2 Job one-shot

É usado por comandos explícitos:

```text
/claude ...
/codex ...
```

Fluxo:

```text
comando
  ↓
JobRegistry
  ↓
ClaudeRunner / CodexRunner
  ↓
AgentProcessExecutor
  ↓
processo curto
  ↓
resultado final
  ↓
TelegramDeliveryService
```

Características:

- um processo por execução;
- não há continuidade de conversa;
- resultado é acumulado até o processo terminar;
- há Job ID;
- pode ser cancelado via `/cancel`.

Essa coexistência é intencional. Sessões não substituem jobs; elas resolvem um problema diferente.

## 6. Núcleo de sessão como máquina de estados

`AgentSession` é uma máquina de estados neutra.

Ela não:

- abre processos;
- acessa Telegram;
- conhece JSON-RPC do Codex;
- conhece stream-json do Claude.

Ela mantém o estado lógico da sessão.

Estados relevantes:

```text
Starting
   ↓
Idle
   ↓
Running
   ↕
WaitingForUser
   ↓
Idle

Closing
   ↓
Closed

qualquer falha terminal
   ↓
Failed
```

A ideia central é separar:

```text
estado lógico da conversa
        ≠
estado do processo
        ≠
estado da entrega Telegram
```

Por isso existem:

- `AgentSessionState`
- `InteractiveAgentProcessState`
- `TelegramDeliveryState`

Cada um responde a uma pergunta diferente.

## 7. Arquitetura orientada a eventos dentro das sessões

Os drivers convertem protocolos específicos em um contrato neutro:

```text
Claude stream-json
        │
        ▼
ClaudeSessionDriver
        │
        ├─ MessageDeltaEvent
        ├─ ToolStartedEvent
        ├─ FileChangeEvent
        ├─ ApprovalRequestedEvent
        ├─ UserInputRequestedEvent
        └─ TurnCompletedEvent
```

e:

```text
Codex app-server
        │
        ▼
CodexSessionDriver
        │
        └─ mesmos AgentEvent
```

O restante do sistema trabalha com `AgentEvent`, não com o protocolo original.

Esse é um ponto arquitetural central.

## 8. Contexto é resolvido antes da execução

O D.A.N.T.E. não permite que uma sessão ou job “descubra sozinho” em qual repositório atuar.

O contexto é resolvido antes:

```text
General Mode
ou
Repository Mode (@alias)
```

A execução recebe um `JobExecutionContext` pronto, produzido pelo `AgentContextResolver` (AD-27) com a
precedência `@alias` explícito → repositório ativo → General Mode, e `/claude`/`/codex` → agente padrão.

Isso reduz risco de:

- atuar no projeto errado;
- trocar de diretório durante a execução;
- inferir contexto com base no prompt.

Em sessões, o contexto é **imutável durante toda a vida da sessão**.

## 9. Persistência híbrida

O sistema não usa banco de dados.

Existem dois tipos de estado.

### Persistente

Guardado em arquivos JSON:

- preferências de usuário;
- aliases de repositórios;
- ambiente configurado por repositório.

### Volátil

Guardado apenas em memória:

- jobs;
- sessões;
- turnos;
- requests pendentes;
- estado de entrega;
- retries recentes.

Consequência:

> reiniciar o Worker encerra a realidade operacional atual e cria uma nova.

Isso é uma decisão arquitetural atual, não um bug.

## 10. Concorrência

A arquitetura usa primitivas explícitas em vez de um framework de atores.

Principais mecanismos:

### `lock`

Protege estruturas em memória como:

- catálogos;
- registros;
- estado de sessão.

### `SemaphoreSlim`

Usado quando uma operação assíncrona precisa ser serializada.

Exemplos:

- escrita no stdin de um agente;
- operações upstream de uma sessão;
- envio/edição de mensagens relacionadas.

### `Channel<T>`

Usado para streaming com backpressure.

Exemplo:

`InteractiveAgentProcess` mantém um channel limitado para stdout/stderr.

Se o consumidor ficar lento, o produtor também desacelera em vez de consumir memória indefinidamente.

### `CancellationToken`

É o mecanismo padrão para:

- shutdown;
- cancelamento de job;
- interrupção de waits;
- timeout.

## 11. Segurança como decisão arquitetural

Segurança não está concentrada em uma única classe. Ela aparece em várias fronteiras.

### Entrada

`TelegramUserAuthorizer` aplica allowlist fail-closed.

### Processo

`AgentProcessStartInfo`:

- não usa shell;
- fixa o executável;
- usa `ArgumentList`;
- trata prompt como dado.

### Diretório

Working directory precisa ser absoluto e existente.

### Repositório

`RepositoryRegistry` valida aliases e raiz Git.

### Ambiente

Em General Mode o ambiente herdado é filtrado.

Bindings sensíveis são resolvidos a partir do host.

### Saída

`TelegramDeliveryService` redige segredos antes de enviar conteúdo.

### Approval

Request é correlacionado a:

- usuário;
- sessão;
- turno;
- request;
- mensagem Telegram.

## 12. As bordas do sistema

As bordas principais são:

```text
                  ┌──────────────┐
                  │   Telegram   │
                  └──────┬───────┘
                         │
                         ▼
                 ITelegramBotApi
                         │
             ┌───────────┴───────────┐
             │     núcleo DANTE      │
             └───────────┬───────────┘
                         │
             IAgentSessionDriver /
             IAgentProcessExecutor
                         │
                         ▼
                 Claude / Codex CLI
```

Outras bordas:

- filesystem;
- Git;
- arquivos `~/.dante/*.json`;
- variáveis de ambiente.

## 13. O que NÃO existe hoje

Para não criar um modelo mental errado:

- não há banco relacional;
- não há API HTTP própria;
- não há frontend web;
- não há message broker;
- não há microserviços;
- não há Kubernetes;
- não há Docker como requisito arquitetural;
- não há scheduler persistente;
- não há fila durável;
- não há event sourcing;
- não há CQRS formal;
- não há arquitetura hexagonal formal.

## 14. Decisões arquiteturais formais

O projeto mantém decisões em:

`docs/context/ARCHITECTURE_DECISIONS.md`

As decisões mais importantes para compreender a arquitetura atual são:

- AD-01 — Telegram via long polling;
- AD-02/03 — processos locais e sem shell arbitrário;
- AD-07–11 — repositórios, contexto e ambiente;
- AD-13/14 — settings persistidas e repo ativo;
- AD-15–20 — sessões, processos interativos, drivers e registry;
- AD-21 — entrega Telegram independente da execução;
- AD-22–24 — approval, session-first e modos;
- AD-25/26 — modelo e esforço.

Essas decisões são a justificativa histórica. Este documento é o mapa consolidado.

## 15. Regra prática para futuras mudanças

Antes de adicionar uma funcionalidade, identifique em qual responsabilidade ela pertence.

Exemplos:

| Necessidade | Lugar provável |
|---|---|
| novo comando Telegram | `TelegramPollingService` |
| nova forma de enviar mensagem | `TelegramDeliveryService` |
| novo detalhe da Bot API | `TelegramBotApi` |
| comportamento comum de sessão | `AgentSession` / `SessionRegistry` |
| comportamento específico do Claude | `ClaudeSessionDriver` |
| comportamento específico do Codex | `CodexSessionDriver` |
| processo one-shot | `Agents/*Runner` / `AgentProcessExecutor` |
| repo/alias/env | `RepositoryRegistry` |
| preferência persistente | `AssistantSettingsStore` |
| seleção de modelo | `AgentModelCatalog` |

Se uma alteração começar a atravessar muitos desses limites ao mesmo tempo, é um sinal para revisar a responsabilidade antes de continuar.

## 16. Arquitetura alvo do D.A.N.T.E. Brain (#133, #134)

Esta seção define **contratos conceituais para implementação futura** (AD-33).
O Brain ainda não está implementado: as seções anteriores descrevem o runtime
atual. A persistência física e os índices são responsabilidade da #135; esta
arquitetura não escolhe backend, provider, biblioteca ou formato de banco.

O Brain entra como módulo do monólito local, composto em `Program.cs`. Não muda
as máquinas de estado dos agentes nem torna jobs/sessões duráveis. Telegram é
uma borda de apresentação; Claude/Codex consomem contexto e sugerem candidatos.
Nenhum desses adapters é dono do conhecimento canônico.

### 16.1 Três informações com ciclos de vida diferentes

| Informação | Finalidade | Autoridade e ciclo de vida |
| --- | --- | --- |
| Conversation History | Mensagens e eventos de uma conversa | Evidência de origem quando selecionada; não equivale a conhecimento. A Epic não promete persistir transcript completo. |
| Working Memory | Estado operacional necessário para retomar uma tarefa | Working Context Snapshot curto, substituível, com origem, timestamp, revisão e possível expiração (#156). Não recupera processo, requests ou fila. |
| Knowledge | Conteúdo consolidado reutilizável | Knowledge Items com escopo, status, validade, sensibilidade e proveniência; independentes da sessão/CLI. |

Fechar sessão, reiniciar Worker, trocar Claude por Codex ou limpar/compactar o
contexto upstream não deve apagar conhecimento ou snapshot automaticamente.
Uma sessão nova poderá retomar **a tarefa**, não a sessão/processo anterior.
Não persistir raciocínio privado/chain-of-thought como snapshot ou conhecimento.

### 16.2 Identidade e escopo

O contexto de acesso é composto por `TenantId`, `UserId`, `KnowledgeSpaceId` e
`ProjectId` opcional. IDs são estáveis e opacos, gerados pelo D.A.N.T.E.; não são
paths, usernames, alias de repositório, chat IDs ou IDs de sessão da CLI.

O MVP pode usar um tenant local, mas deve mapear cada usuário autorizado do
Telegram a uma identidade persistente. A allowlist de Telegram permite entrar;
não substitui autorização para ler/injetar/exportar conhecimento (#150).

- **KnowledgeSpace** é o limite de organização e isolamento: Personal, Work,
  estudos ou outro contexto escolhido pelo usuário (#152). Possui owner, nome,
  descrição e estado de arquivamento. Conhecimento pode pertencer diretamente ao
  Space, sem Project.
- **Project** pertence a exatamente um Space e delimita um trabalho persistente
  (#136). Um repositório pode ser referência do Project; Project não é alias Git
  nem muda permissões de execução ou o diretório da sessão.
- Seleção ativa de Space/Project é preferência do usuário; a identidade e o
  escopo efetivo devem ser resolvidos/validados pelo núcleo para cada operação.
  A UX pode usar nomes amigáveis, sem exigir IDs internos em cada mensagem.
- Nenhum escopo é inferido da saída do agente. Escopo ausente/ambíguo impede
  acesso ao Brain até resolução explícita, sem busca global de fallback.
- Consulta por Project pode combinar itens desse Project e itens do mesmo Space
  sem Project, quando permitido pela intenção/policy. Não inclui outros Projects
  automaticamente. Busca entre Spaces exige seleção explícita e autorização de
  cada Space, nunca um wildcard implícito.
- Relações, fontes, snapshots, índices, caches e exports carregam o mesmo escopo.
  Filtrar só depois do ranking ou da injeção é insuficiente.

### 16.3 Conceitos e contratos de dados

| Conceito | Contrato semântico mínimo |
| --- | --- |
| KnowledgeItem | ID e revisão; tenant/user/space e Project opcional; tipo, conteúdo, dados estruturados opcionais, tags, status, sensibilidade, autoria/timestamps, proveniência e intervalo de validade opcional (#138). |
| KnowledgeRelation | Source/target item, tipo direcional quando aplicável, escopo, proveniência e timestamps; endpoints existentes e autorizados, sem relações órfãs (#153). |
| SourceDocument / Source | Identidade lógica e revisão de uma evidência: nota, documento ou trecho selecionado de conversa. Origem, hash quando aplicável, referência e localização do trecho. Fonte bruta não é fato confirmado (#158). |
| Provenance | Quem/qual processo registrou, fonte e revisão/trecho usado, quando, e quem confirmou/corrigiu. Deve sobreviver à consolidação e à substituição enquanto o conteúdo não for excluído por solicitação do usuário. |
| MemoryCandidate / KnowledgeCandidate | Dois nomes para o mesmo estágio conceitual de proposta: conteúdo/tipo sugerido, origem/evidência, justificativa, escopo e classificação; decisão pendente, confirmada, rejeitada ou corrigida, com ator/timestamp (#139). Não é automaticamente KnowledgeItem. |
| Working Context Snapshot | Working Memory persistida: objetivo, progresso, referências, pendências, próximos passos explícitos e último resultado útil; revisão ativa limitada por Space/Project, timestamp, origem e possível expiração (#156). |
| Context Pack | Representação derivada e limitada preparada para um pedido: itens/revisões/origens/status, trechos selecionados, snapshot elegível, motivo de seleção, tokens estimados, descartes e escopo efetivo (#140). Não é o repositório canônico. |
| Sensitivity | Public, Personal, Work, Confidential ou Secret (#155); dirige leitura, captura, injeção e exportação por policy, não por decisão do modelo. |

Tipos iniciais de KnowledgeItem seguem a #138: Fact, Decision, Preference,
Instruction, Note, Reference, Incident, Solution, Lesson, Procedure, Summary e
Inference. Um Summary produzido para índice/recuperação é derivado; um item
Summary explicitamente consolidado tem sua própria proveniência/status e não
substitui as fontes que resume. Não promover um resumo automático a conhecimento.

Status e validade são eixos distintos: `confirmed`, `inferred`, `temporary`,
`superseded` e `inactive`; `validFrom`/`validUntil` delimitam o intervalo quando
aplicável. Confidence pode acompanhar inferência, mas não equivale a confirmação.
A ausência de `validUntil` não comprova verdade permanente. A policy de recuperação
exclui expirados, ainda não válidos, superseded e inactive; inferidos/temporários,
se pertinentes, devem aparecer com sua classificação e nunca como fatos confirmados.

Relações iniciais: RELATES_TO, BELONGS_TO, SUPERSEDES, DERIVED_FROM, RESOLVED_BY,
PRODUCED_LESSON, DEPENDS_ON, REFERENCES e CONTRADICTS. O grafo expressa semântica,
sem impor banco de grafos. Expansão tem limites de profundidade/quantidade/custo.
Um conflito é representado e explicado; o agente não escolhe silenciosamente uma
versão como verdadeira. Exemplo: Incident → RESOLVED_BY → Solution → PRODUCED_LESSON → Lesson.

### 16.4 Responsabilidades do módulo

Os nomes abaixo designam responsabilidades, não classes/interfaces já existentes.

| Componente | Entrada/saída e limite |
| --- | --- |
| Brain Access Policy | Resolve identidade/escopo e autoriza leitura, escrita, injeção/exportação. Reutilizada em todos os caminhos; fail-closed (#150, #155). |
| Capture / Consolidation | Fonte/evento selecionado → candidato → confirmação/rejeição/correção auditável → item/relações. Detecta duplicatas/conflitos sem consolidar automaticamente todo o chat (#139, #159). |
| Knowledge Core | Valida os conceitos, status, validade, relações, revisões e operações de correção/exclusão. Não conhece Telegram, protocolos de CLI ou tipos físicos de banco (#138). |
| Persistence ports | Operações de leitura/gravação/commit com escopo obrigatório, revisão esperada e resultados de não encontrado, conflito ou indisponibilidade. Sem APIs específicas de backend; provider definido na #135/#160. |
| Source handling | Preserva identidade/proveniência de fontes selecionadas e processa ingestão sem transformar documento inteiro em fato confirmado (#158). |
| Brain Search | Busca lexical/semântica com filtros obrigatórios, top-k limitado e retorno de IDs/revisões/scores/origens. Índices são derivados; lexical continua quando semântica faltar (#154). |
| Context Builder | Centro da recuperação: aplica policy, busca, expande relações, ordena, deduplica e monta Context Pack dentro do orçamento (#140). |
| Working Memory | Atualiza/consulta snapshot operacional limitado; valida revisão, origem, validade e escopo (#156). |
| Conversation adapter | Traduz intenção do usuário para casos de uso do Brain e apresenta confirmação/origem; injeta somente Context Pack autorizado antes do envio ao driver (#145, #157). |
| Inspection / Export | Consulta, correção, exclusão e exportação sob a mesma policy. Não depende de acesso direto ao armazenamento pelo usuário (#149). |
| Measurement | Registra IDs, contagens, tamanhos estimados e decisões de seleção sem logar conteúdo sensível; compara continuidade/qualidade/custo com baseline (#148, #147). |

Persistência entrega dados/revisões ao domínio; índices entregam candidatos ao
Context Builder. Nem o índice nem o driver pode promover conhecimento, ampliar
escopo ou conceder permissão. Composição/infraestrutura dependem dos contratos do
núcleo, mantendo provider, protocolo e canal fora dos conceitos centrais.

### 16.5 Captura e consolidação

Fonte selecionada → autorização/classificação → candidato com evidência →
deduplicação/conflitos → política de confirmação → KnowledgeItem e relações →
commit canônico → atualização reconstruível dos índices.

Captura explícita ("guarde esta solução") e sugestão automática passam pela mesma
policy. Confirmar/rejeitar/corrigir registra ator e evidência; afirmação do usuário
e conclusão do agente continuam distinguíveis. Repetição ou confiança alta do
agente não são confirmação. Falha de persistência não pode produzir confirmação
falsa ao usuário; falha de indexação não desfaz nem perde o item confirmado.

Correção usa revisão esperada para evitar sobrescrita silenciosa. Substituição
mantém ligação/proveniência da versão anterior como superseded; não é exclusão.
Exclusão solicitada precisa remover conteúdo elegível, derivados e referências
que o reintroduziriam; auditoria mínima não preserva o texto excluído. Backups e
retenção precisam explicitar limites de remoção (estratégia física na #135).

### 16.6 Recuperação seletiva e Context Builder

Pedido + identidade/escopo resolvidos → filtros de policy/status/validade → Brain
Search → expansão autorizada e limitada por relações → rerank → deduplicação com
snapshot/prompt/itens → orçamento → Context Pack → revalidação → adapter do agente.

O builder prioriza instruções/decisões confirmadas e relevantes, mas conteúdo do
Brain permanece **dado de contexto**, não instrução de sistema privilegiada.
Documentos importados e saídas de agentes são fontes não confiáveis: não podem
alterar policy, escopo, permissões de execução ou comandos de controle da CLI.

O orçamento abrange snapshot, itens, proveniência e formatação efetivamente
enviados. Deve reservar espaço para pedido atual e resposta; configurações e
estimativas são explícitas, não percentuais inventados. Limites de itens, tokens
e expansão são testáveis. Tokens estimados não são consumo cobrado pela CLI.

Antes da injeção, verificar que revisões continuam elegíveis e autorizadas. Cache
ou embedding de versão excluída/classificada novamente não concede acesso ao
conteúdo. Secret não é injetado automaticamente; credenciais preferem referências
seguras fora do conhecimento comum. Exportação respeita classificação/redaction.

Pack vazio é um resultado válido. Falha de busca/índice/policy não autoriza
reenviar o transcript completo nem buscar outro Space. O adapter deve distinguir
"sem conhecimento relevante" de "recuperação indisponível". Pode informar e seguir
com o pedido sem Brain, quando permitido, sem afirmar continuidade não comprovada.

### 16.7 Continuidade, UX e validação

Na sessão nova, o adapter monta snapshot + Context Pack do escopo autorizado e
mantém configurações/diretório pelos mecanismos atuais. Alterar Space/Project não
move a sessão existente silenciosamente. A integração deverá definir o escopo
fixado na abertura e recusar ambiguidades, preservando AD-14/23/27 (#145).

Consultar, guardar, corrigir e excluir deve funcionar por conversa natural (#157),
com confirmação quando necessária e possibilidade de explicar "de onde veio".
Acesso por nomes amigáveis não dispensa resolver identidades internamente.

Medir conhecimento armazenado versus contexto recuperado/selecionado/injetado e
baseline de histórico. A #147 compara retomada entre sessões/agentes, repetição
exigida do usuário, relevância e tarefa concluída. Redução de tokens é hipótese:
pack pequeno com resposta errada não é sucesso. Registrar versões do builder e
estimador para comparar execuções. Semântica indisponível deve ser identificável.

### 16.8 Limites desta Epic e desta decisão

Fora da Epic: ComfyUI, framework/marketplace de integrações, jobs duráveis, Artifact
Catalog, workers remotos, autenticação hosted, SaaS comercial, frontend web completo,
microserviços e Kubernetes. Não transformar o Brain em sistema de execução.

A #134 entrega arquitetura/documentação, não persistência, Knowledge Core, Search,
UI ou injeção em produção. #134 e #135 são frentes independentes; a #160 só inicia
após conclusão de ambas. As Issues filhas da #133 implementam e validam os contratos
em sequência, conforme suas dependências. O Brain não passa a ser requisito para
executar o Worker atual só porque a arquitetura alvo foi documentada.
