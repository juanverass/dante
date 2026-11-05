# Fechamento Linux da PR #283

Código validado: `abafaabdbbdab7ecf3214c47a5c9cb1b510ae352`. Base comparada: `68479e488e6430d4eb73406089d0af2651e9bd93`.
Execução do implementador, sem CI e sem revisão independente de execução.
[Resultados e comparação em JSON](fechamento-linux-pr283.json).

## Escopo final por decisão do mantenedor

Contenção estrita **somente Linux**. Backend ContencaoWindows/Job Objects e seu teste
foram removidos. O launcher recusa modo estrito fora do Linux antes de selecionar
backend, sem fallback silencioso. Não há promessa nem pendência de homologação Windows
como condição desta entrega Linux. Eventual retomada fica fora desta PR.
O legado de compatibilidade continua apenas por escolha explícita e com aviso de garantia
inferior; não representa nova oferta de contenção Windows.

A AD-63 e o contrato foram atualizados. WSL é Linux e suas instruções operacionais
históricas não foram removidas. Serviço, banco canônico e configuração do homelab não mudaram.

## Comprovação dinâmica do baseline Telegram

Checkout separado, **sem alterações de código**, no commit exato 68479e4. Mesmo SDK
.NET 10.0.114, host, PostgreSQL descartável, comando de teste, paralelismo padrão e
ambiente de teste da PR. Chaves de conexão canônica/Brain/PG foram retiradas somente
do ambiente dos subprocessos de teste nas duas versões; DANTE_TEST_POSTGRES apontou
127.0.0.1:55433/postgres. Nenhuma variável do serviço ativo foi modificada.

Cada suíte usou scope transitório externo exclusivo para limpar também eventuais órfãos
do código antigo, sem modificar seus testes. Foi executado `dotnet test Dante.sln --no-build`
com logger TRX. DANTE_TEST_NATIVE_CONTAINMENT=1 foi usado nas duas versões; na base,
que não contém essa feature, o opt-in não altera o código. Não houve alteração de teste,
asserção ou atraso artificial para fabricar a falha.

| Base 68479e4 | Aprovados | Falhas | Skips |
| --- | ---: | --- | ---: |
| 1 | 1.668 | GracefulExitDoesNotLeaveOrphanedChildProcess | 24 |
| 2 | 1.667 | GracefulExitDoesNotLeaveOrphanedChildProcess e OrdinarySessionReplyReachesTelegramAsHtmlWithoutKeyboard | 24 |

A investigação da base parou após reproduzir o teste solicitado, não após escolher
um resultado verde. A falha do Telegram teve **a mesma assinatura** observada na PR:

```text
Assert.Single() Failure: The collection contained 2 items
Collection: sendChatAction(typing) e sendMessage(HTML)
TelegramBotApiTests.cs:291
```

O texto acima resume a coleção do handler HTTP simulado; o erro original consta no JSON.
`TelegramBotApiTests.cs`, `TelegramDeliveryService.cs` e `TelegramBotApi.cs` são
**byte a byte idênticos** entre a base e a PR. SHA-256s estão no JSON.
Portanto, a intermitência é comprovadamente preexistente. Não foi corrigida, escondida,
ignorada ou absorvida nesta PR. O erro de processos encontrado na base corresponde à
regressão original que motivou esta manutenção; não ocorreu na validação atual.

Reprodução da comparação em checkout separado:

```bash
git worktree add --detach /tmp/dante-baseline-68479e4 68479e488e6430d4eb73406089d0af2651e9bd93
# Em cada checkout, com DANTE_TEST_POSTGRES configurado para uma instância descartável:
dotnet restore
dotnet build Dante.sln -m:1
DANTE_TEST_NATIVE_CONTAINMENT=1 dotnet test Dante.sln --no-build --logger trx
```

O invólucro de proteção usado nas duas versões foi `systemd-run --user --scope --quiet
--no-ask-password --expand-environment=no --unit=dante-validation-<GUID>.scope
--property=TimeoutStopSec=3s -- /usr/bin/dotnet test Dante.sln --no-build ...`, seguido
de stop exclusivamente desse scope e consulta de LoadState/ActiveState/ControlGroup.
As credenciais de teste não são publicadas. O teste pode passar isoladamente por ser
intermitente; a evidência acima vem da suíte completa, sem alterações no caso.

## Validação final da versão Linux

- Base: build sem erros e nove avisos conhecidos. Esses avisos não foram corrigidos
  no checkout de comparação.
- PR: restore/build sem erros e **zero avisos**.
- Processos, contenção, contrato e arquitetura: **86 aprovados, zero falhas e zero skips**.
- Duas novas verificações cobrem recusa de plataforma não Linux, ausência do tipo
  ContencaoWindows na assembly e admissão do modo estrito no Linux.
- A implementação Linux de ContencaoSystemd e LimpezaDaInicializacao é idêntica, por
  blob Git, à versão 2dadd58 que passou nas 100 rodadas anteriores (1.100 casos nativos,
  1.000 falhas simuladas, 100 de backpressure) e 164 aprovações sob carga. Não foi
  reaberto nem reescrito o mecanismo Linux. Os blobs estão no JSON; os resultados
  históricos permanecem no [checkpoint anterior](contencao-linux-pr283.md).
- Os testes nativos reais foram executados novamente no código final, com opt-in,
  nos específicos e em cada suíte completa. Não se apresentam skips como aprovação.

| Tentativa PR | Resultado | Aprovados | Falhas | Skips |
| --- | --- | ---: | ---: | ---: |
| 1 | Completa | 1.741 | 0 | 24 |
| 2 | Completa | 1.741 | 0 | 24 |
| 3 | SDK abortou antes do banner/TRX, SIGABRT/0x80131506 | — | — | — |
| 4 | Reposição única da execução abortada; completa | 1.741 | 0 | 24 |

São **três suítes completas verdes em quatro tentativas**, não três tentativas
consecutivas sem falhas. O aborto não é contabilizado como sucesso; a causa permanece
não determinada. Não se atribui esse erro do SDK à contenção nem se declara resolvido
por remover Windows. Nenhuma alteração de runtime, supressão ou repetição até obter
uma sequência verde foi feita. A intermitência conhecida do Telegram e o aborto do
SDK impedem uma promessa de determinismo global, embora as três suítes concluídas
nesta validação tenham passado.

Os 24 skips são 22 integrações live sem opt-in e dois cenários dependentes de serviço
externo de embeddings/corpus. Não há mais teste Windows ignorado mascarando suporte.
O banco utilizado é descartável; nenhum teste foi apontado ao banco canônico.

## Cancelamento e garantia do helper

O limite WaitAsync permite prosseguir, mas não força uma Task não cooperativa a parar.
Os delegates de produção devem respeitar o token e possuir liberação própria dos
subprocessos. O teste que ignora token só comprova que stop/observação seguintes não
ficam bloqueados; não comprova que a Task anterior foi abortada. Essa condição agora
está explícita no contrato, conforme a observação do quinto review.

## Preservação e encaminhamento

Verificação final: Worker com o mesmo PID/início/cgroup, hashes da configuração
operacional preservados, zero probes e zero scopes de teste remanescentes. Main e
sua alteração preexistente em deploy/systemd/dante.service foram preservadas. Nenhum
instalador, restart, migration em produção ou acesso deste trabalho ao banco canônico.
Brain permanece lexical; #147 não é encerrada/homologada por esta PR e #282 não foi iniciada.

Os achados restantes do quinto review foram tratados: execução Windows retirada e
falha Telegram reproduzida na base. Entrega pronta para **revisão final do escopo Linux**;
merge e implantação continuam decisões separadas do mantenedor. Não são reivindicadas
homologação Windows, estabilidade global perfeita ou execução independente.

Artefatos locais: `/tmp/dante-linux-only/` (TRX, logs e conferência operacional).
Este Markdown e JSON registram as comparações, assinaturas, comandos e resultados
necessários à revisão remota sem depender desses caminhos locais.
