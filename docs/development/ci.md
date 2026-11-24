# Integração de CI — contratos, provedor GitHub Actions e andamento do PR

Guia da integração de CI do D.A.N.T.E. (Epic #234; contratos na #236, provedor GitHub Actions na #237, andamento do CI
de um PR pelo Telegram na #238; AD-76 e AD-77 em [ARCHITECTURE_DECISIONS](../context/ARCHITECTURE_DECISIONS.md)). O CI
externo continua sendo a fonte de verdade e roda sem o D.A.N.T.E., online ou não: aqui só existe o que é preciso para
**observá-lo**. Diagnóstico é a #239; correção supervisionada, a #240.

## Onde vive

| Camada | O quê |
| --- | --- |
| `Dante.Application/IntegracaoDeCi/Contratos` | identidade (`ConexaoDeCi`, `RepositorioDeCi`, `ConsultaDeCi`, `IdentidadeDaExecucaoDeCi`, `IdentidadeDoJobDeCi`), estados, DTOs (`ExecucaoDeCiDto`, `JobDeCiDto`, `EtapaDeJobDeCiDto`, `CheckAgregadoDeCiDto`, `EvidenciaDeCiDto`, `FalhaDeCiDto`, `LogDeJobDeCiDto`), `TrechoDeLogDeCi`, `PoliticaDeConsultaDeCi`, `FalhaNaConsultaDeCiException` |
| `Dante.Application/IntegracaoDeCi/Contratos` (#238) | `PullRequestDeCiDto`, `EstadoDoPullRequestDeCi`, `PedidoDeCiDoPullRequestDto`, `SituacaoDoCiDoPullRequestDto`, `OrigemDoRepositorioDeCi`, `ProblemaDoRepositorioDeCi` |
| `Dante.Application/IntegracaoDeCi/Portas` | `IProvedorDeCi` (somente leitura), `IRepositoriosDeCi` (identidade de CI de um alias, #238) |
| `Dante.Application/IntegracaoDeCi/CasosDeUso` | regras puras: `TransicoesDaExecucaoDeCi`, `ConsolidacaoDoCheckDeCi`, `MatrizDeFalhasDeCi`, `SanitizacaoDeTextoDeCi` |
| `Dante.Application/IntegracaoDeCi` (#238) | `AcompanhamentoDeCiAppService`: andamento do CI de um PR, sob demanda |
| `Dante.Infrastructure/IntegracaoDeCi` | `ProvedorDeCiGitHubActions` (porta), `ConexoesDeCiAutorizadas` (conexões do host), `ClienteDaApiDoGitHub` (só GET, política de repetição), `MapeamentoDoGitHubActions` (status → estados neutros), `RepositoriosDeCiConfigurados` (cadastro + conexão de um alias, #238) |
| `Dante.Worker/Telegram/TelegramCi` (#238) | `/ci` e a pergunta natural: gramática fechada, chamada do caso de uso e texto da resposta |

Nada é persistido nesta fase: não há entidade de Domain, tabela nem migration. Nenhum tipo da feature menciona
provedor, interface ou caminho do host (`IntegracaoDeCiContratosTests` recusa esse vocabulário).

## Identidade e isolamento

Toda leitura nasce de um **alias cadastrado** (AD-07) e de uma **conexão** (`ConexaoDeCi`: id + referência
`secret://host/NOME` ao binding do host, AD-10). Não há acesso implícito: repositório fora do cadastro não tem
`RepositorioDeCi`, e repositórios vinculados (AD-58) não herdam a conexão do principal.

| Campo | Regra |
| --- | --- |
| alias | formato do cadastro (`@nome`) |
| nome completo | `dono/repositorio`; comparado sem diferenciar maiúsculas, como o provedor |
| ID no provedor | numérico e imutável; nulo só na consulta; obrigatório em toda execução devolvida |
| conexão | a mesma da consulta; outra conexão é outro repositório |
| head SHA | completo (40 ou 64 hex), normalizado em minúsculas; abreviado é recusado |
| PR | o provedor é consultado pelo SHA; com número de PR na consulta, a execução também tem de estar associada a esse PR pelo provedor (#238) |
| workflow, execução, tentativa, job | IDs positivos; reexecução é **outra tentativa**, nunca continuação |
| URLs | https absoluta, sem credencial; só a URL oficial da execução/job, nunca a da API |

`ConsolidacaoDoCheckDeCi` aplica as regras sobre a resposta do provedor:

1. execução de outro repositório, conexão ou ID → a resposta inteira é recusada (`Inconsistente`), nunca filtrada em
   silêncio;
2. execução de outro SHA é descartada: um run antigo nunca é diagnosticado como sendo do head atual;
3. com número de PR na consulta (#238), só conta a execução que o provedor associa a esse PR (ver
   [associação ao PR](#associação-ao-pr)); sem número, vale só o SHA;
4. de cada execução vale a maior tentativa; de cada workflow e gatilho, a execução mais recente.

## Estados

`EstadoDaExecucao` vale para tentativa, job e etapa. `EstadoDoCheckDeCi` é o agregado do head SHA, separado deles.

### Transições de uma tentativa

```text
NaFila ──→ AguardandoRunner ──→ EmAndamento ──→ Sucesso | Falha | Cancelada | Expirada
  │               │                               ↑
  └───────────────┴──→ Ignorada                   └── também direto da fila ou da espera por runner
```

* só avançam; o polling amostra, então saltos para frente valem;
* observação que regride (cache, réplica atrasada) é ignorada: `Avancar(atual, observado)` mantém o atual;
* final é imutável dentro da tentativa; job já iniciado não vira `Ignorada`;
* finais e ordem são definidos **estado a estado**, nunca pela ordem numérica do enum.

### Estado desconhecido

Um valor que não é estado declarado (`(EstadoDaExecucao)999`, um enum futuro, um cast) nunca vira resultado:

1. **fronteira**: `ExecucaoDeCiDto`, `JobDeCiDto` e `EtapaDeJobDeCiDto` recusam o valor no construtor/inicializador e
   em `with` (`ArgumentOutOfRangeException`); o mesmo vale para tipo de evidência, disponibilidade de log e categoria
   de falha;
2. **matriz**: `TransicoesDaExecucaoDeCi` recusa o valor em `Final`, `Permitida` e `Avancar`;
3. **check**: `Sucesso` só quando todas as execuções relevantes estão explicitamente em `Sucesso`; motivo de falha
   da consulta desconhecido vira `Indisponivel`.

O adapter (#237) mapeia cada status/conclusão do provedor para um estado declarado; status que ele não conhece não é
aproximado: a consulta falha como `Inconsistente` e o check fica `Indisponivel`.

### Check agregado

| Situação | Estado |
| --- | --- |
| nenhuma execução do SHA e nenhum workflow no repositório | `SemWorkflowConfigurado` |
| nenhuma execução relevante do SHA (workflow existe, não disparou, ou tudo `Ignorada`) | `SemExecucao` |
| alguma execução não final | `EmAndamento` > `AguardandoRunner` > `NaFila` |
| todas finais | `Falha` > `Expirado` > `Cancelado` > `Sucesso` (só com todas explicitamente em `Sucesso`) |
| resultado incompleto (paginação no limite) sem falha observada | `Indisponivel` — nunca `Sucesso` |
| consulta falhou: sem credencial, 401, 403, 404 | `SemPermissao` |
| consulta falhou: limite de taxa, indisponível, inconsistente | `Indisponivel` |

`SemPermissao` e `Indisponivel` dizem que o D.A.N.T.E. não sabe o resultado, nunca que o CI falhou.

## Matriz de falhas

Uma categoria só é aceita com ao menos uma evidência do tipo exigido (`MatrizDeFalhasDeCi`). Sem evidência, a resposta
é `Indeterminada`.

| Categoria | Evidência aceita | Não basta |
| --- | --- | --- |
| `ErroNoCodigoOuTeste` | `EtapaComFalha` (job + etapa), `TrechoDeLog` | workflow vermelho |
| `RunnerIndisponivel` | `TempoSemRunner` (fila além de `EsperaSemRunnerAteSinalizar`), `AnotacaoDoProvedor` | job cancelado |
| `LimiteDeMinutosOuCobranca` | `AnotacaoDoProvedor` (mensagem explícita de cota/cobrança) | vermelho, fila longa, log ausente |
| `LogsInacessiveis` | `RespostaDoProvedor` (expirado, negado, ausente) | — |
| `Indeterminada` | nenhuma | — |

Também: só se classifica execução `Falha`, `Cancelada` ou `Expirada` (ou `AguardandoRunner` para runner
indisponível), e toda evidência que aponta um job tem de ser um job da própria tentativa.

Log é separado do estado: `LogDeJobDeCiDto` traz a disponibilidade (`Disponivel`, `AindaEmAndamento`, `Expirado`,
`NaoEncontrado`, `SemPermissao`, `Indisponivel`) e o trecho só quando disponível. Log ausente nunca muda nem esconde o
estado do job.

## Política de API

`PoliticaDeConsultaDeCi.Padrao` (validada por `Validada()`), obedecida por todo adapter:

| Item | Padrão | Regra |
| --- | --- | --- |
| tempo limite por requisição | 15 s | até 60 s |
| tentativas por requisição | 3 | só GET idempotente; repete timeout, 5xx e limite de taxa com backoff exponencial e jitter; nunca credencial negada, sem permissão ou recurso ausente (o adapter distingue o 403 de limite de taxa do 403 de permissão) |
| espera máxima por limite de taxa | 60 s | acima disso, `LimiteDeTaxa` com `RepetirApos`, sem prender a chamada |
| paginação | 100 itens × 5 páginas | no limite, `Completo = false` |
| cache | finais sem expiração; não finais 20 s | estado final de uma tentativa é imutável; cache menor que o intervalo de polling |
| polling | 30 s → dobra até 10 min | observa, nunca dirige o CI; webhook fica para depois |
| log | 64 KiB do final | entre 1 KiB e 1 MiB |
| sem runner até sinalizar | 15 min | base da evidência `TempoSemRunner` |

Idempotência: a porta não tem efeito colateral; repetir uma consulta devolve o mesmo fato ou um mais novo, e a
consolidação é determinística para a mesma resposta.

## Segurança

* **Menor privilégio e credencial por conexão.** O valor só é resolvido pelo adapter no momento da chamada, a partir
  do binding do host; nunca vai à Application, ao ledger, ao log do Worker nem ao Telegram. Nada de token pessoal
  global para todos os projetos.
* **Somente leitura.** `IProvedorDeCi` só consulta. Reexecutar, cancelar, comentar, fazer push ou merge são efeitos
  colaterais: entram, quando existirem (#240), como ação protegida do catálogo com aprovação vinculada a hash (AD-72).
* **Logs e comentários são dados não confiáveis.** `SanitizacaoDeTextoDeCi.Sanitizar` remove sequências de terminal
  (CSI/OSC), controles, caracteres de largura zero e de direção bidirecional; redige a linha inteira com credencial
  aparente (`ProtecaoDeSegredos`, fail-closed no timeout); mantém só o final, em linhas inteiras, até o limite.
  `TrechoDeLogDeCi.ParaContextoDeAgente` envolve o texto num envelope que declara o conteúdo como dado e não como
  instrução; o texto não consegue fechar o envelope (`<<<`/`>>>` são neutralizados) nem injetar atributos pela origem.
* **Mensagens de falha** da consulta não trazem credencial, cabeçalho, URL de API nem corpo de resposta.

## Provedor GitHub Actions (#237)

`ProvedorDeCiGitHubActions` é o adapter registrado da porta. Ele consulta a API REST (`api.github.com`, versão
`2022-11-28`) só com GET e devolve só tipos neutros. A execução no GitHub-hosted ou no self-hosted produz a mesma
resposta: runner, rótulos e máquina não fazem parte do contrato.

### Configuração

Uma conexão por credencial, no ambiente do Worker (ou em `appsettings`), nunca pelo Telegram:

```bash
IntegracaoDeCi__Conexoes__pessoal__Credencial=secret://host/DANTE_CI_GITHUB_TOKEN
IntegracaoDeCi__Conexoes__pessoal__Repositorios=@dante,@fitnessapp
DANTE_CI_GITHUB_TOKEN=<fine-grained token só com os repositórios e permissões abaixo>
```

| Item | Regra |
| --- | --- |
| id da conexão | o mesmo de `ConexaoDeCi.Id` (`^[a-z][a-z0-9_-]{0,39}$`) |
| `Credencial` | referência `secret://host/NOME` (AD-10); o valor fica só na variável `NOME` do host |
| `Repositorios` | aliases do cadastro que a conexão pode consultar, separados por vírgula; ao menos um |

Credencial com o valor em vez da referência, conexão sem repositório, alias fora do formato do cadastro ou id
repetido são recusados ao compor o provedor. Sem a seção, o provedor existe e toda chamada responde `NaoConfigurada`.

### Antes de qualquer requisição

Toda chamada confere, nesta ordem e sem rede:

1. a conexão pedida está configurada (`NaoConfigurada`) e é exatamente a configurada, com a mesma referência
   (`NaoAutorizada`): uma referência trocada nunca faz o adapter ler outra variável do host;
2. o alias está liberado nessa conexão (`NaoAutorizada`);
3. o alias está no cadastro (AD-07) com o mesmo `dono/repo` consultado (`NaoAutorizada`);
4. a variável do host existe e não está vazia (`NaoConfigurada`).

Só então a credencial é lida e enviada no cabeçalho `Authorization` da API. Ela nunca vai para a URL nem para o destino
de redirect, e não aparece em mensagem de falha.

### Consultas

| Porta | Chamadas |
| --- | --- |
| `ConsultarAsync` | `actions/runs?head_sha=` (paginado); para cada execução, `runs/{id}/attempts/{tentativa}/jobs` (paginado); sem execução, `actions/workflows?per_page=1` para distinguir repositório sem workflow |
| `ObterExecucaoAsync` | `runs/{id}/attempts/{tentativa}` e os jobs da tentativa |
| `ObterLogDoJobAsync` | `actions/jobs/{id}`; se terminou, `actions/jobs/{id}/logs` e o redirect assinado, seguido **sem credencial** e só para domínio de log do GitHub |
| `ObterPullRequestAsync` (#238) | `pulls/{numero}`, sem cache: head SHA atual, estado, rascunho, fork, título e URL do PR |

Conferências sobre a resposta; o que não confere é recusado inteiro como `Inconsistente`, nunca filtrado:

* execução de outro repositório (nome ou ID diferente do consultado);
* em `ObterExecucaoAsync`, outra execução, tentativa, workflow ou SHA que a identidade pedida;
* job de outra execução, tentativa ou SHA que a tentativa em que ele aparece (vale também para o log);
* URL oficial que não seja `https://github.com/<dono>/<repo>/...` (URL da API, de outro repositório, http ou com
  credencial);
* campo obrigatório ausente, tipo errado, JSON inválido ou resposta acima de 8 MiB;
* 3xx em consulta JSON (repositório renomeado ou transferido: o cadastro precisa ser atualizado);
* no PR (#238): outro número, repositório de destino (`base.repo`) com outro nome ou ID, head SHA abreviado, `state`
  fora de `open`/`closed`, ou URL que não seja `https://github.com/<dono>/<repo>/pull/<numero>`.

Execução de outro SHA que a API devolva fica no resultado e é descartada pela `ConsolidacaoDoCheckDeCi`. A listagem só
traz a última tentativa de cada execução; tentativas anteriores são lidas por `ObterExecucaoAsync`, cada uma com
identidade e jobs próprios. Nomes de workflow, job, etapa e evento passam pela `SanitizacaoDeTextoDeCi`, ficam numa
linha só e com até 200 caracteres.

### Mapeamento de estados

| `status` | `conclusion` | Execução e etapa | Job |
| --- | --- | --- | --- |
| `queued` | — | `NaFila` | `AguardandoRunner` (o job espera um runner) |
| `requested`, `pending`, `waiting` | — | `NaFila` | `NaFila` |
| `in_progress` | — | `EmAndamento` | `EmAndamento` |
| `completed` | `success` | `Sucesso` | `Sucesso` |
| `completed` | `failure`, `startup_failure` | `Falha` | `Falha` |
| `completed` | `cancelled` | `Cancelada` | `Cancelada` |
| `completed` | `timed_out`, `stale` | `Expirada` | `Expirada` |
| `completed` | `skipped`, `neutral` | `Ignorada` | `Ignorada` |
| `completed` | `action_required` | `NaFila` (aguarda aprovação para rodar, ex.: PR de fork) | `NaFila` |
| outro, ou `completed` sem conclusão | | consulta falha como `Inconsistente` | |

`neutral` não afirma sucesso nem falha: não conta para o check. Nenhuma conclusão vira limite de minutos ou cobrança;
essa categoria só nasce de anotação explícita do provedor (#239).

### Falhas e repetição

| Resposta | Consulta (`ConsultarAsync`, `ObterExecucaoAsync`) | Log (`ObterLogDoJobAsync`) | Repete? |
| --- | --- | --- | --- |
| 401 | `NaoAutorizada` | `SemPermissao` | não |
| 403 sem sinal de limite de taxa | `SemPermissao` | `SemPermissao` | não |
| 404 | `NaoEncontrada` | `NaoEncontrado` | não |
| 410 | `Indisponivel` | `Expirado` | não |
| 429, ou 403 com `x-ratelimit-remaining: 0` ou `Retry-After` | `LimiteDeTaxa` com `RepetirApos` | `Indisponivel` | sim, se a espera couber em `EsperaMaximaPorLimiteDeTaxa` |
| 5xx, timeout da requisição, falha de rede | `Indisponivel` | `Indisponivel` | sim, backoff 1 s, 2 s... com jitter |
| outro 4xx | `Indisponivel` | `Indisponivel` | não |
| cancelamento de quem chamou | `OperationCanceledException`, nunca falha do CI | idem | não |

A espera do limite de taxa vem de `Retry-After`, de `x-ratelimit-reset` ou, sem cabeçalho, de um minuto. Se passar do
máximo da política, a chamada falha na hora com `RepetirApos`, sem ficar presa. A repetição é da mesma requisição GET:
a resposta que vale é a última, sem somar resultados. Log inacessível nunca vira exceção nem muda o estado do job, que
vem da execução. Job ainda não terminado responde `AindaEmAndamento`, sem baixar nada.

### Logs

* só o final é baixado, numa janela de 4× o limite pedido; o começo descartado marca o trecho como truncado e o
  primeiro segmento, partido no meio, nunca aparece, mesmo quando é o único (`Sanitizar(..., inicioCortado: true)`);
* log acima de 64 MiB não é lido: `Indisponivel`;
* o redirect só é seguido, sem `Authorization`, para os domínios que a documentação do GitHub Actions lista para
  download de logs e operação essencial: subdomínios de `actions.githubusercontent.com` (inclusive
  `results-receiver.actions.githubusercontent.com`) e de `blob.core.windows.net`. Exige `https` na porta 443, sem
  userinfo e sem IP literal; outro destino (host externo, loopback, rede interna, sufixo falso) responde `Indisponivel`
  sem receber requisição. Se o GitHub mudar esses domínios, a lista em `ProvedorDeCiGitHubActions` precisa acompanhar;
  até lá, o log fica `Indisponivel`, nunca o job;
* o texto passa pela `SanitizacaoDeTextoDeCi` (sequências de terminal, invisíveis, linhas com credencial aparente) e,
  para um agente, só pelo envelope de dado não confiável.

### Cache

Mesma regra da política: a tentativa final (com a lista de jobs completa) não muda e não expira; a não final vale por
`ValidadeDoCacheNaoFinal` (20 s) em `ObterExecucaoAsync`. Na listagem só se reaproveita a tentativa final. A chave é a
identidade inteira, com alias e conexão: uma conexão nunca lê o que outra trouxe, e a autorização é refeita a cada
chamada mesmo com o cache cheio. O cache fica em memória, tem até 1024 entradas e é esvaziado quando enche.

### Limites conhecidos

* só `api.github.com`; GitHub Enterprise Server fica para quando houver um cliente com ele;
* check runs e status legados de outros sistemas (apps de terceiros) não entram: o resultado é o dos workflows do
  Actions;
* paginação no limite da política (100 × 5) deixa o resultado `Completo = false`, e o check nunca vira `Sucesso`;
  em `ObterExecucaoAsync`, jobs além do limite fazem a consulta falhar como `Indisponivel` em vez de devolver parte;
* a conexão não é persistida nem gerida pelo Telegram: é configuração do host;
* não há polling: a porta só é chamada sob demanda pelo andamento do CI de um PR (#238, abaixo).

## Permissões mínimas no GitHub (#237)

Para o adapter somente leitura, por repositório selecionado (fine-grained token restrito aos repositórios cadastrados
ou, quando houver comercialização, instalação de GitHub App por conta):

| Permissão | Nível | Para quê |
| --- | --- | --- |
| Metadata | read | obrigatória; ID e nome do repositório |
| Actions | read | workflows, runs, tentativas, jobs e logs |
| Checks | read | check runs e suites do head SHA |
| Pull requests | read | head SHA atual de um PR |
| Commit statuses | read | opcional: só para repositórios que publicam status legados |

Nada de escrita, nem `Contents`, `Workflows` ou `Administration`. Token clássico com escopo `repo` não é aceito como
caminho padrão. O adapter usa hoje Metadata, Actions e, desde a #238, Pull requests (só `GET pulls/{numero}`); Checks
continua prevista e sem uso. Escrita (`Actions: write` para reexecutar, `Pull requests: write` para comentar) só
existirá com a ação protegida correspondente.

## Andamento do CI de um PR (#238)

O usuário pergunta pelo Telegram e recebe o estado do CI do **commit atual** do PR, sob demanda (AD-77):

```text
/ci 12                                   repositório da sessão ativa; sem ela, o repositório ativo (/use)
/ci @dante 12                            repositório explícito
como está o CI do PR 12 do @dante?       a mesma consulta, em linguagem natural
```

### Interface

O D.A.N.T.E. não tem NLU geral: a conversa natural com intenção própria é a gramática fechada e determinística do Brain
(`ResolvedorDeIntencaoDoBrain`). O andamento do CI segue o mesmo modelo, no adapter `TelegramCi`:

* `/ci [@alias] <número>` (aceita `#12`); qualquer outro argumento responde o uso;
* a pergunta natural precisa da palavra CI (ou checks/pipeline) e do número do PR: "como está/anda", "qual o status
  do", "status do", "e o CI do PR 12?", "o CI do PR 12 já passou?", com `do|no|de|em @alias` opcional. Acentos,
  maiúsculas e espaços extras não importam. Frase que não casa (sem número, com outro verbo, com dois aliases) segue
  para a conversa com o agente, como antes;
* o reconhecimento vem antes do Brain e da sessão: um pedido de CI nunca vira captura do Brain nem turno do agente.

### Resolução do repositório

1. alias explícito no pedido;
2. o repositório principal da sessão ativa, quando ela trabalha num repositório (é para onde vão as mensagens comuns);
3. o repositório ativo do usuário (`/use`);
4. nenhum: a resposta pede o alias. Nunca há inferência pelo texto além do alias dito.

O alias tem de estar no cadastro (AD-07); repositório ativo que saiu do cadastro pede nova seleção. A identidade de
CI (`IRepositoriosDeCi`) é o `dono/repo` do cadastro e a **única** conexão que libera o alias: sem `dono/repo`, sem
conexão ou com duas conexões, a resposta é "não configurado", sem rede. Repositório vinculado (AD-58) não herda a
conexão do principal: só é consultado se uma conexão o liberar pelo próprio alias.

### Correlação

1. o PR é lido agora (`ObterPullRequestAsync`, nunca em cache) e conferido: mesmo número e mesmo repositório de
   destino, por nome e ID; PR de outro repositório é `Inconsistente` e nada é exibido. PR de fork é aceito e
   informado: o CI que vale é o do repositório consultado;
2. a consulta das execuções usa o head SHA lido e o ID confirmado do repositório;
3. a `ConsolidacaoDoCheckDeCi` descarta execuções de outro SHA, de outro PR, tentativas anteriores e execuções
   substituídas. Um commit novo no PR **não herda** o sucesso do anterior: sem execução do commit novo, o estado é
   "ainda não há execução de CI para este commit".

#### Associação ao PR

O mesmo SHA pode ser o head de dois PRs, e o commit pode ter execuções de outros gatilhos: o SHA sozinho não prova que
uma execução é do PR consultado. Por isso a consulta leva o número do PR, e cada execução do SHA é classificada pela
lista de PRs que o provedor associa a ela (`NumerosDePr`, do campo `pull_requests` da execução no GitHub). A ordem é
fixa: primeiro a tentativa vigente de cada execução (a maior), depois a associação **dessa** tentativa, e só então a
execução mais recente de cada workflow e gatilho. Uma tentativa substituída nunca empresta a associação nem o estado à
vigente (o provedor não garante que a associação seja a mesma entre tentativas), e a execução de outro PR no mesmo
workflow não substitui a do PR consultado:

| Associação da execução | Efeito |
| --- | --- |
| contém o PR consultado (sozinho ou com outros) | conta para o check |
| só outros PRs | descartada (`Descartadas`): a falha do PR vizinho não entra no PR consultado |
| nenhuma (lista vazia) | não conta e é informada (`SemAssociacaoAoPr`): "N execuções deste commit não aparecem ligadas a este PR no provedor e não foram consideradas" |

Se nenhuma execução associada resta e há execução do SHA sem associação, o check é `Indisponivel` ("não consigo
afirmar o resultado... Isso não quer dizer que o CI falhou"), nunca `Sucesso`, `Falha` ou `SemExecucao`: o D.A.N.T.E.
não sabe se elas são do PR. Se não resta nada associado e não há execução sem associação, vale a regra comum
(`SemExecucao`/`SemWorkflowConfigurado`).

A documentação da API REST não descreve quando `pull_requests` vem preenchido ou vazio; só o esquema da resposta é
documentado. Por isso a regra não presume associação pelo SHA. Em quais cenários reais a lista vem vazia (por exemplo,
PR de fork ou PR já fechado) fica para a validação com o CI real (#242); até lá, nesses casos a resposta é
`Indisponivel` com a explicação acima, não um resultado inventado.

### Resposta

Curta, em PT-BR, com título do PR (sanitizado), estado (fechado, mesclado, rascunho, de fork), SHA curto do commit
atual, o resumo do check e uma linha por workflow com o link oficial da execução, a tentativa quando é reexecução, os
jobs que falharam com a etapa, e o que está pendente (em andamento, na fila, aguardando runner). No fim, o link do PR.
Até 10 workflows e 5 jobs por lista; o restante é contado.

* `SemPermissao`, `Indisponivel` (inclusive resultado incompleto e limite de taxa, com o prazo para tentar de novo) e
  falhas ao ler o PR dizem que o D.A.N.T.E. não sabe o resultado: nunca "falhou";
* sucesso vem com o aviso de que proteção da branch, checks obrigatórios e revisões **não** foram avaliados: o
  D.A.N.T.E. não diz que o PR pode ser mesclado;
* sem endpoint de API, nome de ferramenta interna, SHA completo, stack trace ou trecho de log; falha inesperada só
  registra o tipo da exceção e responde "indisponível agora";
* a consulta inteira tem até 45 s; passado isso, a resposta é "não consegui consultar a tempo".

### Limites conhecidos (#238)

* sem acompanhamento periódico nem aviso quando o CI termina: não há política para isso, e a consulta é sempre
  pedida pelo usuário (o cache de tentativas finais da #237 evita releituras);
* a consulta roda no fluxo das mensagens do Telegram: enquanto ela dura (até 45 s), as mensagens seguintes esperam;
* não lê check runs de apps de terceiros, status legados, regras de proteção da branch, checks obrigatórios nem
  revisões;
* PR de fork que aguarda aprovação de um mantenedor aparece como "na fila" (`action_required`), com uma observação;
* a gramática natural é fechada: perguntas fora dela vão para o agente da conversa, que não tem acesso à porta de CI.
