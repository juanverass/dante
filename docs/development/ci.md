# Integração de CI — contratos e provedor GitHub Actions

Guia da integração de CI do D.A.N.T.E. (Epic #234; contratos na #236, provedor GitHub Actions na #237; AD-76 em
[ARCHITECTURE_DECISIONS](../context/ARCHITECTURE_DECISIONS.md)). O CI externo continua sendo a fonte de verdade e roda
sem o D.A.N.T.E., online ou não: aqui só existe o que é preciso para **observá-lo**. Correlação e consulta pelo
Telegram são a #238; diagnóstico, a #239; correção supervisionada, a #240.

## Onde vive

| Camada | O quê |
| --- | --- |
| `Dante.Application/IntegracaoDeCi/Contratos` | identidade (`ConexaoDeCi`, `RepositorioDeCi`, `ConsultaDeCi`, `IdentidadeDaExecucaoDeCi`, `IdentidadeDoJobDeCi`), estados, DTOs (`ExecucaoDeCiDto`, `JobDeCiDto`, `EtapaDeJobDeCiDto`, `CheckAgregadoDeCiDto`, `EvidenciaDeCiDto`, `FalhaDeCiDto`, `LogDeJobDeCiDto`), `TrechoDeLogDeCi`, `PoliticaDeConsultaDeCi`, `FalhaNaConsultaDeCiException` |
| `Dante.Application/IntegracaoDeCi/Portas` | `IProvedorDeCi` (somente leitura) |
| `Dante.Application/IntegracaoDeCi/CasosDeUso` | regras puras: `TransicoesDaExecucaoDeCi`, `ConsolidacaoDoCheckDeCi`, `MatrizDeFalhasDeCi`, `SanitizacaoDeTextoDeCi` |
| `Dante.Infrastructure/IntegracaoDeCi` | `ProvedorDeCiGitHubActions` (porta), `ConexoesDeCiAutorizadas` (conexões do host), `ClienteDaApiDoGitHub` (só GET, política de repetição), `MapeamentoDoGitHubActions` (status → estados neutros) |

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
| PR | contexto de exibição; a correlação é sempre pelo SHA |
| workflow, execução, tentativa, job | IDs positivos; reexecução é **outra tentativa**, nunca continuação |
| URLs | https absoluta, sem credencial; só a URL oficial da execução/job, nunca a da API |

`ConsolidacaoDoCheckDeCi` aplica as regras sobre a resposta do provedor:

1. execução de outro repositório, conexão ou ID → a resposta inteira é recusada (`Inconsistente`), nunca filtrada em
   silêncio;
2. execução de outro SHA é descartada: um run antigo nunca é diagnosticado como sendo do head atual;
3. de cada execução vale a maior tentativa; de cada workflow e gatilho, a execução mais recente.

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

Conferências sobre a resposta; o que não confere é recusado inteiro como `Inconsistente`, nunca filtrado:

* execução de outro repositório (nome ou ID diferente do consultado);
* em `ObterExecucaoAsync`, outra execução, tentativa, workflow ou SHA que a identidade pedida;
* job de outra execução, tentativa ou SHA que a tentativa em que ele aparece (vale também para o log);
* URL oficial que não seja `https://github.com/<dono>/<repo>/...` (URL da API, de outro repositório, http ou com
  credencial);
* campo obrigatório ausente, tipo errado, JSON inválido ou resposta acima de 8 MiB;
* 3xx em consulta JSON (repositório renomeado ou transferido: o cadastro precisa ser atualizado).

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
* não há polling, comando nem interface: quem chama a porta é a #238.

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
caminho padrão. O adapter da #237 usa hoje Metadata e Actions; Checks e Pull requests ficam previstas para a correlação
da #238. Escrita (`Actions: write` para reexecutar, `Pull requests: write` para comentar) só existirá com a
ação protegida correspondente.
