# Estado atual

Retrato **substituível** do D.A.N.T.E. em `main`. Descreve o HEAD, não uma sessão: quem
muda o estado do projeto reescreve a parte afetada em vez de acrescentar entradas. O
histórico consolidado fica em [DEVELOPMENT_HISTORY](DEVELOPMENT_HISTORY.md).

Estado de Issues em andamento (worker, branch, handoff) **não** vive aqui: vive nas
próprias Issues e PRs do GitHub. Para o estado vivo do backlog, consulte o GitHub.

Última revisão: 2026-10-07, com isolamento (#150), fontes (#158), auditoria/exportação (#149), construção seletiva de contexto (#140), Brain por conversa natural (#157), continuidade do Brain nas sessões (#145) e métricas de continuidade (#148), e estrutura física da Infrastructure (#196), e convenção feature-first da Application (#203), e mappings da Application por feature (#204), e validação de entrada da Application (#205), e decomposição da conversa Brain (#206), e políticas puras de qualidade/contexto/métricas (#207), e administração de banco/migrations (#199), e composição modular da Infrastructure (#200), e ownership da composição na Application (#208), e regras arquiteturais executáveis da Application (#209), e Google Sheets como ferramenta genérica de planilhas (#224).

## Marcos

| Marco | Situação |
| --- | --- |
| MVP 1 — Telegram → D.A.N.T.E. → Claude/Codex → Telegram (Epic #1) | concluído |
| MVP 2 — Context-aware orchestration (Epic #18) | concluído |
| Agent Harness v1 (Epic #40) | concluído |
| MVP 3 — Conversational Context (Epic #32) | concluído (#33–#38); fechamento da Epic por decisão humana |
| Interactive Agent Sessions (Epic #60) | concluído |
| Mídias no Telegram (Epic #92) | em andamento: spike #93, recebimento #94, imagens aos agentes #95, áudio e vídeo #96, artefatos #97 e imagem para LinkedIn #98 entregues; #99 pendente |
| Cotas de uso pelo `/uso` (Epic #114) | entregue: spike #115 (AD-31), `/uso` com Codex (#116) e Claude (#117); fechamento da Epic por decisão humana |
| Limpar e compactar contexto (Epic #118) | entregue: spike #119 (AD-32), `/clear` (#120) e `/compact` (#121); fechamento da Epic por decisão humana |

## Funcionalidades disponíveis

- bot Telegram por long polling com allowlist de usuários;
- `/ping`;
- `/uso claude|codex` (#116, #117, AD-31): percentual usado da janela de sessão do provedor e da semana, e tempo até a
  janela de sessão renovar, da conta autenticada na CLI (inclusive uso fora do D.A.N.T.E.). Consulta num processo
  efêmero da CLI no workspace geral, sem sessão, turno ou cache: Codex por `account/rateLimits/read` (janelas
  reconhecidas pela duração, outros buckets à parte), Claude pelo `get_usage` experimental do stream-json (`five_hour`
  e `seven_day`, semanas por modelo à parte). Métrica ausente é "indisponível", reset vencido não é renovação, e erros
  de login, API key/provedor de nuvem, CLI antiga, timeout (20 s) e serviço são acionáveis;
- `/help` mostra comandos por categoria com descrições, exemplos fictícios e escopo das
  preferências/sessões/jobs; `/help comando` ou `/help /comando` detalha a sintaxe sem consultar
  dados locais; comandos desconhecidos orientam para `/help` (#106);
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
- áudio e vídeo aos agentes (#96, AD-29): voz, áudio, vídeo e video note são baixados (até 20 MB, contêiner conferido
  pelo conteúdo) e ficam pendentes como as imagens, ou são recusados antes do download quando faltam as ferramentas
  locais (`ffmpeg`/`ffprobe`, `whisper-cli` e o modelo `~/.dante/models/ggml-small.bin` ou `DANTE_WHISPER_MODEL`).
  Antes de o turno chegar ao agente, o D.A.N.T.E. transcreve a fala com timestamps (whisper.cpp) e amostra até 6
  quadros por vídeo, enviados como imagens; o texto declara a proveniência e o que não foi analisado (além de 10 min,
  entre quadros, sem trilha de áudio). Na sessão, a preparação roda dentro do turno já aberto: a fila mantém a ordem,
  `/session stop` cancela as ferramentas e falha encerra só aquele turno; no one-shot, roda dentro do job, e `/cancel`
  a interrompe. `/steer` não leva áudio nem vídeo e aceita só texto durante a preparação; limite de 10 min de processamento por mensagem;
- arquivos produzidos (#97, AD-29): imagem gerada pela sessão do Codex (`imageGeneration.savedPath`) é enviada sozinha,
  e `/send <caminho>` envia um arquivo do diretório da sessão ativa; caminho real validado contra a raiz do canal
  (symlinks resolvidos), até 50 MB, sem nomes de credencial, e nunca de sessão com segredos vinculados; imagem vai
  como foto e documento original, o resto como documento; cópia privada para retry, `/resend F000001` e `/status`;
- imagem para LinkedIn (#98, AD-29): `/vitrine <pedido>` (ou como legenda dos prints) abre um turno da conversa em que o
  agente responde só textos e arranjo num JSON validado (formato, título, subtítulo, cor, etiquetas, destaque, rodapé); o
  D.A.N.T.E. monta o PNG localmente com `ffmpeg`, colando os prints sem alteração (cantos arredondados, sombra,
  etiquetas), e o envia pelo canal de arquivos da #97. Ajustes são novos `/vitrine` na mesma conversa e geram versões
  `vitrine-T…-vN.png`; exige sessão ociosa; sem `ffmpeg`, recusa antes do agente; fonte Inter quando instalada, senão
  DejaVu Sans;
- planilhas (#224, AD-55): `/google connect|status|disconnect` conecta uma conta Google por OAuth local (callback
  loopback no WSL, PKCE, credencial cifrada, renovação automática, revogação) e `/planilha add|show|remove` e
  `/planilhas` cadastram planilhas por URL/ID com alias. Sessões iniciadas com a conta conectada recebem o servidor MCP
  `dante_planilhas` (Claude e Codex), com ferramentas genéricas para descrever, ler intervalos A1, buscar texto,
  atualizar células/intervalos, escrever por referência e acrescentar linhas; leitura sem aprovação, escrita pelo modo
  da sessão, alvo ambíguo/fórmula/mesclagem/limpeza em massa recusados, valor anterior e auditoria local por célula;
  XLSX existente no Drive tem caminho opcional por ID, sem conversão: habilitar API Drive,
  `Google__PermitirXlsxNoDrive=true` e reconectar. Mantém formato/ID/estilos e partes não
  alteradas, com versão esperada e ETag/If-Match; leitura bruta e cache de fórmulas, sem cálculo local;
- `/use @alias`, `/use general` e `/use`: repositório ativo por usuário, persistido em
  `~/.dante/settings.json` e usado por toda execução sem `@alias` explícito (AD-14);
- resolvedor único de agente e contexto (AD-27): `/claude`/`/codex` → agente padrão;
  `@alias` explícito → repositório ativo → General. Overrides valem para uma execução e
  não alteram preferências; alias desconhecido, ativo inválido e prompt vazio recusam sem
  iniciar agente. O início de um one-shot cujo agente ou `@alias` difere do padrão avisa que o override vale só
  para aquela execução (#38).
- `/session start|list|select|stop|close` e `/steer`, com fila durante o turno e eventos
  agrupados no Telegram em linhas inteiras; `/agent set` e `/use` avisam quando a sessão ativa
  continua com outro agente ou contexto; numa sessão Claude, texto de usuário iniciado por `/` (inclusive em
  `/steer` e com anexos) é recusado antes de interromper, enfileirar, levar anexos ou escrever ao processo, porque a
  CLI o executaria como comando (#128, AD-32); no Codex segue como texto;
- `/clear` (#120, AD-32): limpa a conversa da sessão ativa ociosa mantendo sessão, agente, contexto, modo, modelo e
  esforço — Claude pelo `/clear` interno do driver (confirmado por `conversation_reset`, novo `session_id`), Codex por
  thread nova no mesmo processo, trocada só após confirmação; recusa mantém a conversa anterior, confirmação incerta
  encerra a sessão; anexos pendentes e imagens da conversa anterior são descartados;
- `/compact` (#121, AD-32): compacta a conversa da sessão ativa ociosa com ao menos uma resposta, mantendo sessão, id
  upstream e configurações — Claude pelo `/compact` interno (confirmado por `compact_boundary`, com tokens antes/depois
  exibidos), Codex por `thread/compact/start` (turno próprio da CLI, sem métrica). Roda em segundo plano com aviso de
  início e de fim; enquanto isso a sessão recusa mensagens, `/clear` e troca de modo, `/status` mostra "compactando" e
  `/session stop` cancela (histórico mantido); limite de 10 min; confirmação incerta encerra a sessão;
- entrega de resultados de jobs e eventos de sessão com retry/backoff, estado independente
  da execução em `/status` e recuperação de partes pendentes por `/resend` (AD-21).
- `/mode` consulta e escolhe o modo de trabalho das novas sessões — `manual` (aprovação), `auto`
  (automático) ou `plan` (planejamento) —, com padrão por usuário persistido em
  `~/.dante/settings.json`; `/session start` aceita modo explícito. `/mode session <modo>` (#108)
  troca somente na sessão ativa ociosa: Claude confirma via controle, Codex aplica no próximo
  turno da mesma thread e exige confirmação das políticas; `/status` e `/mode` distinguem
  perfil efetivo e troca pendente. Turno ativo/fila/requests pendentes recusam sem interrupção
  automática; recusa mantém modo anterior, confirmação incerta encerra a sessão com erro.
  `/status`, `/mode` e `/session start` mostram o modo, sem aviso na abertura implícita, e
  modo não suportado pelo agente é recusado antes de iniciar (AD-24). `/permissions` segue como interface de baixo nível do mesmo
  padrão. `/approve`, `/approve-session`, `/deny` e `/input` respondem a
  solicitações correlacionadas por sessão, turno e request, com expiração em cinco minutos
  e estado pendente em `/status` (AD-22).
- Codex em sessão `auto` usa `on-request` + `auto_review`: pedidos de acesso são avaliados
  automaticamente pela CLI, como no `/codex`; recusas continuam possíveis. O driver exige
  confirmação do revisor pela CLI e a configuração vale para novas sessões; evidência real
  na CLI 0.159.3 criou uma branch Git e consultou o GitHub sem aprovação humana.
- input humano por botões e Reply (#105, AD-30): uma pergunta com até dez opções curtas
  oferece botões; texto livre, opções extensas e múltiplas perguntas usam Reply à mensagem
  original, correlacionada por usuário/chat/message_id e validada pelo registry; várias respostas
  seguem a ordem das perguntas, separadas por `|`. `/input` continua aceito e aparece como
  instrução somente nos transportes sem suporte; segredos ocultam perguntas e opções. A mensagem
  é atualizada após resposta, expiração, interrupção ou fechamento; Reply incorreto/tardio é
  recusado sem abrir novo turno. Mensagens comuns sem Reply continuam na conversa, sem adivinhação;

- respostas técnicas renderizam blocos fenced Markdown como código nativo do Telegram,
  com linguagem, escape de HTML e divisão em partes válidas; comandos multiline/extensos
  usam blocos `bash`, com fallback para texto simples em rejeição de markup e recuperação
  por retry e `/resend` sem reexecutar agentes (#81); comandos respeitam a ordem da prosa
  anterior e aguardam a liberação segura de caudas de segredo retidas;
- aprovações oferecem botões inline com validação de dono, turno, request e expiração;
  `/approve`, `/approve-session` e `/deny` seguem aceitos manualmente, mas as instruções só
  aparecem na mensagem quando o transporte não suporta os botões (#104), inclusive com
  detalhes ocultos por segredos; o botão e o comando de aprovação na sessão dependem do suporte do agente;
- execução como serviço systemd do usuário no WSL (`deploy/dante-service.sh`), com restart em
  falha, stop gracioso, logs no journald e distro iniciada no logon do Windows por tarefa agendada
  (AD-28);

Detalhes de uso: [README](../../README.md).

## Limitações atuais

- jobs e histórico somente em memória (perdidos ao reiniciar);
- `/uso claude` depende do `get_usage` **experimental** do Claude Code (pode mudar a cada versão; formato desconhecido
  vira "CLI sem suporte") e a CLI pode responder com leitura própria de até 1 min, ou até 1 h quando o serviço falha,
  sem informar a idade; contas por API key ou provedor de nuvem não têm cota de assinatura nas duas CLIs (AD-31);
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
- planilhas (#224): só Google Sheets; o servidor MCP só entra em sessões iniciadas depois de conectar a conta; one-shot
  (`/claude`, `/codex`) não recebe as ferramentas; o link de `/google connect` só funciona no navegador do próprio
  computador; sem exclusão de linhas/abas, formatação, gráficos ou descoberta pelo Drive; a validação real com a conta e a
  planilha de treino do usuário é o `LivePlanilhasEvidenceTests`, opt-in (`DANTE_LIVE_GOOGLE=1`), ainda não executado;
- áudio e vídeo chegam ao agente só como transcrição e quadros amostrados (nenhuma CLI recebe o arquivo); a
  transcrição é automática e local, e o vídeo é visto só nos quadros amostrados; animações (GIF) são recusadas;
- one-shot (`/claude`, `/codex`) não envia arquivos gerados: o `codex exec` não informa onde salvou a imagem;
  registros e cópias de arquivos enviados não sobrevivem ao reinício do Worker;
- a evidência de áudio e vídeo com as ferramentas reais é o `LiveMediaEvidenceTests`, opt-in por `DANTE_LIVE_MEDIA=1`
  (e `DANTE_LIVE_CLI=1` para os agentes), executado em 2026-10-02 com ffmpeg 8.0.1, whisper.cpp 1.8.3 (modelo
  `small`), Claude Code 2.1.287 e codex-cli 0.159.3: voz sintética (TTS do Windows, en-US, em OGG/Opus) transcrita
  e respondida pela sessão do Claude, e vídeo sintético (vermelho, depois azul) amostrado e descrito na ordem pela
  sessão do Codex; o bot do Telegram real não foi exercitado;
- a evidência da `/vitrine` com as ferramentas reais é o `LiveShowcaseEvidenceTests`, opt-in por `DANTE_LIVE_MEDIA=1`
  (e `DANTE_LIVE_CLI=1` para os agentes), executado em 2026-10-02 com ffmpeg 8.0.1, Claude Code 2.1.287 e codex-cli
  0.159.3: prints sintéticos viraram PNGs de celular e desktop, e as sessões dos dois agentes responderam o JSON
  válido destacando o print de alto contraste; o bot do Telegram real não foi exercitado;
- a interpretação de imagens pelas CLIs reais foi validada no spike #93; na #95, os testes automáticos usam as CLIs
  simuladas do `Dante.ProcessProbe`, e a evidência com as CLIs reais é o `LiveImageEvidenceTests`, opt-in por
  `DANTE_LIVE_CLI=1`, executado em 2026-10-02 com Claude Code 2.1.287 e codex-cli 0.159.3: os quatro caminhos
  (sessão e one-shot de cada CLI) identificaram a imagem sintética; o bot do Telegram real não foi exercitado.

## Direção de armazenamento do Brain

A #135 formaliza a AD-34: [PostgreSQL canônico](../brain/POSTGRESQL_STORAGE.md),
full-text lexical, pgvector derivado, originais em filesystem quando apropriado e
export Markdown/JSON. Há estratégia de migrations, backup/restore consistente com
fontes e roteiro preparatório para WSL. A persistência opcional EF/PostgreSQL foi entregue pela #160 na Infrastructure;
o Worker oferece operações Brain por conversa natural (#157); a WebApi continua sem endpoints funcionais Brain.
O roteiro de banco não foi executado nesta entrega; Docker não está acessível na
distro utilizada. Não é necessário instalar PostgreSQL para rodar o produto atual.

## Fundação hexagonal (#165)

Solution com Dante.Domain, Dante.Application, Dante.Infrastructure, Dante.Worker
e Dante.WebApi em .NET 10. Referências para o núcleo e dependências do Domain/
Application são verificadas por testes arquiteturais. Hosts compartilham
AddApplication/AddInfrastructure; Program do Worker delega a composição legada a
AddWorker, sem alteração funcional. WebApi oferece host independente com health,
ProblemDetails e OpenAPI em Development (#170).

AD-35/AD-36 formalizam a migração incremental, Guid Id via EntidadeBase (base a
implementar na #171), FKs com Id no início e vocabulário PT-BR. A fundação precedeu as entidades
Brain, EF, CRUD e Mapster, hoje descritos nas entregas abaixo. A PR #163 segue em revisão, reestruturada pela #160.

## Extração do núcleo (#166)

Primeiro lote do legado fora do Worker, sem mudança de comportamento (AD-38):
AgentKind e AssistantSettings no Domain; AgentContextResolver, JobExecutionContext
e ResolvedRepositoryEnvironment na Application, sobre as portas PT-BR
IWorkspaceGeral, ICatalogoDeRepositorios (RepositorioCadastrado) e
IPreferenciasDoAssistente, implementadas por adapters hoje na Infrastructure (#167). O Domain não
carrega caminho, GitHub, variável do host nem ambiente; RepositoryDefinition segue
no adapter. Nomes legados em inglês ficam como exceção temporária explícita, e
`<Using>` globais no Worker/testes evitam big-bang. Testes arquiteturais cobrem
referências do núcleo compilado, ausência de TId, de IO no núcleo e de conceitos
operacionais no modelo do Domain. Sessões, jobs, drivers e Telegram seguem no
Worker; adapters externos foram migrados em parte pela #167.

## Host HTTP (#170)

Dante.WebApi compõe Application/Infrastructure, sem referência ao Worker/Telegram.
GET /health operacional retorna DTO PT-BR; falhas/status HTTP têm ProblemDetails
neutro com traceId. OpenAPI apenas em Development. Nenhum endpoint Brain, regra de
negócio, EF ou repository foi introduzido. Oito testes HTTP cobrem inicialização,
health/503, 404/405, sanitização e ambientes. [Operação HTTP](../maintainer/WEBAPI.md).

## Em andamento

- **Epic #92 — Mídias no Telegram**: spike #93 (AD-29), recebimento (#94), imagens aos agentes (#95), áudio e vídeo (#96), artefatos (#97) e imagem para LinkedIn (#98) entregues; validação final (#99) pendente.
- **Epic #114 — Cotas de uso pelo `/uso`**: spike #115 (AD-31, [`docs/spikes/usage-quotas`](../spikes/usage-quotas/README.md)), Codex (#116) e Claude (#117) entregues; aguarda revisão e fechamento humano.
- **Epic #118 — Limpar e compactar contexto**: o spike #119 comprovou, nas duas CLIs reais, compactação e limpeza no mesmo processo da sessão — Claude por `/compact`/`/clear` no stream-json, Codex por `thread/compact/start` e nova thread — e definiu contrato e regras (AD-32, [`docs/spikes/clear-compact`](../spikes/clear-compact/README.md)); `/clear` (#120) e `/compact` (#121) entregues; aguarda revisão e fechamento humano.

Para saber quem está trabalhando em qual Issue, consulte os comentários de turno na
própria Issue.

## Build e testes

Estado conhecido após a extração do núcleo #166:

```text
dotnet build Dante.sln   sucesso, 3 avisos CA1416 nos testes de deploy
dotnet test Dante.sln    784 testes aprovados, 18 pulados (evidência com CLIs e ferramentas reais, opt-in)
```

`LocalServiceDeploymentTests.InstallWithoutTokenLeavesTheServiceDisabled` falhou no baseline da #166, antes de
qualquer mudança, e segue falhando na mesma working tree; a causa (provavelmente ambiental) não foi
investigada nesta Issue.

`LiveSessionModeEvidenceTests` (#108), opt-in com `DANTE_LIVE_CLI=1`, passou para Claude Code
2.1.287 e Codex 0.159.3 nas seis transições dirigidas entre modos, mantendo conversa e esforço
`high` no mesmo processo/session/thread.

`LiveUsageEvidenceTests` (#116, #117), opt-in com `DANTE_LIVE_CLI=1`, passou em 2026-10-02 com codex-cli 0.159.3
(login ChatGPT, plano Plus) e Claude Code 2.1.287 (login claude.ai, plano Pro): janela de sessão de 5 h, semana de
7 dias e reset lidos pelo leitor de produção nas duas CLIs, sem turno.

`LiveContextEvidenceTests` (#120, #121), opt-in com `DANTE_LIVE_CLI=1`, passou em 2026-10-03 com Claude Code 2.1.287 e
codex-cli 0.159.3: com os drivers de produção (General, modo plan), o marcador dito antes do `/clear` não foi
recuperado depois ("UNKNOWN") e o id upstream mudou nas duas CLIs; depois do `/compact`, marcador e instrução
(maiúsculas) seguiram válidos nas duas, com 6517 → 1837 tokens informados pelo Claude e nenhuma métrica do Codex.

`InteractiveSessionEndToEndTests` exercita o caminho interativo completo (Telegram →
`SessionRegistry` → drivers reais → CLIs simuladas do `Dante.ProcessProbe`).

`InteractiveAgentProcessTests.GracefulExitDoesNotLeaveOrphanedChildProcess` (#62) pode ser
intermitente na suíte completa em WSL2 e passa isolado; já falhava assim antes da #63.
`TelegramBotApiTests.OrdinarySessionReplyReachesTelegramAsHtmlWithoutKeyboard` também falhou uma vez na suíte
completa durante a #94 e de novo no baseline da #95; passa isolado e nas execuções seguintes. Na #96 falhou no baseline
(antes de qualquer mudança) e em duas de três execuções completas, sempre passando isolado.

## Próximos marcos

A Epic #133 tem [arquitetura alvo documentada](../maintainer/ARCHITECTURE.md#16-arquitetura-alvo-do-dante-brain-133-134)
(AD-33, #134). O Brain é opcional no runtime; Knowledge Core existe em Domain/Application
(#138); persistência, busca, snapshot, Context Pack e a continuidade nas sessões (#145) foram
entregues depois, assim como as métricas de continuidade (#148). Resta a validação E2E go/no-go (#147).
A arquitetura separa conhecimento, memória de trabalho e histórico; não altera
persistência ou permissões do Worker atual.

1. mídias no Telegram (#92): validação final (#99).

## Mapper compartilhado da Application (#169)

Mapster registrado por AddApplication via IMapsterTypeAdapter, com configurações
centralizadas, expressões explícitas por par/direção e sem automapping de campos
sensíveis/identidade ou atualização direta de entidades existentes. Domain continua
sem Mapster. AppServices podem receber o adapter; hosts não duplicam mappings.
Seis testes novos cobrem contratos/invariantes/DI/concurrency. Guia e convenções:
[mappings da Application](../development/mapping.md). Nenhum DTO/entidade funcional
Brain ou base CRUD foi antecipado.


## Base CRUD (#171)

EntidadeBase (Guid Id gerado pelo domínio, setter protegido) no Domain e
IRepository, IUnitOfWork, ICrudBasicoAppService e CrudBasicoAppService na
Application, sem TId (AD-39). Criação pelo mapping explícito com constructor do
domínio, atualização por métodos do domínio no AppService específico e pesquisa
por consulta do repository específico. Onze testes cobrem as operações sobre
consumidores fictícios em PT-BR e a convenção de nomes por reflexão; suíte com
790 aprovados e 18 pulados; LocalServiceDeploymentTests.InstallWithoutTokenLeavesTheServiceDisabled
já falhava no baseline, antes de qualquer mudança, e não foi investigado. Guia: [base CRUD](../development/crud.md). Nenhuma entidade
funcional, implementação EF de repository/unit of work (#168) ou registro DI foi
antecipado.

## Espaços de Conhecimento (#152)

EspacoDeConhecimento no Domain (proprietário IdUsuario imutável, nome/descrição de
apresentação, estado Ativo/Arquivado, arquivado somente leitura até reativar) e
IEspacoDeConhecimentoRepository, IEspacoDeConhecimentoAppService,
EspacoDeConhecimentoAppService, EspacoDeConhecimentoDto e EspacoDeConhecimentoSearchDto
na Application, sobre a base CRUD, com arquivar/reativar e pesquisa sempre escopada ao
proprietário (AD-40). Mappings registrados em AddApplication. Vinte e três testes de
Domain/Application, sem banco; suíte com 820 aprovados e 18 pulados. Sem persistência
concreta (#160), registro DI do AppService, Projeto, tenant ou autorização (#150).

## Adapters na Infrastructure (#167)

Workspace geral, catálogo de repositórios, preferências persistidas, execução das CLIs
(one-shot, processo interativo, catálogo de modelos) e leitura de cotas saíram do Worker
para Dante.Infrastructure, compostos por AddInfrastructure e portanto disponíveis aos dois
hosts (AD-41). Portas usadas pelo Worker (runners, catálogo, cotas, anexo neutro) estão na
Application, assim como a seleção de modelo; o Domain recebe só o perfil de permissão, e
os nomes/rótulos dos modos seguem no Worker como apresentação. Comportamento inalterado:
suíte com 800 aprovados e 18 pulados. Sessões/drivers, jobs, mídia, artefatos e Telegram seguem no
Worker; EF Core/PostgreSQL é a #168.

## Projetos (#136)

Projeto no Domain, preso a um EspacoDeConhecimento (fixo na criação), com nome e
descrição/objetivo de apresentação, estado Ativo/Arquivado (arquivado somente leitura até
reativar) e repositório cadastrado como associação opcional por alias, nunca identidade.
IProjetoRepository, IProjetoAppService, ProjetoAppService, ProjetoDto e ProjetoSearchDto na
Application, sobre a base CRUD: criação só em espaço existente e ativo, associação validada
pelo catálogo de repositórios, arquivar/reativar e pesquisa sempre escopada ao espaço
(AD-42). Mappings registrados em AddApplication. Vinte e cinco testes de Domain/Application,
sem banco; suíte com 848 aprovados e 18 pulados. Repository Mode e /use não mudam. Persistência/DI foram entregues na #160, autorização na #150 e seleção
de projeto Brain na conversa na #157.

## Fundação EF Core + PostgreSQL (#168)

Persistência opcional via ConnectionStrings:Dante em AddInfrastructure: DanteDbContext,
Repository<TEntity>, UnitOfWork scoped e configuração base Guid/xmin. Migration EF
inicial prepara schemas, sem mapear entidades funcionais Brain nem alterar stores JSON.
Conflitos são traduzidos em exception da Application; commit transacional e conexões
fora do checkout/ambiente dos agentes. Guia: [persistência](../development/persistence.md).
Persistência funcional está na #160; full-text/pgvector seguem na #154.

## Núcleo de Conhecimento (#138)

Conhecimento no Domain com escopo espaço/projeto, tipos/status/sensibilidade PT-BR,
proveniência, autoria, validade, tags, conteúdo/JSON e revisões imutáveis. Confirmar,
Corrigir, Invalidar e Substituir preservam evidência e exigem revisão esperada;
inferência não vira confirmação por confiança e correção de confirmado exige nova
confirmação (AD-44). ConhecimentoAppService e ports/DTOs na Application sobre CRUD,
com validação de escopo ativo, pesquisa limitada e mappings explícitos. DI/repositories/EF estão na #160, captura na #139 e relações na #153.
Autorização central (#150) protege os canais; a política de sensibilidade da #155 já protege as saídas específicas do Brain. 49 testes novos sem banco com fakes;
build aprovado e suíte final com 903 aprovados, 19 pulados e zero falhas.
Guia: [núcleo de Conhecimento](../development/knowledge.md).

## Persistência EF do Brain (#160)

Infrastructure mapeia EspacoDeConhecimento, Projeto e Conhecimento com migrations EF,
repositories específicos e DI scoped de AppServices. Histórico/proveniência e estado
canônico transacionais, FKs compostas e xmin; health e backup/restore explícitos nos
dois hosts. PR #163 reestruturada na mesma branch; modelo genérico/SQL manual removido.
Banco permanece opcional. Busca, fontes e identidade Telegram são compostas pelas
entregas #154/#158/#150, sem acoplar o storage ao canal.
Guia: [operação local](../brain/LOCAL_STORAGE.md).

## Relações de Conhecimento (#153)

RelacaoDeConhecimento no Domain e AppService/ports/DTOs na Application, com EF e
migration específica na Infrastructure. Nove tipos explícitos, proveniência,
deduplicação simétrica, FKs e substituição integrada ao histórico transacional.
Vizinhança escopada com BFS, profundidade/custo limitados e sinal de truncamento.
Guia: [relações](../development/relations.md). Policy, busca/expansão e UX Telegram são compostas por #150/#140/#157.

## Captura de Conhecimento (#139)

Pipeline neutro selecionado → CandidatoDeConhecimento → correção/decisão explícita →
Conhecimento, com natureza de origem, evidência/justificativa, auditoria por revisão,
deduplicação conservadora e confirmação transacional. Sugestões permanecem pendentes;
inferências não viram fatos por confirmação. EF/migration de candidatos e consolidação
Incidente/Solucao/Aprendizado com relações na mesma transação. Guia: [captura](../development/capture.md).
Sem observador de transcripts/turnos; adapter natural Telegram e policy foram
compostos pelas #157/#150.

## Política de sensibilidade (#155)

Leitura protegida valida proprietário/escopo, limita Confidencial e impede Secreto em contexto automático, exportação e embeddings externos. Redaction centralizada e defesa de captura/correção rejeitam credenciais óbvias, incluindo evidências; referências secret://host/NOME não resolvem valores. Classificação corrigível com histórico; adapters futuros devem consumir a projeção protegida. Guia: [sensibilidade](../development/sensitivity.md).

## Contexto de trabalho (#156)

Snapshot persistente por espaço/projeto, com campos operacionais selecionados e 8.000 caracteres. Atualização substitui versão ativa, conserva apenas metadados da revisão anterior e usa xmin. Retomada valida proprietário, sensibilidade e expiração; não cria Conhecimento ou guarda transcript/raciocínio privado. /clear e /compact continuam limitados à conversa upstream e não alteram snapshots. Guia: [contexto de trabalho](../development/working-context.md).


## Busca do Brain (#154)

Busca full-text PostgreSQL com fallback lexical, filtros de escopo/sensibilidade/validade e paginação. Semântica pgvector exata e ranking híbrido por modelo/revisão; índices derivados com pendências duráveis e reindexação explícita, embeddings HTTP opcionais sem presumir assinatura das CLIs. Secreto não entra no índice lexical ou vetorial; reclassificação remove derivados, migration corretiva limpa índices antigos e rebuild respeita a política, inclusive sob concorrência. Consulta por ID preserva apenas metadados permitidos sem depender do índice lexical. Fontes brutas rastreáveis participam da busca pela #158. Guia: [busca](../development/search.md).

## Qualidade do Brain (#159)

Revisão manual limitada detecta duplicatas/contradições potenciais, órfãos, fontes sem referência e validade/confirmação. Consolidação explícita preserva todas as proveniências e substitui originais transacionalmente. CONTRADIZ tem resolução auditável; conflitos abertos, histórico inativo/substituído e itens fora de validade não entram na seleção neutra para contexto. Inferência não substitui confirmação automaticamente. Guia: [qualidade](../development/quality.md).
## Isolamento do Brain (#150)

Operações compostas por DI exigem identidade e escopo scoped, estabelecidos pelo adapter
antes do acesso. Ausência de identidade falha fechado. Tenant/proprietário do espaço
são imutáveis; filtros EF e validação de escrita cobrem espaços, projetos, conhecimento,
candidatos, relações e snapshot. Busca e manutenção validam o mesmo contexto antes de
consultas SQL. Telegram autorizado recebe identidade Guid determinística por tenant e
ID numérico, estável após reinício; nomes e prompts não definem identidade/permissões.
O tenant local é o padrão, com DANTE_BRAIN_TENANT opcional. Contextos construídos
diretamente são administrativos (migrations/backup/fixtures), não uma porta de usuário.
WebApi não oferece login nem endpoints Brain. Fontes e Context Builder reutilizam
essa policy nas implementações #158/#140.

## Fontes brutas rastreáveis (#158)

DocumentoFonte aceita notas, Markdown e texto local selecionado sob raiz explícita,
com hash/revisão/escopo e isolamento da #150. Trechos Unicode reconstruíveis vivem em
brain_index, com full-text e embeddings opcionais, e aparecem em BuscaDoBrain como
FonteBruta. Selecionar um trecho cria candidato com citação exata; confirmação é
separada. Reimportação exige revisão, invalida derivados e não muda conhecimento
confirmado. Remoção limpa texto/índices, preservando citações já consolidadas.
[Contrato e operação](../development/fontes.md).

## Inspeção e exportação do Brain (#149)

InspecaoDoBrainAppService oferece auditoria paginada por tipo/status/origem/tag e
exportação Markdown/JSON versionada, preservando IDs, relações, provas e revisões
permitidas. Auditoria de espaço inclui projetos, inclusive sem projeto; projeto
selecionado é restrito. Snapshot repeatable-read, Secret excluído de export, redaction
nos dois formatos e omissão explícita de itens obsoletos/removidos. Os comandos locais
--brain inspect/export são fallback; export não sobrescreve arquivos e usa 0600 no
Unix. [Política, formato e limites](../development/auditoria-brain.md).

## Construção seletiva de contexto (#140)

ConstrutorDeContextoAppService monta PacoteDeContexto neutro com busca lexical/híbrida,
expansão controlada de relações, snapshot ativo, rerank, deduplicação e orçamento de
itens/tokens estimados. Exclui obsoletos, Secret e conflitos abertos, inclusive quando
a outra ponta é protegida. IDs/revisões/origens e motivos de descarte são rastreáveis;
construção e confirmação de injeção são etapas distintas. Não usa transcript completo.
A integração às sessões da conversa natural é a #145; [contrato](../development/context-builder.md).

## Brain por conversa natural (#157)

Worker roteia intenções naturais de consulta/experiência/captura/correção/invalidação/
relação/origem ao núcleo, com identidade da allowlist e seleção de espaço/projeto por
nome. Captura explícita cria candidato, confirmação consolida; alterações ambíguas ou
destrutivas pedem alvo inequívoco e confirmação com revisão. Estado transitório isolado
por usuário/chat/tópico/escopo, com expiração e consumo único. IDs internos não são a
UX principal. Sem banco ou em mensagens não Brain, fluxo legado continua disponível.
[Exemplos e regras](../development/conversa-brain.md).

## Continuidade do Brain nas sessões (#145)

Mensagens comuns às sessões levam o PacoteDeContexto do escopo selecionado no chat/tópico
como dado citado antes do pedido: bootstrap no primeiro envio de cada conversa upstream
(2048 tokens estimados) e refresh só com chaves novas ou revisadas (1024). Só o aceito pela
sessão conta como injetado; `/clear`, `/compact` concluído e troca de espaço/projeto
reiniciam o bootstrap sem alterar Brain ou snapshot. Sessão nova, com Claude ou Codex,
retoma pelo Brain, sem transcript. ContextoDeTrabalho é atualizado e consultado por
conversa (`atualize o contexto de trabalho: ...`), Reply a uma resposta com `documente
isso` cria candidato só do trecho citado, e `/status` mostra escopo e custo do último envio.
Estado de injeção em memória; `/steer`, `/vitrine` e one-shot não recebem pacote (AD-46).
[Continuidade](../development/continuidade-brain.md).

## Métricas de continuidade do Brain (#148)

Envios aceitos (recuperados/selecionados/injetados/descartados, custo do pacote e do
snapshot, pedido sem pacote, conhecimento armazenado), fins de turno (resposta estimada e
uso informado pelas CLIs, agora no `TurnCompletedEvent`) e avaliações humanas vão para
`~/.dante/brain/metricas.jsonl`, sem conteúdo. `métricas do Brain` compara cada retomada
com o histórico bruto das sessões anteriores do escopo e indica ganho, neutralidade,
regressão ou dados insuficientes por limiares fixos, com alertas de qualidade de
`avalie a retomada: ...`. Dado ausente fica indisponível (AD-47). Os dados alimentam a
#147. [Métricas](../development/metricas-brain.md).

## Estrutura física da Infrastructure (#196, #197)

A antiga pasta `Persistencia` foi desfeita: DbContext/factory em `Data`, migrations e
snapshot em `Data/Migrations`, Repository/UnitOfWork/EntidadeConfiguration em
`Persistence`, administração do PostgreSQL em `Banco` e a consulta de qualidade em
`QualidadeDoBrain` (AD-48). Cada entidade persistente tem `<Entidade>DbMapping` e
repository específico em `Modulos/<Modulo>`, com o nome do módulo do Domain e namespace
igual à pasta (AD-50); um teste recusa mapping/repository fora do módulo ou tipo
específico em `Persistence`. Só nomes de classe, namespaces e usings mudaram: as dez
migrations seguem reconhecidas e aplicadas, sem mudança de modelo nem migration nova.
DbContext, administração e DI seguem nas #198–#201 (Epic #195).

## Convenção de módulos da Application (#203, #204)

Features da Application seguem convenção documentada para módulos simples e
complexos (AD-49, [guia](../development/application.md)): um tipo público por
arquivo e namespace da feature mesmo em subpastas. Os agregadores `ContratosDe*.cs`
e os DTOs/ports declarados em arquivos de AppService ou de política foram separados;
Auditoria, Busca, Conversa, Fontes e Qualidade usam `Contratos/` e `Portas/`. Sem
mudança funcional nem de namespace; um teste arquitetural novo recusa namespaces
abaixo da feature. Mappings Mapster ficam em `<Entidade>Mapping` de cada feature, e
`MapeamentosDaApplication` só compõe a lista explícita (AD-51,
[mappings](../development/mapping.md)); pares, direções e payloads não mudaram.
Validação, decomposição, DI e regras executáveis vieram nas #205–#209 (Epic #202).

## Regras arquiteturais da Application (#209)

As fronteiras e a organização da Application são verificadas por testes:
`ApplicationArchitectureTests` recusa referência além de Domain/Mapster/abstrações de
DI, Domain com interface/mapper/validator/repository/DTO, porta declarada fora da
Application, repository da Infrastructure sem a porta da feature homônima, arquivo
fora da estrutura de módulo simples/complexo, tabela de classificação divergente do
[guia](../development/application.md), validator/mapping fora da feature e host
com AppService, validator, mapping, repository ou port implementada.
`ApplicationCompositionTests` resolve todos os serviços de `AddApplication` com fakes
dos ports, sem Infrastructure, e fixa idempotência e lifetimes. O guia documenta
responsabilidade por camada, quando criar AppService/fachada ou extrair caso de uso
e o roteiro de um módulo novo. Sem mudança de código de produção.

## Validação de entrada da Application (#205)

Checks estruturais de entrada (IDs obrigatórios, limites/paginação, tamanhos, enums,
formato e combinação de campos) saíram dos AppServices para `<Conceito>Validator`
estáticos por feature e para o helper `Comum/ValidacaoDeEntrada` (AD-52,
[guia](../development/application.md)). Regras que dependem de repository/estado
continuam no AppService e invariantes no Domain. Mensagens, tipos de exceção e
`ParamName` foram preservados, e o check segue no mesmo ponto do fluxo. A Application
passa a expor internals a `Dante.Tests`, como Infrastructure e Worker, para testar os
validators isoladamente. `MetricasDoBrain` mantém seu check inline.

## Isolamento técnico do DbContext (#198)

DanteDbContext delega filtros automáticos a FiltrosDoBrain e validação de escrita
a ValidacaoDeEscritaDoBrain, em Data. Escopo/sensibilidade compartilhados usam
expressões EF parametrizadas pelo contexto atual; snapshots continuam sem Secreto.
SaveChanges síncrono/assíncrono preserva validação fail-closed e modo administrativo
explícito, sem mudar mappings ou migrations. Testes verificam parâmetros por contexto,
ausência de identidade, escrita cross-user/tenant e isolamento espaço/projeto.

## Casos de uso da conversa Brain (#206)

ConversaDoBrainAppService preserva a entrada do Worker e orquestra intenções,
validação de acesso, estado pendente e consumo único da confirmação. Consulta,
captura, fontes, alterações confirmadas, inspeção, contexto de trabalho e avaliação
ficam em casos coesos em CasosDeUso, com dependências próprias e namespace da feature.
ContextoDaConversa existe por chamada e compartilha chave/estado/proveniência sem
service locator; o store continua responsável pelo consumo atômico e expiração.
Respostas e regras de revisão/sensibilidade são preservadas. Testes diretos dos casos
cobrem captura citada, inferência, proteção, preparação de correção e entrada inválida.
O registro scoped acompanha a fachada em AddApplication (#208).

## Políticas de qualidade, contexto e métricas (#207)

ManutencaoDoBrainAppService mantém acesso, consultas e transações; AnaliseDeQualidade
recebe o instante e dados já autorizados para analisar duplicatas, contradições,
validade e truncamento sem alterar entidades. ConstrutorDeContextoAppService delega
filtros a ElegibilidadeDeContexto e precedência/deduplicação/sobreposição/orçamento
à SelecaoDeContexto. AgregacaoDeMetricas e ApresentacaoDeMetricas separam cálculo e
texto do acesso autenticado ao registro, mantendo os métodos estáticos existentes.
Casos puros ficam em CasosDeUso com namespace da feature; os contratos de Contexto
e Métricas seguem a organização de módulo complexo. Sem alteração de provider,
limiares, mensagens ou transações; testes isolados cobrem os algoritmos.

## Administração de banco e migrations (#199)

ComandosDoBrain preserva a interface local --brain nos dois hosts e separa
importação/auditoria/busca de Banco/ComandosDoBanco. Apenas administração explícita
resolve AdministracaoDoBanco; startup normal não migra. Migrations e snapshot
continuam em Data/Migrations, histórico em brain_meta e canônico em brain_data,
sem migration nova ou projeto Migrator. Fluxo único de geração/listagem/validação
e aplicação documentado no [guia](../development/persistence.md). Health, backup,
restore e códigos 0/1 preservados.

## Composição modular da Infrastructure (#200, #201)

`AddInfrastructure` mantém a API pública e delega a extensões internas em
`Composicao/`: banco/EF e administração, repositories e adapters do Brain,
contexto local e execução de agentes. A classe pública `DependencyInjection` e seu
namespace permanecem compatíveis. Sem conexão não há registros de banco/adapters Brain;
adapters locais continuam disponíveis. A #208 transfere os 22 registros próprios
da Application para `AddApplication`, preservando lifetimes e a API dos hosts.
Testes validam ausência de conexão, unicidade
de registros, validação de DI e isolamento scoped/compartilhamento singleton.

## Proteções da Infrastructure (#201)

Guia [Infrastructure](../development/infrastructure.md) consolida estrutura, novas
entidades/mappings/repositories, migrations e administração explícita sem Migrator.
Testes arquiteturais impedem persistência/migrations nos hosts e migração no startup.
Cobertura administrativa valida migrate idempotente, health pendente/histórico
desconhecido e mensagem de falha sem credenciais; complementa os testes existentes
de filtros, sensibilidade, UoW, concorrência e backup/restore em PostgreSQL real.

## Ownership da composição na Application (#208)

`AddApplication` registra autorização, AppServices e casos da conversa por lista
explícita, além de mapping/policy. Serviços dependentes de ports usam factories
scoped para conservar a inicialização sem banco; port ausente falha na resolução.
Infrastructure registra apenas adapters técnicos. Validators/policies estáticos
continuam puros. DI do DbContext exige autorização, sem fallback administrativo
quando AddApplication é omitido. Testes de composição resolvem a lista integral
com adapters e um CRUD com fakes sem Infrastructure (AD-54).

## Planilhas genéricas (#224)

Feature `Planilhas` na Application (AD-55) com contratos provider-agnostic e sem domínio:
`PlanilhasAppService` sobre `IPlanilhaService`, `IConexaoDePlanilha`, `ICadastroDePlanilhas` e
`IAuditoriaDePlanilhas`. A Infrastructure implementa Google Sheets por HTTP (OAuth local com PKCE e
credencial AES-GCM; adapter com renovação após 401 e repetição de 429/5xx), cadastro e auditoria em
`~/.dante/planilhas`. O Worker expõe a capacidade às sessões por MCP stdio (`--mcp-planilhas`) e
mapeia a aprovação MCP do Codex (`mcpServer/elicitation/request`) para o fluxo de aprovações. A
planilha de treino é cenário E2E sobre o emulador da API, sem código de treino na produção; o
`LivePlanilhasEvidenceTests` cobre a planilha real (opt-in). Spike com as CLIs reais (Claude Code
2.1.287, codex-cli 0.159.3) confirmou o servidor MCP do Worker, leitura sem aprovação e escrita por
aprovação. [Guia](../development/planilhas.md).

A ampliação da #224 acrescenta `GooglePlanilhasAdapter`, que seleciona por MIME o adapter
Sheets existente ou `GoogleDriveXlsxAdapter` (Drive v2, que conserva Files.etag). XLSX usa
`DocumentoXlsx` em memória, sem pacote novo: lê shared strings, valores, fórmulas em cache
e mesclagens; escreve células e acrescenta linha sem alterar o formato/ID. Application
propaga revisão esperada e observações neutras. Permissão Drive é opt-in na autorização,
persistida na credencial cifrada para o processo MCP. Versão divergente/If-Match 412, ETag
ausente/fraco, aba protegida, assinatura ou alvo em fórmula compartilhada/matricial recusam
escrita. Sem recálculo ou reprodução de formatos de exibição; limites no guia. Testes
emulados cobrem preservação, conflitos, OAuth, MCP e proteções. O teste opt-in no XLSX real do Drive
comprovou escrita, leitura posterior e restauração no mesmo arquivo, com autorização do
usuário. Leituras do teste repetem apenas conflitos transitórios de revisão após upload;
escritas não são reaplicadas automaticamente. HTTP 412 real não foi provocado; proteção
condicional continua coberta pelo emulador.

A correção de review da #224 limita busca/valores Sheets a 250 mil posições e respostas a
16 MB; descrição de grades maiores não calcula área usada. Append não repete rede/5xx
com resultado incerto. Cadastro/auditoria têm lock de arquivo entre processos; falha de
auditoria após escrita informa que a operação foi aplicada e não deve ser repetida.
Cadastro por alias/ID e mutações de regiões são atômicos sob o lock entre processos,
incluindo validação e leitura do estado atual. MCP anuncia sobrescritas de valores
com destructiveHint=true.
Revogação Google só é confirmada em sucesso; configuração DANTE_GOOGLE_KEY é recusada
para evitar incompatibilidade com MCP, e segredo OAuth/chave são filtrados dos agentes.

## Brain por ferramentas MCP (#226)

Sessões Claude/Codex com configuração, storage e escopo Brain autorizados recebem
`dante_brain`, composto com `dante_planilhas` por providers independentes. MCP stdio
encaminha ao Worker por pipe local exclusivo de sessão; Worker mantém credenciais e
chama AppServices. Troca de conversa/escopo exige nova sessão; encerramento revoga o pipe.
Ferramentas cobrem busca, proveniência, captura/correção/listagem de candidatos,
propostas de consolidação/descarte/relação e snapshot operacional. Aprovação da CLI
não confirma negócio: o usuário confirma propostas no Telegram, com expiração e
revisão esperada. Captura preserva título/tags opcionais no histórico JSON e promoção;
sem migration ou endpoints WebApi. [Guia](../development/brain-mcp.md), AD-56.

O MCP usa a mensagem autenticada do turno ativo, preservada junto à entrada na fila.
Mensagens recebidas/enfileiradas não substituem essa evidência; entradas sem contexto
autenticado revogam o acesso. Captura e correção factual exigem conteúdo na origem.
Relações são projetadas somente quando ambos os alvos permitem leitura; sua prova
bruta não é exposta no canal MCP. Sessão ausente/revogada falha fechado.
