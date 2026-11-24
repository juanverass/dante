# CI em múltiplos repositórios — inventário, padrão e rollout

Estratégia de CI reaproveitável para os repositórios do usuário (Issue #241, Epic #234). É documentação de adoção:
nada aqui altera workflow, runner, secret ou configuração de repositório. Cada mudança real acontece no repositório
de destino, com autorização para aquele repositório, seguindo o [rollout](#rollout-incremental-por-repositório).

O CI continua no GitHub Actions como fonte de verdade; o runner é só infraestrutura de execução; o D.A.N.T.E. apenas
observa o CI pela conexão somente leitura da #237 ([ci](ci.md), AD-76). Nada deste documento depende do D.A.N.T.E.
estar no ar, e nada aqui coloca credencial do D.A.N.T.E. no runner.

Exemplos testáveis em [`ci-multirepo-exemplos/`](ci-multirepo-exemplos/). Eles ficam fora de `.github/workflows/` e
por isso **não executam** neste repositório.

| Arquivo | O quê |
| --- | --- |
| [`ci-reutilizavel-dotnet.yml`](ci-multirepo-exemplos/ci-reutilizavel-dotnet.yml) | `workflow_call` .NET: build e testes; PostgreSQL por service container ou dependências por docker compose |
| [`ci-reutilizavel-node.yml`](ci-multirepo-exemplos/ci-reutilizavel-node.yml) | `workflow_call` Node/React: `.nvmrc`, `npm ci` com cache e scripts do `package.json` |
| [`ci-reutilizavel-playwright.yml`](ci-multirepo-exemplos/ci-reutilizavel-playwright.yml) | `workflow_call` Playwright na imagem oficial, com Node do projeto e relatórios |
| [`exemplo-ci-node-playwright.yml`](ci-multirepo-exemplos/exemplo-ci-node-playwright.yml) | chamador no formato do `fitnessapp_frontend`, com plano da execução e check agregado |
| [`exemplo-ci-dotnet.yml`](ci-multirepo-exemplos/exemplo-ci-dotnet.yml) | chamador .NET: suíte rápida no PR, completa com PostgreSQL nos marcos |
| [`verificar-runs-on.py`](ci-multirepo-exemplos/verificar-runs-on.py) | verificação estática de `runs-on` em todos os jobs, inclusive de workflows reutilizáveis |

## Inventário

Inspeção somente leitura pela API do GitHub (`gh api`, `gh run list`) em **24/11/2026**, no HEAD do branch padrão
(`main`) de cada repositório. Todos pertencem a uma **conta pessoal** (tipo `User`), não a uma organização.

| Repositório | Visibilidade | Commit inspecionado | Stack | Workflows |
| --- | --- | --- | --- | --- |
| `fitnessapp_frontend` | privado | `c076c73e` (21/10/2026) | React 19 + Vite + TypeScript, Vitest, Playwright 1.64.1; Node `24` (`.nvmrc`, `engines >=24 <25`, `engine-strict`) | `ci.yml` |
| `fitnessapp_backend` | privado | `b31206c8` (20/10/2026) | .NET 10 (`global.json` 10.0.100), EF Core + Npgsql, xUnit; testes sem banco | `ci.yml` |
| `dante` | público | `6a0a4bfd` (24/11/2026) | .NET 10, xUnit; PostgreSQL opcional nos testes (ignorados sem banco) | `dante-ci.yml` e um workflow de smoke do runner self-hosted |
| `cognexa-backend` | público | `7e3ed7c3` (07/10/2026) | .NET 10 (`global.json` 10.0.100), xUnit com fixture PostgreSQL | `ci.yml` |
| `cognexa-frontend` | público | `37f26789` (07/10/2026) | só `README.md`: sem stack ainda | nenhum (`.github/workflows` inexistente) |

Nenhum repositório tem `docker-compose`/`compose.yaml` versionado. O `fitnessapp_frontend` roda o Playwright dentro da
imagem oficial (`scripts/playwright-docker.mjs`, Docker local ou `container:` no CI).

### O que cada workflow faz hoje

| Repositório · workflow | Gatilhos | Jobs e `runs-on` | Práticas presentes | Lacunas |
| --- | --- | --- | --- | --- |
| `fitnessapp_frontend` · `CI` | `pull_request`, `push` main, `schedule` diário, `workflow_dispatch` | `verificacao`, `e2e-telas`, `mudancas`, `e2e-ds`: todos `ubuntu-latest`; os dois E2E em `container: mcr.microsoft.com/playwright:v1.64.1-noble` | `permissions: contents: read`; `concurrency` por ref e evento com cancelamento; cache npm; artefatos com `retention-days: 14`; suíte pesada (~1 h) só quando o DS muda | sem `timeout-minutes`; job condicional sem agregador; E2E sem Node do projeto (ver abaixo) |
| `fitnessapp_backend` · `CI` | `pull_request` e `push` para main | `build-e-testes`, `migrations-em-dia`: `ubuntu-latest` | `global-json-file`; cache NuGet; resultados TRX como artefato; verificação de migration pendente | sem `permissions` no workflow; sem `concurrency`; sem `timeout-minutes`; artefato em toda execução sem `retention-days` |
| `dante` · `DANTE CI` | só `workflow_dispatch` | `build-and-test`: `[self-hosted, Linux, X64]` | `permissions: contents: read`; `concurrency`; `timeout-minutes: 30` | não roda em PR (a validação continua local, como diz o [contrato](agent-contract.md#build-e-testes)) |
| `dante` · smoke do runner self-hosted | só `workflow_dispatch` | um job: `[self-hosted, Linux, X64]` | `permissions: {}`; `timeout-minutes: 5` | — |
| `cognexa-backend` · `Backend CI` | `push` e `pull_request` (todos os branches) | `build-test`: `ubuntu-latest`, service container `postgres:17` com health check | testes com PostgreSQL real | sem `permissions`, `concurrency`, `timeout-minutes`; PR de branch interno roda duas vezes (push e PR) |

### Histórico recente (últimas 10 execuções)

| Repositório | Resultado | Motivo observado |
| --- | --- | --- |
| `fitnessapp_frontend` | 10 falhas, todas do `schedule` diário de 14 a 23/11, no mesmo SHA | os dois jobs Playwright falham em `npm ci` com `EBADENGINE`: a imagem traz Node 26 e o projeto exige `>=24 <25` ([fitnessapp_frontend#44](https://github.com/juanverass/fitnessapp_frontend/issues/44)); `verificacao` e `mudancas` passam. Os quatro jobs rodaram em runner hosted (`ubuntu-latest`) até 23/11 |
| `fitnessapp_backend` | 10 sucessos (push em main, 20/10) | — |
| `dante` | 1 sucesso (smoke) e 3 falhas do `DANTE CI` (31/10) | runner self-hosted do repositório; na falha inspecionada, 2 testes com falha de 1329 (1240 aprovados, 87 ignorados) |
| `cognexa-backend` | 9 sucessos, 1 falha (07/10) | erro de compilação `CS0246` num branch de feature |
| `cognexa-frontend` | nenhuma execução | sem workflow |

O endpoint de tempo de execução (`actions/runs/{id}/timing`) devolveu `0 ms` faturáveis para a execução hosted de
23/11 do `fitnessapp_frontend`: não serve como evidência de consumo ou de cota. O consumo de minutos se afere na
página de cobrança da conta (a confirmar no piloto #235).

### Configuração dos repositórios

| Item | `fitnessapp_frontend` | `fitnessapp_backend` | `dante` | `cognexa-backend` | `cognexa-frontend` |
| --- | --- | --- | --- | --- | --- |
| Proteção de branch / rulesets | indisponível: a API responde 403 "Upgrade to GitHub Pro or make this repository public" | idem | sem proteção; nenhum ruleset | sem proteção; nenhum ruleset | sem proteção; nenhum ruleset |
| Runners self-hosted do repositório | nenhum | nenhum | 1 runner self-hosted Linux registrado (`self-hosted,Linux,X64`) | nenhum | nenhum |
| Permissão padrão do `GITHUB_TOKEN` | read | read | read | read | read |
| Actions permitidas | todas; SHA pinning não exigido | idem | idem | idem | idem |
| Workflows de PR de fork | desligado (repositório privado) | desligado | aprovação só para quem contribui pela primeira vez | idem | idem |
| Retenção de artefatos e logs | 90 dias (máx. 400) | 90 dias (máx. 400); 103 artefatos guardados | 90 dias | 90 dias | 90 dias |
| Cache do Actions em uso | 1 entrada, ~59 MiB | vazio | vazio | vazio | vazio |

Consequências diretas:

* **Nos repositórios privados não existe check obrigatório hoje.** No plano atual a proteção de branch não está
  disponível para eles; o "check obrigatório" é convenção do processo (revisão humana antes do merge) até que haja
  plano ou organização que permita impor. O padrão abaixo já deixa um único check estável pronto para ser exigido.
* **O runner self-hosted existente está num repositório público.** A documentação do GitHub diz que self-hosted
  "should almost never be used for public repositories"
  ([secure use](https://docs.github.com/en/actions/reference/security/secure-use)). Hoje os dois workflows do `dante`
  são só `workflow_dispatch` (exigem permissão de escrita para disparar), mas um PR de fork pode propor um workflow
  novo com `pull_request` e `runs-on: self-hosted`, e a política atual só pede aprovação a quem contribui pela
  primeira vez. O isolamento desse runner não foi avaliado aqui e não está dado como mitigado: a decisão operacional
  (aprovação de todo colaborador externo ou desregistro) é humana e fica fora desta Issue; o ambiente isolado de
  runner é escopo da #235. Ver
  [riscos](#riscos-por-repositório) e [rollout](#rollout-incremental-por-repositório).

## Trade-offs por stack

| Stack | Escolha padrão | Alternativa | Trade-off e risco |
| --- | --- | --- | --- |
| .NET (build + unit) | `setup-dotnet` com `global-json-file`, cache de `~/.nuget/packages`, `--no-restore`/`--no-build` | SDK pré-instalado no runner | `setup-dotnet` deixa o repositório decidir a versão e funciona igual em hosted e self-hosted; SDK do host acopla repositórios à mesma versão. Em self-hosted persistente o diretório de pacotes já sobrevive entre jobs, e o cache do Actions passa a importar só com runner efêmero |
| .NET + PostgreSQL | **service container** com porta do host dinâmica (`5432/tcp`) e health check | docker compose no job | service container é declarativo, isolado por job e limpo pelo runner; exige Linux com Docker no self-hosted ([service containers](https://docs.github.com/en/actions/tutorials/use-containerized-services/use-docker-service-containers)). Compose espelha o ambiente local e aceita vários serviços, mas a limpeza é do workflow (`down --volumes` em `always()`) e o nome do projeto precisa ser único por execução para não colidir no mesmo host |
| .NET · migrations | `dotnet-ef` instalado em `--tool-path` do job | `dotnet tool install --global` (como hoje) | instalação global num runner persistente vaza versão entre execuções e repositórios |
| Node/React | `setup-node` com `node-version-file: .nvmrc` e `cache: npm`, `npm ci` | Node do host | o projeto define a versão; com `engine-strict` qualquer divergência quebra o `npm ci` (exatamente o caso da #44) |
| Playwright | imagem oficial na **mesma versão** do `@playwright/test`, `--ipc=host`; browsers vêm da imagem, sem cache de browsers | `npx playwright install --with-deps` com cache de `~/.cache/ms-playwright` | a imagem garante capturas determinísticas (o repositório já depende disso), mas traz o próprio Node: é preciso alinhar o Node do projeto dentro do container (#44). Sem imagem, o cache economiza download mas `--with-deps` precisa de privilégio de instalação no host, inaceitável num runner compartilhado |
| Suítes longas (DS ~1 h) | job condicional decidido por um job de plano **dentro** do workflow + agregador | filtro `paths:` no gatilho | `paths:` deixa o check pendente para sempre quando o workflow é pulado ([troubleshooting de required checks](https://docs.github.com/en/pull-requests/how-tos/merge-and-close-pull-requests/troubleshooting-required-status-checks)); job pulado por `if` reporta sucesso, o que exige o agregador para não aceitar pulo causado por falha a montante |

### Riscos por repositório

| Repositório | Riscos | Recomendação |
| --- | --- | --- |
| `fitnessapp_frontend` | E2E quebrados por Node (#44) antes de qualquer teste; `schedule` diário vermelho consumindo minutos hosted enquanto quebrado; container como root deixa arquivos de root no workspace de runner persistente; suíte DS de ~1 h ocupa o único runner | piloto da #235 depois da #44; considerar suspender o `schedule` até lá (decisão do dono do repositório); `timeout-minutes` em todos os jobs; runner com Docker; limpeza de workspace (ver [políticas](#políticas)) |
| `fitnessapp_backend` | artefato TRX em toda execução, com retenção de 90 dias, ocupando armazenamento de repositório privado; `dotnet tool install --global` em runner persistente; sem `permissions` explícitas | segundo repositório do rollout; artefato só na falha e `retention-days` curto; ferramenta local ao job |
| `dante` | **público com runner self-hosted registrado**; testes sensíveis ao ambiente (2 falhas no runner em 31/10) | não ligar `pull_request` no runner self-hosted; para CI de PR, preferir runner hosted padrão (gratuito em repositório público, [billing](https://docs.github.com/en/billing/concepts/product-billing/github-actions)); manter o runner só para dispatch do dono ou desregistrá-lo (decisão humana, fora desta Issue) e exigir aprovação para todo colaborador externo |
| `cognexa-backend` | público; roda duas vezes em PR de branch interno; sem `permissions`/`timeout` | continuar em hosted (gratuito em repositório público); só políticas (permissões, concorrência, timeout, agregador). Self-hosted não |
| `cognexa-frontend` | sem código | aplicar o template Node quando houver stack, em hosted |

## Arquitetura de runners para conta pessoal

Fatos da plataforma que limitam o desenho:

* runner de repositório é dedicado: "Repository-level runners are dedicated to a single repository"
  ([self-hosted runners](https://docs.github.com/en/actions/concepts/runners/self-hosted-runners)). Num repositório
  de usuário só o dono registra runners
  ([add runners](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/add-runners)). **Um
  runner de repositório nunca atende outro repositório**;
* compartilhar um pool entre repositórios exige runner de organização (ou empresa), com acesso controlado por
  runner groups ([manage access](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/manage-access));
  grupos além do padrão dependem do plano da organização;
* uma instância de runner executa um job por vez; runner efêmero (`--ephemeral`) é desregistrado depois de um job e
  a limpeza da máquina é automação nossa
  ([referência](https://docs.github.com/en/actions/reference/runners/self-hosted-runners));
* uso de self-hosted não consome minutos hosted; armazenamento de artefatos e cache continua com limites e cobrança
  ([billing](https://docs.github.com/en/billing/concepts/product-billing/github-actions)).

Opções:

| Opção | Como | Isolamento | Concorrência | Custo operacional | Quando |
| --- | --- | --- | --- | --- | --- |
| **A. Uma instância por repositório** (padrão) | cada repositório privado ganha a própria instância: diretório, usuário de sistema e serviço separados, registrada só naquele repositório, na VM de CI da #235 | por usuário de SO e diretório; a VM separa do D.A.N.T.E. | 1 job por instância; mais instâncias por repositório se a fila incomodar | registro e atualização por instância | conta pessoal, poucos repositórios privados |
| **B. Uma VM por repositório ou por grupo de confiança** | como A, mas com VM própria | por VM | idem | mais memória/disco | repositórios com dados ou dependências de confiança diferentes |
| **C. Organização GitHub com pool compartilhado** | runners de organização com runner groups restritos por repositório | por grupo + VM | pool compartilhado | migração de repositórios para organização | só por decisão humana explícita; **esta proposta não move repositórios** |
| **D. Hosted** | `ubuntu-latest` | da plataforma | da plataforma | cota de minutos em repositório privado | repositórios públicos; rollback de privado quando houver cota |

Recomendação: **A** para `fitnessapp_frontend` e `fitnessapp_backend`, em instâncias distintas na VM de CI; **D**
para `dante`, `cognexa-backend` e `cognexa-frontend` (públicos). Reavaliar B ou C se surgirem mais repositórios
privados, fila recorrente acima do limite da #237 (`EsperaSemRunnerAteSinalizar`, 15 min) ou necessidade de check
obrigatório imposto.

Limites por instância (a dimensionar no piloto): `timeout-minutes` em todo job; uma instância por repositório no
início (a suíte DS longa bloqueia as demais do mesmo repositório, o que é preferível a disputar CPU com outro
repositório); disco com limpeza agendada de imagens e volumes Docker; Docker disponível só para as instâncias que
precisam (`docker` nos rótulos). Acesso ao Docker equivale a root no host: mais um motivo para a VM.

### Runner Linux além do primeiro repositório sem credenciais do D.A.N.T.E.

1. **Registro manual pelo dono**: o token de registro de cada repositório é obtido pelo dono, com a própria conta, na
   tela do repositório (ou `gh` autenticado pelo dono). O D.A.N.T.E. nunca registra runner: a conexão da #237 só tem
   leitura (Metadata, Actions) e não tem `Administration`, por desenho ([ci](ci.md#permissões-mínimas-no-github-237)).
2. **Instância nova por repositório** (opção A), com usuário de SO próprio, sem acesso a diretórios, variáveis,
   sockets ou bancos do D.A.N.T.E. e sem nenhuma referência `secret://host/...` no ambiente do serviço.
3. **Nenhum secret novo** é necessário para os pipelines inventariados: build, testes e PostgreSQL efêmero não usam
   credencial. Se um repositório precisar de secret, ele é do repositório (Settings → Secrets), nunca do D.A.N.T.E.
4. **O D.A.N.T.E. observa de fora**: para acompanhar o repositório novo, o dono acrescenta o alias à conexão
   `IntegracaoDeCi__Conexoes__<id>__Repositorios` e o repositório ao fine-grained token de leitura. Isso não toca o
   runner. O resultado é o mesmo em hosted e self-hosted ([ci](ci.md#provedor-github-actions-237)).
5. **Verificação antes de ligar**: `verificar-runs-on.py --modo self-hosted` no repositório e o checklist do piloto.

## Workflows reutilizáveis

Desenho:

* **o chamador escolhe o runner**: todo reutilizável recebe `runner` (JSON) e usa `runs-on: ${{ fromJSON(inputs.runner) }}`.
  Nenhum reutilizável tem `ubuntu-latest` embutido, então um repositório privado não cai em hosted por esquecimento.
  Os rótulos ficam literais no chamador para a verificação estática funcionar;
* **um check obrigatório por repositório**, `CI · resultado`: job final com `if: always()` que exige `success` de cada
  job necessário e aceita `skipped` só no job condicional cujo plano decidiu não rodar. Assim nenhum check fica
  pendente por workflow pulado e nenhum pulo por falha a montante vira verde;
* **sem filtro `paths:` em workflow que produz o check**: a decisão de rodar suíte pesada é um job de plano dentro do
  workflow (como o `mudancas` atual do frontend);
* **rápido no PR, completo nos marcos**: PR roda build, unitários e E2E essenciais; `push` em `main`, `schedule` e
  `workflow_dispatch` rodam a suíte completa (integração com banco, suítes longas). Nenhum check deixa de existir no
  PR: o que muda é o filtro dentro do job;
* **entradas viram variáveis de ambiente**, nunca interpolação direta em `run:`; nomes de script e de variável são
  validados por expressão regular.

Onde os reutilizáveis vivem:

| Opção | Prós | Contras |
| --- | --- | --- |
| **Cópia versionada em cada repositório** (`./.github/workflows/ci-reutilizavel-*.yml`) — início | sem dependência entre repositórios; funciona em público e privado; a verificação estática enxerga tudo | atualização repetida por repositório |
| Repositório central de templates, chamado por `dono/repo/.github/workflows/x.yml@<sha>` | uma fonte; atualização por referência | repositório privado só compartilha com outros privados do mesmo dono e com a opção de acesso ligada ([configurações de Actions](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/enabling-features-for-your-repository/managing-github-actions-settings-for-a-repository)); repositório público não chama reutilizável privado; o `runs-on` passa a morar fora do repositório (a verificação exige revisão manual e SHA fixo) |

Recomendação: cópia no piloto e no segundo repositório; avaliar repositório central só quando houver três ou mais
consumidores estáveis. Limites da plataforma: até dez níveis de aninhamento e 50 reutilizáveis distintos por
arquivo ([reusing workflow configurations](https://docs.github.com/en/actions/reference/workflows-and-actions/reusable-workflows)).

### Como validar

1. **Sintaxe e coerência** (feito nesta Issue): PyYAML carrega os cinco YAMLs; cada `needs.X` referenciado está em
   `needs`; cada `inputs.X` usado está declarado; cada chamada passa todos os inputs obrigatórios e nenhum
   desconhecido; os blocos `run:` passam em `bash -n`; o agregador foi simulado nos casos sucesso, pulo planejado,
   falha a montante com pulo, pulo indevido, cancelamento e falha.
2. **actionlint** no repositório piloto, depois de copiar os arquivos para `.github/workflows/`
   (`actionlint .github/workflows/*.yml`): confere expressões, contextos e tipos de input que o PyYAML não vê.
   Não foi executado nesta Issue.
3. **`verificar-runs-on.py`** (abaixo) no modo do repositório.
4. **Execução no piloto** (#235), com o checklist de [saída do piloto](#critérios-de-saída-do-piloto-235).

Pontos marcados "a confirmar no piloto" nos YAMLs: `setup-node` dentro do container Playwright (#44) e o passo que
devolve o workspace ao usuário do runner depois de um job em container.

## Verificação de `runs-on`

Ações (`uses:` em passo) não escolhem runner: rodam no runner do job que as usa, então ação de terceiro não desvia o
job para hosted. Quem define runner é o job e, no caso de `uses:` em job, o **workflow reutilizável chamado**. A
verificação cobre os dois:

```bash
python3 verificar-runs-on.py <raiz-do-repo> --modo self-hosted --rotulos self-hosted,Linux,X64,docker
python3 verificar-runs-on.py <raiz-do-repo> --modo hosted
```

| Regra | Resultado |
| --- | --- |
| modo `self-hosted`: job sem `self-hosted` (inclusive `ubuntu-latest` padrão) | erro: consumiria minutos hosted |
| modo `self-hosted`: rótulo fora da lista | erro |
| modo `hosted`: job com `self-hosted` | erro (repositório público) |
| reutilizável local (`./.github/...`) | segue a chamada, resolve `fromJSON(inputs.runner)` com o `with:` do chamador ou o `default`, verifica cada job |
| reutilizável remoto | erro, salvo `--reutilizavel-revisado dono/repo/...@<sha>` após revisão manual |
| `runs-on` com expressão ou matriz não resolvível, `group` sem `labels`, input dinâmico | erro: na dúvida, falha |
| `pull_request_target` | erro sempre |
| `workflow_run`, workflow sem `permissions`, job sem `timeout-minutes` | aviso |

Resultado sobre o inventário de 24/11/2026 (cópias dos workflows em diretório temporário):

| Alvo | Modo | Resultado |
| --- | --- | --- |
| exemplos desta pasta | self-hosted | 10 jobs, 0 erros, 0 avisos |
| `fitnessapp_frontend` | self-hosted | 4 jobs, 4 erros (`ubuntu-latest`), 4 avisos (timeout) |
| `fitnessapp_backend` | self-hosted | 2 jobs, 2 erros, 3 avisos (permissions, timeout) |
| `dante` | hosted | 2 jobs, 2 erros (self-hosted em repositório público) |
| `cognexa-backend` | hosted | 1 job, 0 erros, 2 avisos |

Casos negativos testados: `pull_request_target`, reutilizável remoto, chamador passando `["ubuntu-latest"]` a um
reutilizável, chamador sem `runner`, runner por `vars`/matriz, rótulo fora da lista e `group` sem rótulos. Todos
falham com código 1; um workflow self-hosted simples passa.

Fora dos arquivos de workflow, recursos do GitHub que usam runner próprio (por exemplo, setup padrão de code
scanning ou agentes hospedados) não aparecem nesta verificação. Em 24/11/2026 o code scanning estava desligado ou
`not-configured` nos cinco repositórios; ligar qualquer desses recursos exige conferir onde roda (a confirmar caso a
caso).

Uso sugerido no repositório de destino: um passo no próprio CI (no runner do repositório) ou um hook local antes do
push. Instalar o script é mudança daquele repositório e segue o rollout.

## Políticas

### Rótulos e escolha de runner

* rótulos padrão do runner Linux: `self-hosted`, `Linux`, `X64`; capacidade extra por rótulo: `docker` para jobs com
  `container:`/`services:`/compose. Rótulo **roteia**, não protege: o isolamento vem do registro por repositório, do
  usuário de SO e da VM;
* repositório privado sem cota: todo job em `[self-hosted, Linux, X64(, docker)]`, inclusive plano e agregador;
* repositório público: hosted padrão (`ubuntu-latest`); nunca self-hosted em evento que um fork alcance;
* `runs-on` literal no chamador; variável de repositório (`vars`) para trocar de runner sem commit é possível, mas
  tira a escolha do Git e da verificação estática: só como rollback de emergência, registrado na Issue.

### Concorrência e tempo

* `concurrency` por workflow, evento e PR/ref; `cancel-in-progress` só em `pull_request`. Em `main` cada SHA mantém
  o próprio resultado, que é o que a correlação por SHA do D.A.N.T.E. lê
  ([concorrência](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency));
* `timeout-minutes` em todo job (o padrão da plataforma é 360 min, o que prende o runner pessoal);
* PR de branch interno não precisa de `push` e `pull_request` no mesmo branch: `push` só em `main`.

### Cache

| Ecossistema | Chave | Observação |
| --- | --- | --- |
| NuGet | `nuget-<os>-<hash de Directory.Packages.props, *.csproj, packages.lock.json>` | útil em hosted e em runner efêmero |
| npm | `setup-node` com `cache: npm` (hash do `package-lock.json`) | o cache guarda o diretório do npm, não `node_modules` |
| Playwright | nenhum: browsers vêm da imagem | sem imagem, `~/.cache/ms-playwright` com a versão do `@playwright/test` na chave |

Limite padrão de 10 GB por repositório; PR só restaura cache do próprio branch ou do branch padrão
([dependency caching](https://docs.github.com/en/actions/reference/workflows-and-actions/dependency-caching)), o que
impede um PR de envenenar o cache de outro. Cache nunca guarda credencial nem saída de build publicada.

### Retenção e limpeza

* artefato de teste só na falha (`if: failure()`), `retention-days` de 7 em PR; relatórios de E2E `always()` com
  7 a 14 dias; nome com `run_attempt` para não colidir em reexecução;
* retenção do repositório (90 dias hoje) pode baixar para 30 nos privados (configuração do repositório, com
  autorização);
* workspace: runner persistente reaproveita o diretório de trabalho; o `checkout` limpa, mas arquivos de root criados
  por container quebram a limpeza. Mitigação, em ordem de preferência: runner efêmero; passo final que devolve o
  workspace ao usuário do runner (no template Playwright); hook `ACTIONS_RUNNER_HOOK_JOB_COMPLETED`
  ([scripts antes e depois do job](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/run-scripts));
* host: limpeza agendada de imagens, containers parados e volumes Docker e alerta de disco (operação da #235).

### Secrets e permissões

* `permissions: contents: read` no topo de todo workflow (ou `{}` quando não faz checkout) e nada além por job sem
  justificativa; reutilizável não consegue ampliar o que o chamador deu
  ([GITHUB_TOKEN](https://docs.github.com/en/actions/tutorials/authenticate-with-github_token));
* `actions/checkout` com `persist-credentials: false`: o token não fica no `.git/config` do workspace persistente;
* nenhum secret do D.A.N.T.E. (token do Telegram, do GitHub, do Brain, conexões `secret://host/...`) no runner, no
  usuário do runner ou no repositório; senha de PostgreSQL de teste é descartável e vale só no container do job;
* actions de terceiros fixadas por SHA completo; `actions/*` por versão maior é aceitável no início (a configuração
  "SHA pinning" está desligada nos cinco repositórios).

### PRs externos e forks

* **nunca rodar código de fork em self-hosted.** Em repositório privado, workflows de fork estão desligados (estado
  atual) e devem continuar assim. Em repositório público, self-hosted não atende PR: condição `if:` no YAML **não é
  barreira**, porque o PR pode alterar o próprio workflow;
* `pull_request_target` proibido para qualquer workflow que faça checkout ou execute código do PR
  ([preventing pwn requests](https://securitylab.github.com/resources/github-actions-preventing-pwn-requests/));
  a verificação recusa o gatilho em qualquer uso;
* repositório público com runner self-hosted registrado: aprovação para todos os colaboradores externos, não só os
  de primeira vez (configuração do repositório, decisão humana);
* PR de fork aguardando aprovação aparece como `action_required`, que o D.A.N.T.E. lê como `NaFila`
  ([mapeamento](ci.md#mapeamento-de-estados)).

## Matriz de falhas

Cada linha diz o sintoma no GitHub, como o D.A.N.T.E. o lê pela porta da #237 e o que fazer. A categoria de falha só
existe com a evidência exigida pela [matriz](ci.md#matriz-de-falhas) do D.A.N.T.E.

| Falha | Sintoma | Leitura no D.A.N.T.E. | Ação |
| --- | --- | --- | --- |
| runner offline ou ocupado | job `queued` sem início | `AguardandoRunner`; `RunnerIndisponivel` só após `TempoSemRunner` (15 min) | reiniciar o serviço do runner; se persistir, rollback do job para hosted (com cota) ou desligar o job |
| cota hosted zerada | job hosted não inicia, anotação de cobrança | `LimiteDeMinutosOuCobranca` só com a anotação do provedor | verificação de `runs-on`; mover o job para self-hosted |
| Docker parado ou sem permissão | falha em "Initialize containers" / service container | `ErroNoCodigoOuTeste` só com etapa e log; infra aparece no log | restaurar Docker; job com `docker` nos rótulos só em instância com Docker |
| limite de pull de imagem | falha no pull de `postgres`/Playwright | etapa com falha + trecho do log | espelho/cache de imagem na VM (a confirmar no piloto) |
| disco cheio | falhas de escrita, `No space left on device` | trecho do log | limpeza agendada; alerta de disco |
| workspace com arquivos de root | `checkout` falha ao limpar | etapa `checkout` | passo de devolução do workspace, hook ou runner efêmero |
| Node/SDK divergente | `EBADENGINE`, `global.json` não satisfeito | etapa `npm ci`/`setup-dotnet` | versão vem do projeto (`.nvmrc`, `global.json`); #44 |
| teste instável | falha intermitente, passa na reexecução | reexecução é **outra tentativa**; vale a maior | registrar e corrigir no repositório; nunca reexecutar até ficar verde em silêncio |
| testhost encerrado sem falha visível | `dotnet test` aprovado com total menor que o esperado | sucesso aparente | conferir a contagem de testes no piloto (a confirmar como passo do template) |
| timeout | `timed_out` | `Expirada` | ajustar `timeout-minutes` ou dividir suíte |
| cancelado por concorrência | `cancelled` em PR | `Cancelada` | esperado: vale a execução nova |
| check pendente para sempre | workflow pulado por `paths:` | `SemExecucao` | sem `paths:`; plano + agregador |
| pulo por falha a montante | condicional `skipped` com dependência vermelha | agregador vermelho | agregador exige o plano em `success` |
| PR de fork aguardando aprovação | `action_required` | `NaFila` | aprovar só após revisar o diff do workflow |
| artefato acima da cota de armazenamento | upload falha | etapa de upload | artefato só na falha, retenção curta |
| log expirado ou negado | log indisponível | `LogsInacessiveis`; estado do job inalterado | retenção do repositório; permissão da conexão |

## Rollout incremental por repositório

Pré-requisitos gerais: [fitnessapp_frontend#44](https://github.com/juanverass/fitnessapp_frontend/issues/44)
resolvida; VM de CI da #235 pronta e isolada do D.A.N.T.E. Cada passo abaixo é uma Issue/PR **no repositório de
destino**, aberta só com autorização do dono para mudar os workflows **daquele** repositório. Nenhum passo altera
outro repositório.

| Passo | Repositório | Mudança | Rollback |
| --- | --- | --- | --- |
| 1 | `fitnessapp_frontend` (piloto #235) | instância de runner própria; os quatro jobs em self-hosted (`docker` nos E2E); `timeout-minutes`; agregador `CI · resultado`; verificação de `runs-on` | reverter o commit (volta a `ubuntu-latest`, só com cota) ou desligar o workflow/job; suspender `schedule` |
| 2 | `fitnessapp_backend` | segunda instância na VM; template .NET (sem banco); `migrations` com ferramenta local; `permissions`; `concurrency`; artefato só na falha | reverter o commit; desligar o job |
| 3 | `cognexa-backend` | continua hosted; `permissions`, `concurrency`, `timeout`, agregador, `push` só em main; PostgreSQL por service container com porta dinâmica | reverter o commit |
| 4 | `dante` | decisão humana sobre o runner do repositório público (desregistrar, ou manter só para dispatch com aprovação de todos os externos); CI de PR em hosted, se desejado | desligar o workflow |
| 5 | `cognexa-frontend` | template Node em hosted quando houver código | — |

Regras do rollout:

* um repositório por vez; o seguinte só começa depois de duas semanas (ou dez execuções) estáveis do anterior;
* rollback **por repositório e por job**: reverter o commit do workflow (auditável) é o caminho padrão; trocar
  `runs-on` por variável ou desligar o workflow pela interface são emergências registradas na Issue do repositório;
* rollback para hosted em repositório privado só com cota disponível e decisão explícita; sem cota, desligar o job é
  o rollback (job desligado não consome minutos e não fica falhando em silêncio);
* repositórios não vão para organização sem decisão humana; a opção C fica registrada como alternativa.

### Critérios de saída do piloto (#235)

* todos os jobs do `CI` do `fitnessapp_frontend` executam no runner self-hosted (rótulos e nome do runner visíveis em
  cada job), inclusive o Playwright DS condicional numa execução em que o plano o aciona;
* `verificar-runs-on.py --modo self-hosted` sem erros no repositório;
* PR de teste com `CI · resultado` verde e outro com falha proposital vermelho, ambos com relatórios publicados;
* consumo de minutos hosted do repositório sem aumento no período do piloto (aferido na cobrança da conta);
* runner sem acesso a qualquer secret, diretório ou serviço do D.A.N.T.E. (verificado na VM);
* runner offline produz `AguardandoRunner` e depois `RunnerIndisponivel` no D.A.N.T.E. (quando a #238 existir), sem
  falso diagnóstico de código;
* rollback ensaiado: reverter o commit e voltar ao estado anterior sem efeito em outro repositório;
* workspace limpo entre execuções (nenhum arquivo de root remanescente) e disco estável depois de uma semana.

## Fora de escopo

Registrar ou configurar runner (#235), mudar workflow, secret ou configuração de qualquer repositório, correlação e
consulta pelo Telegram (#238), diagnóstico (#239), correção supervisionada (#240) e validação integrada (#242).
