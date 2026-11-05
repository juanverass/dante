# Evidência Linux — contenção de agentes, PR #283

Checkpoint histórico do commit 2dadd58, anterior à retirada do backend Windows.
Os números e skips abaixo registram aquela execução; não descrevem o escopo atual.
A decisão Linux e a comparação dinâmica com a base constam no
[fechamento da validação](fechamento-linux-pr283.md).

Fonte executada: `2dadd58aa8b096e76b84216dff5cfa53569e6586`.
Execução do implementador em 05/11/2026, sem CI e sem revisão independente de execução.
Este relatório e o [JSON de resultados](contencao-linux-pr283.json) versionam a evidência;
os commits posteriores deste relatório não alteram código nem testes executados.

## Ambiente e escopo

- systemd 259.9, kernel 7.0.0-42-generic, x86_64, filesystem cgroup2.
- Worktree isolada, scopes transitórios próprios no user manager existente, fora do
  cgroup do Worker. Sem VM ou manager descartável.
- `/usr/bin/systemd-run`, `/usr/bin/systemctl`, pidfd e cgroups de kernel reais;
  o alvo controlado é o Dante.ProcessProbe/shell. Não há fake do manager nesses casos.
- PostgreSQL somente na instância descartável 127.0.0.1:55433/postgres; credenciais omitidas.
- Nenhuma alteração em Worker ativo, serviço persistente, arquivos de ambiente ou banco canônico.
  Brain operacional permanece lexical. Windows não foi executado nem homologado.

## Comandos reproduzíveis

A partir da raiz do repositório, com SDK .NET 10 e requisitos Linux do
[contrato de contenção](../contencao-de-agentes.md). Configure DANTE_TEST_POSTGRES
somente para uma instância descartável antes da suíte completa.

```bash
dotnet restore
dotnet build Dante.sln -m:1
DANTE_TEST_NATIVE_CONTAINMENT=1 dotnet test tests/Dante.Tests --no-build --filter 'FullyQualifiedName~Contencao|FullyQualifiedName~LimpezaDaInicializacaoTests|FullyQualifiedName~InteractiveAgentProcessTests|FullyQualifiedName~ApplicationArchitectureTests' --logger 'trx;LogFileName=especificos.trx'
```

Sem `DANTE_TEST_NATIVE_CONTAINMENT=1`, integração nativa é skip explícito e **não comprova**
funcionamento do backend. Com opt-in, indisponibilidade é falha, sem fallback automático.

Filtro efetivamente usado nas repetições (100 processos de teste distintos, fases sem
suíte completa concorrente):

```bash
dante_filtro_repeticao='FullyQualifiedName~BootstrapAnunciaPidReal|FullyQualifiedName~DuploForkESetsid|FullyQualifiedName~SaidaDoBootstrapNaBarreira|FullyQualifiedName~FilhoNascidoAposEOF|FullyQualifiedName~MultiplosNiveisNaoSobrevivem|FullyQualifiedName~LancamentosConcorrentes|FullyQualifiedName~CancelamentoNaBarreira|FullyQualifiedName~ShellEAuxiliares|FullyQualifiedName~CgroupProprioFicaVazio|FullyQualifiedName~SlowReaderReceivesEveryLine|FullyQualifiedName~LimpezaDaInicializacaoTests'
for dante_rodada in $(seq 1 100); do
  DANTE_TEST_NATIVE_CONTAINMENT=1 dotnet test tests/Dante.Tests --no-build --filter "$dante_filtro_repeticao" --logger "trx;LogFileName=rodada-$dante_rodada.trx" || exit 1
done
```

Carga: quatro execuções concorrentes com filtro
`FullyQualifiedName~ContencaoNativaTests|FullyQualifiedName~LimpezaDaInicializacaoTests`.
Após a carga terminar, três execuções consecutivas de
`DANTE_TEST_NATIVE_CONTAINMENT=1 dotnet test Dante.sln --no-build`, com conexão descartável.
Não foram repetidas até escolher três resultados verdes.

## Resultados e distinção entre contrato e nativo

- Build: zero erros e zero avisos.
- Específicos: 84 aprovados, zero falhas e um skip Windows. Não são todos testes nativos.
- Repetições: **100/100 rodadas**, 22 casos cada, **2.200 aprovações**, zero falhas,
  224,44 s. Por rodada: **11 casos Linux reais**, dez casos de limpeza com falhas
  simuladas e um caso de backpressure em compatibilidade. Logo, são **1.100 aprovações
  de casos nativos**, 1.000 de contrato simulado e 100 de backpressure.
- Carga: quatro execuções de 41 casos, **164 aprovações**, zero falhas. Mistura testes
  de configuração/contrato e integrações reais; não apresentar tudo como nativo.

Os casos simulados de LimpezaDaInicializacaoTests injetam erro de sinal pidfd, timeout
na espera, erro de systemctl stop, populated 1, acesso negado à observação, timeout da
confirmação, falhas simultâneas, timeout do stop e espera que ignora cancelamento.
Comprovam ordem das etapas e agregação da falha original/erros; **não** simulam uma falha
real do kernel ou de permissões do manager como se tivesse ocorrido no homelab.
Tokens novos e prazos limitados permitem sempre tentar stop e confirmação.

Os casos Linux reais usam bootstrap parado, marcador anterior à CLI, cancelamento/saída
na barreira, quatro níveis, filho após EOF, raiz inesperadamente encerrada, parada forçada,
concorrência, setsid/duplo fork/reparentamento e confirmação de vazio após Completion.
O timeout real de inicialização também executou na classe nativa e nas suítes completas;
seu prazo é de 5s, diferentemente do prazo reduzido dos testes simulados.

## Amostra real do handshake

Observação do teste BootstrapAnunciaPidRealDentroDoScopeAntesDeExecutarCLI, na primeira
rodada. Valores são amostra da execução, não constantes do contrato:

```json
{
  "PidCliente": 1271194,
  "PidBootstrap": 1271194,
  "PidDoPidfd": 1271194,
  "InicioUtc": "2026-11-05T14:47:35.7758794Z",
  "Unidade": "dante-agent-6b00bd08a25b408a8a1fcdf6e12cef0c.scope",
  "Cgroup": "/user.slice/user-1000.slice/user@1000.service/app.slice/dante-agent-6b00bd08a25b408a8a1fcdf6e12cef0c.scope",
  "Estado": "T"
}
```

O teste confere que o marcador escrito somente pela CLI ainda não existe na barreira,
relê o estado T e a associação ao cgroup, compara início UTC e pidfd, e só depois libera
CONT. Após a liberação, o marcador contém o PID da CLI. Após Completion, confirma
contenção vazia. A igualdade dos PIDs foi observada/validada, não presumida.

## Setsid, duplo fork e reparentamento

Amostra de DuploForkESetsidReparentamFilhosSemEscaparDoScope:

```json
{
  "principal": 1271558,
  "intermediario": 1271562,
  "neto": 1271563,
  "bisneto": 1271564
}
```

O teste aguarda o intermediário encerrar, verifica PPid diferente do intermediário e
mesmo cgroup para neto/bisneto, mata a raiz e verifica que ambos encerraram antes de
Completion concluir. O JSON registra PIDs; as condições anteriores são asserções do teste,
não uma inferência de que PPid necessariamente virou 1.

Cancelamento na barreira, saída antecipada e confirmação de vazio também constam no
JSON versionado e nos resultados dos testes. A limpeza do caminho de falha agora sempre
tenta sinal/espera, stop e confirmação em etapas independentes, com até 5s por etapa;
a falha original e os erros de limpeza são preservados. Completion normal mantém 5s.

## Três suítes completas consecutivas

| Execução | Aprovados | Falhas | Skips | Falha |
| --- | ---: | ---: | ---: | --- |
| 1 | 1739 | 0 | 25 | Nenhuma |
| 2 | 1738 | 1 | 25 | TelegramBotApiTests.OrdinarySessionReplyReachesTelegramAsHtmlWithoutKeyboard |
| 3 | 1738 | 1 | 25 | TelegramBotApiTests.OrdinarySessionReplyReachesTelegramAsHtmlWithoutKeyboard |

A falha das execuções 2 e 3 é a intermitência já documentada do Telegram: typing e resposta
aparecem juntos na coleção de Assert.Single (`TelegramBotApiTests.cs:291`). O teste e o
código de produção não mudaram nesta correção. **Não houve três suítes verdes e não se
atesta determinismo global.** Nenhuma falha de contenção ocorreu nesta rodada.
Os 25 skips por suíte são 22 integrações live sem opt-in, dois cenários com endpoint
externo de embeddings/corpus ausente e um teste Windows. Skips não são aprovações.

## Preservação e pendências

Verificação final: zero probes sobreviventes e zero scopes de teste remanescentes;
hashes do serviço/template local/arquivos de ambiente preservados, mesmo PID/início/cgroup
do Worker e nenhuma chave de embedding inserida. Main continua em 68479e4 com somente
sua alteração operacional preexistente. Não houve acesso deste trabalho ao banco canônico,
instalador, daemon-reload, reinício, implantação ou migração operacional.

Windows segue sem runner e **não homologado**. A suíte global continua com pendência
intermitente do Telegram. #147 permanece fora desta homologação E2E; #282 não iniciada.
Revisão independente e decisões de publicação/merge/implantação continuam separadas.

Artefatos locais completos (não versionados): `/tmp/dante-review4/`, incluindo logs,
TRX de cada rodada/suíte e conferência operacional. Este Markdown e o JSON contêm os
comandos, contagens e amostras necessárias para revisão remota; não dependem de o revisor
conseguir abrir esses caminhos locais. Evidência não equivale a execução independente.
