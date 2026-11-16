# Investigação: agentes.chat

Engenharia reversa arquitetural do [agentes.chat](https://github.com/agentes-chat/agentes-chat) para
orientar a Epic #296 (plataforma multiagente) e o spike #297. Documento de pesquisa: **não** é decisão
arquitetural do D.A.N.T.E. e não altera AD, backlog ou contratos. As propostas daqui só valem depois de
revisão humana e de virarem AD/Issue pelo fluxo normal.

> **Referência, não proposta.** Esta pesquisa é o insumo do spike #297. A proposta arquitetural vigente está em
> [`docs/architecture/multi-agent-platform/`](../../architecture/multi-agent-platform/README.md); onde as duas
> divergem (nomes, ordem das etapas, estados), vale a proposta. As recomendações **[R]** daqui foram avaliadas lá.
> Evidências brutas do laboratório (prompt capturado, OpenAPI, logs) ficam fora do repositório.

> **Licença.** O agentes.chat é AGPL-3.0-only com termo de atribuição (7b) e marcas reservadas. Nada
> deste diretório é código do concorrente, e nenhum trecho de código dele pode entrar no D.A.N.T.E.
> Estudamos conceitos e comportamento; a implementação é nossa, em C#/.NET. Ver [LICENCAS](LICENCAS.md).

## Versão analisada

| O quê | Valor |
| --- | --- |
| Código lido | `main` @ `d6209e6ff1f0bfa24896b66d1195f0c52c50ce4f` (2026-11-16 10:50 -03, PR #684) |
| Aplicação executada | imagem `ghcr.io/agentes-chat/agentes@sha256:d6bde5d5b68510a7d8a309b0491ec1431ffeeb815f019c2b9bdaf269e004ad4f` = **v0.1.2**, commit `1abdbe79f5c3` (2026-11-15) |
| Distância | o código lido está 265 commits à frente da imagem; comportamento observado vale para a v0.1.2 |
| ADRs | 186 arquivos em `docs/adr/` (0001–0188) |
| Tamanho | ~1,4 milhão de linhas TS/TSX; `packages/engine/src` com 326 módulos; 171 tabelas no Postgres após as migrations |

## Como ler as afirmações

Cada afirmação relevante leva uma marca de origem:

| Marca | Significado |
| --- | --- |
| **[C]** | fato comprovado no código (arquivo citado) |
| **[O]** | comportamento observado na aplicação rodando no laboratório |
| **[D]** | documentação oficial (README, SELF_HOSTING, ADR) — intenção declarada, nem sempre conferida no código |
| **[I]** | inferência arquitetural nossa |
| **[R]** | recomendação para o D.A.N.T.E. |

Caminhos do concorrente são relativos à raiz do repositório dele (`packages/engine/src/router.ts`). Caminhos
do D.A.N.T.E. são relativos a este repositório (`src/Dante.Worker/Sessions/AgentSession.cs`).

## Documentos

| Documento | Conteúdo |
| --- | --- |
| [ARQUITETURA](ARQUITETURA.md) | visão geral, stack, processos, persistência, diagramas |
| [MODULOS](MODULOS.md) | módulos e responsabilidades reais, priorizados para a Epic #296 |
| [FLUXOS](FLUXOS.md) | chat, roteamento, delegação, execução, aprovação, ponta a ponta |
| [AGENTES](AGENTES.md) | cadastro, identidade, modelo, orçamento, ciclo de vida |
| [ORQUESTRACAO](ORQUESTRACAO.md) | grupos, coordenador, comunicação, limites de autonomia |
| [SEGURANCA](SEGURANCA.md) | escopos, aprovações, isolamento, credenciais, auditoria |
| [INTEGRACOES](INTEGRACOES.md) | MCP (cliente e servidor), skills, conectores, computadores |
| [BRAIN-E-MEMORIA](BRAIN-E-MEMORIA.md) | memória, busca, wiki, comparação com a Braúna |
| [LICENCAS](LICENCAS.md) | licenças, marcas, o que pode e o que não pode ser reutilizado |
| [COMPARACAO-DANTE](COMPARACAO-DANTE.md) | matriz A–F funcionalidade a funcionalidade |
| [ROADMAP](ROADMAP.md) | proposta incremental para a Epic #296 |

## Laboratório

Instalação self-hosted isolada, feita com o Compose oficial **por inspeção**, sem `curl | sh` (o
`install.sh` foi lido: instala Docker/Bun e chama `@agenteschat/setup`, o que não era necessário aqui).

| Item | Valor |
| --- | --- |
| Diretório | `~/labs/agentes-chat` (`source/`, `lab/`, `data/`, `notes/`, `evidence/`, `reports/`); `/srv/labs` exigiria `sudo` |
| Acesso | `http://localhost:3310`, só loopback (de outra máquina: `ssh -L 3310:localhost:3310`) |
| Projeto Compose | `agenteslab`: containers `agenteslab-{agentes,postgres,stubllm}-1`, volumes `agenteslab_{pgdata,files}`, redes `agenteslab_{default,speech,embeddings}` |
| Isolamento | Postgres 18 próprio (não o canônico do D.A.N.T.E.), nenhuma porta além de `127.0.0.1:3310`, nenhuma alteração em Docker global, Tailscale, firewall, `dante.service` ou Worker |
| Privacidade | telemetria, relay de push e relay de feedback desligados no `.env` |
| Sem credenciais reais | um stub nosso, OpenAI-compatível e determinístico (`lab/scripts/stub_llm.py`), cadastrado como provider `custom` permitiu exercitar execução, delegação, perguntas e aprovações; ele grava cada requisição e por isso revelou o prompt real montado pelo concorrente |

Iniciar, parar, atualizar, fazer backup e remover: `~/labs/agentes-chat/notes/LAB.md`.

## O que foi testado na prática [O]

- conta dona, workspace, agente embutido (Lodi, "braço direito") criado automaticamente;
- cadastro de 3 agentes (dev, revisor, infra) com modelo, credencial e orçamento; grupo "Engenharia"; canal `#engenharia`;
- menção explícita → run → resposta no canal; recibo de roteamento por regra (`decidedBy: rule follow_up`);
- mensagem sem menção decidida pela cadeia de roteamento em modo "regras" (sem modelo pequeno);
- duas menções na mesma mensagem → duas runs; o `ask_agent` recusou autoconsulta e pedido duplicado;
- `ask_agent` real: DM privada entre agentes, run do consultado, aviso `consult_reply` de volta e retomada do solicitante;
- `ask_user` com opções → run em `waiting_input`, cartão, resposta pela API, retomada;
- `request_approval` → cartão com `why`, aprovar → run retomada; trilha em `approvals`, `agent_run_steps`, `audit_log`;
- tarefa (issue) atribuída a agente → canal `task` próprio, run vinculada, status `in_progress`;
- rotina agendada disparada manualmente → run em segundo plano em sala própria, registrada como `silent`;
- skill em Markdown cadastrada com versão; catálogo de 79 conectores MCP; Task Manager com runs ativas e encerradas.

Não testado na prática (só código/ADR): computador do agente (runner), navegador/desktop do agente, Claude Code e
Codex como "cérebro" (exigem o runner, instalado como root no host), conectores MCP reais (exigem OAuth/chaves),
roteador Jev/LLM (exige chave), memória por reflexão (exige modelo de resumo), wiki, Drive, apps móvel/desktop.

## Conclusões em uma página

1. **O núcleo é um motor de execuções persistente no Postgres, não o chat.** Runs são uma máquina de estados
   com compare-and-set (`agent_runs`), passos numerados (`agent_run_steps`), transcript append-only retomável e
   uma fila transacional (`outbox` com `FOR UPDATE SKIP LOCKED`). Tudo o mais — roteador, aprovações,
   delegação, rotinas, tarefas — cria ou retoma runs. **[C]** `packages/engine/src/{runs,outbox,executor}.ts`.
   Para o D.A.N.T.E., cujos jobs e sessões vivem só em memória (AD-06, AD-20), essa é a lacuna estrutural nº 1.
2. **Um motor, cérebros trocáveis.** API, Claude Code, Codex, Cursor etc. emitem os mesmos eventos neutros; o
   motor concentra contexto, ferramentas, aprovações, uso e eventos. **[C]** `packages/engine/src/brains/types.ts`.
   O D.A.N.T.E. já tem o equivalente para as CLIs (`IAgentSessionDriver`, AD-15/16), e mais maduro em
   contenção de processo (AD-63).
3. **Delegação é uma linha rastreada, não uma mensagem.** `ask_agent`/`tell_agent` gravam
   `agent_consultations`, rodam o consultado numa DM privada, voltam sozinhos uma vez, consolidam sub-entregas e
   têm anti-ciclo, anti-duplicidade e profundidade máxima. **[C][O]** `consultations.ts`, ADR 0034/0095.
4. **Quem responde é decidido por código, com o modelo só pontuando.** Candidatos vêm do banco; uma estratégia
   pontua; a política (limiar, máximo de agentes, limites de salto) decide; cada decisão é gravada.
   **[C]** `router.ts`, ADR 0013/0074. O "coordenador" foi tentado e substituído (ADR 0093 → 0137).
5. **Segurança é um gateway único.** Toda chamada de ferramenta passa por busca por nome, escopo lido do banco a
   cada chamada, validação de schema e aprovação vinculada a hash dos parâmetros, executada uma única vez.
   **[C]** `tools/gateway.ts`, ADR 0015.
6. **O custo de produto deles é alto.** Muitas ADRs corrigem efeitos de autonomia (agentes que não param,
   loops entre agentes, ruído de alertas, perguntas que viram ação): limites por "trecho", controlador de fim de
   turno, freios de autonomia. **[D]** ADR 0089/0097/0131. Para nós: incorporar os limites desde o início, em
   regras determinísticas, e adiar juízes por LLM.
7. **Licença impede reuso de código; ideias são livres.** Tudo relevante é AGPL-3.0-only. O único pacote MIT
   (`packages/setup`) não interessa. [LICENCAS](LICENCAS.md).

Recomendação de próxima atividade: ver [ROADMAP](ROADMAP.md#próxima-atividade-sugerida-para-a-epic-296).
