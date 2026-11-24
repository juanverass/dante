# Integração de CI — contratos

Guia dos contratos da integração de CI do D.A.N.T.E. (Epic #234, Issue #236, AD-76 em [ARCHITECTURE_DECISIONS](../context/ARCHITECTURE_DECISIONS.md)).
O CI externo continua sendo a fonte de verdade e roda sem o D.A.N.T.E.: aqui só existem os contratos para
**observá-lo**. O primeiro provedor (GitHub Actions) é a #237; correlação e consulta pelo Telegram, a #238; diagnóstico,
a #239; correção supervisionada, a #240.

## Onde vive

| Camada | O quê |
| --- | --- |
| `Dante.Application/IntegracaoDeCi/Contratos` | identidade (`ConexaoDeCi`, `RepositorioDeCi`, `ConsultaDeCi`, `IdentidadeDaExecucaoDeCi`, `IdentidadeDoJobDeCi`), estados, DTOs (`ExecucaoDeCiDto`, `JobDeCiDto`, `EtapaDeJobDeCiDto`, `CheckAgregadoDeCiDto`, `EvidenciaDeCiDto`, `FalhaDeCiDto`, `LogDeJobDeCiDto`), `TrechoDeLogDeCi`, `PoliticaDeConsultaDeCi`, `FalhaNaConsultaDeCiException` |
| `Dante.Application/IntegracaoDeCi/Portas` | `IProvedorDeCi` (somente leitura) |
| `Dante.Application/IntegracaoDeCi/CasosDeUso` | regras puras: `TransicoesDaExecucaoDeCi`, `ConsolidacaoDoCheckDeCi`, `MatrizDeFalhasDeCi`, `SanitizacaoDeTextoDeCi` |
| `Dante.Infrastructure/IntegracaoDeCi` | `ProvedorDeCiNaoConfigurado` (fail-closed) até a #237 |

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
* final é imutável dentro da tentativa; job já iniciado não vira `Ignorada`.

### Check agregado

| Situação | Estado |
| --- | --- |
| nenhuma execução do SHA e nenhum workflow no repositório | `SemWorkflowConfigurado` |
| nenhuma execução relevante do SHA (workflow existe, não disparou, ou tudo `Ignorada`) | `SemExecucao` |
| alguma execução não final | `EmAndamento` > `AguardandoRunner` > `NaFila` |
| todas finais | `Falha` > `Expirado` > `Cancelado` > `Sucesso` |
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
caminho padrão. Escrita (`Actions: write` para reexecutar, `Pull requests: write` para comentar) só existirá com a
ação protegida correspondente.
