# Estado atual

Retrato **substituível** do D.A.N.T.E. em `main`. Descreve o HEAD, não uma sessão: quem
muda o estado do projeto reescreve a parte afetada em vez de acrescentar entradas. O
histórico consolidado fica em [DEVELOPMENT_HISTORY](DEVELOPMENT_HISTORY.md).

Estado de Issues em andamento (worker, branch, handoff) **não** vive aqui: vive nas
próprias Issues e PRs do GitHub. Para o estado vivo do backlog, consulte o GitHub.

Última revisão: 2026-10-02, com a entrega de arquivos produzidos pelos agentes (#97).

## Marcos

| Marco | Situação |
| --- | --- |
| MVP 1 — Telegram → D.A.N.T.E. → Claude/Codex → Telegram (Epic #1) | concluído |
| MVP 2 — Context-aware orchestration (Epic #18) | concluído |
| Agent Harness v1 (Epic #40) | concluído |
| MVP 3 — Conversational Context (Epic #32) | concluído (#33–#38); fechamento da Epic por decisão humana |
| Interactive Agent Sessions (Epic #60) | concluído |
| Mídias no Telegram (Epic #92) | em andamento: spike #93, recebimento #94, imagens aos agentes #95 e artefatos #97 entregues; #96, #98 e #99 pendentes |

## Funcionalidades disponíveis

- bot Telegram por long polling com allowlist de usuários;
- `/ping`;
- `/claude [@alias] <prompt>` e `/codex [@alias] <prompt>`;
- General Mode em workspace isolado e Repository Mode por `@alias`;
- `/repos` e `/repo add|show|remove`;
- `/repo env set|bind|list|remove` com segredos por referência ao host;
- `/status` com contexto de cada job e `/cancel <jobId>`;
- configurações do assistente em `~/.dante/settings.json` (agente padrão, Claude quando
  não configurado; repositório ativo, modo padrão, modelos e esforço por usuário);
- `/agent` e `/agent set claude|codex` para consultar e alterar o agente padrão; com sessão ativa, `/agent` e
  `/use` mostram também a sessão (agente e contexto) que recebe as mensagens comuns (#38);
- `/model` e `/model claude|codex [<modelo>|default]`: preferência persistida por usuário
  e agente, validada no catálogo das CLIs (cache de dez minutos), usada nas novas sessões
  e nos one-shot explícitos; `/session start ... model=<modelo>` permite override local
  e `model=default` retorna ao padrão da CLI naquela sessão. Modelo fica fixo e visível
  em `/status`; preferência indisponível recusa novas execuções e orienta nova escolha
  ou retorno ao default, sem troca silenciosa (AD-25);
- `/effort` e `/effort claude|codex [<nível>|default]`: preferência por usuário/agente
  validada nos níveis anunciados pela CLI para o modelo escolhido, aplicada às novas
  sessões e one-shot; override `effort=<nível>|default` em `/session start`, esforço
  fixo entre turnos e visível em `/status`, independente de permissões (AD-26);
- conversa session-first (AD-23): mensagem sem slash command abre uma sessão interativa do
  agente padrão no contexto atual, ou continua a sessão ativa, e mostra essencialmente a
  resposta do agente, com progresso curto e indicador "digitando…"; `/claude` e `/codex`
  seguem como execução one-shot com Job ID; slash command desconhecido responde erro e não
  inicia agente;
- recebimento de imagens (#94, AD-29): fotos, prints como arquivo e álbuns de usuários autorizados são
  baixados com limite de tamanho e tempo, validados pelo conteúdo (JPEG/PNG/GIF/WebP, 7 MB, 8000 px) e guardados
  como pendentes em `~/.dante/attachments`, por usuário e contexto, com uma confirmação por álbum, linha em
  `/status`, descarte na troca de contexto, expiração em 10 min e limpeza de sobras na inicialização; áudio,
  vídeo e outros arquivos são recusados sem download;
- imagens aos agentes (#95, AD-29): a legenda é o pedido (turno da conversa, fila durante turno ativo, ou one-shot
  com `/claude`/`/codex`), e sem legenda o próximo texto (mensagem, `/steer`, `/claude`, `/codex`) leva todos os
  pendentes, na ordem; Claude recebe blocos `image` na sessão e `Read` + `--add-dir` no one-shot, Codex recebe
  `localImage` no turno e no steer e `-i` no one-shot; anexos sem suporte são recusados antes do agente; os arquivos
  ficam no diretório da sessão ou do job e são apagados quando ela ou ele termina;
- arquivos produzidos (#97, AD-29): imagem gerada pela sessão do Codex (`imageGeneration.savedPath`) é enviada sozinha,
  e `/send <caminho>` envia um arquivo do diretório da sessão ativa; caminho real validado contra a raiz do canal
  (symlinks resolvidos), até 50 MB, sem nomes de credencial, e nunca de sessão com segredos vinculados; imagem vai
  como foto e documento original, o resto como documento; cópia privada para retry, `/resend F000001` e `/status`;
- `/use @alias`, `/use general` e `/use`: repositório ativo por usuário, persistido em
  `~/.dante/settings.json` e usado por toda execução sem `@alias` explícito (AD-14);
- resolvedor único de agente e contexto (AD-27): `/claude`/`/codex` → agente padrão;
  `@alias` explícito → repositório ativo → General. Overrides valem para uma execução e
  não alteram preferências; alias desconhecido, ativo inválido e prompt vazio recusam sem
  iniciar agente. O início de um one-shot cujo agente ou `@alias` difere do padrão avisa que o override vale só
  para aquela execução (#38).
- `/session start|list|select|stop|close` e `/steer`, com fila durante o turno e eventos
  agrupados no Telegram em linhas inteiras; `/agent set` e `/use` avisam quando a sessão ativa
  continua com outro agente ou contexto;
- entrega de resultados de jobs e eventos de sessão com retry/backoff, estado independente
  da execução em `/status` e recuperação de partes pendentes por `/resend` (AD-21).
- `/mode` consulta e escolhe o modo de trabalho das novas sessões — `manual` (aprovação), `auto`
  (automático) ou `plan` (planejamento) —, com padrão por usuário persistido em
  `~/.dante/settings.json`; `/session start` aceita modo explícito, a sessão mantém o modo até ser
  encerrada, `/status`, `/mode` e `/session start` o mostram, sem aviso na abertura implícita, e
  modo não suportado pelo agente é recusado antes de iniciar (AD-24). `/permissions` segue como interface de baixo nível do mesmo
  padrão. `/approve`, `/approve-session`, `/deny` e `/input` respondem a
  solicitações correlacionadas por sessão, turno e request, com expiração em cinco minutos
  e estado pendente em `/status` (AD-22).

- respostas técnicas renderizam blocos fenced Markdown como código nativo do Telegram,
  com linguagem, escape de HTML e divisão em partes válidas; comandos multiline/extensos
  usam blocos `bash`, com fallback para texto simples em rejeição de markup e recuperação
  por retry e `/resend` sem reexecutar agentes (#81); comandos respeitam a ordem da prosa
  anterior e aguardam a liberação segura de caudas de segredo retidas;
- aprovações oferecem botões inline com validação de dono, turno, request e expiração;
  `/approve`, `/approve-session` e `/deny` seguem disponíveis como fallback textual;
- execução como serviço systemd do usuário no WSL (`deploy/dante-service.sh`), com restart em
  falha, stop gracioso, logs no journald e distro iniciada no logon do Windows por tarefa agendada
  (AD-28);

Detalhes de uso: [README](../../README.md).

## Limitações atuais

- jobs e histórico somente em memória (perdidos ao reiniciar);
- como serviço, o D.A.N.T.E. fica disponível a partir do logon no Windows, não do boot (AD-28);
- sem worktrees, fila persistente ou execução concorrente isolada por Issue;
- sessões interativas e resultados recentes de entrega ficam apenas em memória; ao
  reiniciar o Worker, sessões e saídas pendentes não podem ser recuperadas;
- perfil `full` não é oferecido, pois não há mapeamento comum validado entre as CLIs;
- input humano do Codex só aparece no perfil `plan` (limitação do `app-server`, AD-19);
- streaming longo ainda chega em várias mensagens (uma por lote de linhas); editar uma única
  mensagem progressivamente não está implementado;
- a validação real foi feita contra as CLIs instaladas com a API do Telegram simulada; o
  dogfooding pelo Telegram real depende do bot do usuário;
- sem CI no GitHub: validação é local;
- áudio e vídeo enviados ao bot são recusados até haver ferramenta aprovada (#96);
- one-shot (`/claude`, `/codex`) não envia arquivos gerados: o `codex exec` não informa onde salvou a imagem;
  registros e cópias de arquivos enviados não sobrevivem ao reinício do Worker;
- a interpretação de imagens pelas CLIs reais foi validada no spike #93; na #95, os testes automáticos usam as CLIs
  simuladas do `Dante.ProcessProbe`, e a evidência com as CLIs reais é o `LiveImageEvidenceTests`, opt-in por
  `DANTE_LIVE_CLI=1`, executado em 2026-10-02 com Claude Code 2.1.287 e codex-cli 0.159.3: os quatro caminhos
  (sessão e one-shot de cada CLI) identificaram a imagem sintética; o bot do Telegram real não foi exercitado.

## Em andamento

- **Epic #92 — Mídias no Telegram**: spike #93 (AD-29), recebimento (#94), imagens aos agentes (#95) e artefatos (#97) entregues; áudio/vídeo (#96) e a imagem para LinkedIn (#98) dependem de decisão humana sobre ferramentas.

Para saber quem está trabalhando em qual Issue, consulte os comentários de turno na
própria Issue.

## Build e testes

Estado conhecido com #97:

```text
dotnet build Dante.sln   sucesso, 3 avisos CA1416 nos testes de deploy
dotnet test Dante.sln    495 testes aprovados, 4 pulados (evidência com CLIs reais, opt-in)
```

`InteractiveSessionEndToEndTests` exercita o caminho interativo completo (Telegram →
`SessionRegistry` → drivers reais → CLIs simuladas do `Dante.ProcessProbe`).

`InteractiveAgentProcessTests.GracefulExitDoesNotLeaveOrphanedChildProcess` (#62) pode ser
intermitente na suíte completa em WSL2 e passa isolado; já falhava assim antes da #63.
`TelegramBotApiTests.OrdinarySessionReplyReachesTelegramAsHtmlWithoutKeyboard` também falhou uma vez na suíte
completa durante a #94 e de novo no baseline da #95; passa isolado e nas execuções seguintes.

## Próximos marcos

1. mídias no Telegram (#92): áudio e vídeo (#96), imagem para LinkedIn (#98) e validação final (#99).
