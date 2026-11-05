# Contenção nativa de sessões interativas

A contenção pertence a `Dante.Infrastructure.Agentes`; o domínio e a aplicação não
recebem referências a systemd, cgroups ou Win32. Substitui a garantia baseada em
observação da AD-17. Não altera os executores one-shot nem o Brain.

## Contrato e disponibilidade

- Padrão: **estrito**. Cada CLI só executa depois de estar associada à contenção.
  Falha de criação/associação impede o lançamento. Falha de observação/encerramento
  ou prazo excedido faz `Completion` falhar. Sucesso exige raiz encerrada e grupo vazio.
- `DANTE_AGENT_CONTAINMENT_MODE=compatibility` é a única seleção por ambiente do
  caminho legado. A API aceita também opções explícitas, úteis para testes de protocolo.
  Não há detecção que degrade automaticamente. Valor desconhecido recusa lançamento.
- Compatibilidade publica aviso no log e em `AvisoDeContencao`, com
  `ContencaoNativa=false`. Só confirma a saída dos descendentes observados;
  filhos criados/reparentados entre snapshots podem sobreviver.
- O prazo de limpeza continua compartilhado (5 segundos). Cancelamento do lançamento
  impede liberar o bootstrap e tenta limpar a unidade exclusiva. Cancelamento de uma
  escrita não encerra a sessão. Stop/Dispose/Kill repetidos compartilham a limpeza nativa.

A configuração é uma proposta de código para revisão, não uma mudança aplicada ao
serviço ativo. Não executar instalador, daemon-reload ou migração operacional para
experimentar esta funcionalidade.

## Linux: escopo transitório de usuário por agente

`systemd-run --user --scope` associa seu próprio processo à unidade antes de `exec`.
Uma unidade aleatória `dante-agent-<guid>.scope` é criada por lançamento, com descrição
fixa, KillMode=control-group, SendSIGKILL=yes e TimeoutStopSec=3s.

Um bootstrap `/bin/sh` usa **script fixo**, sem interpolação de prompt, executável ou
argumentos: anuncia `identificador-aleatório:PID` em uma linha exclusiva de stdout,
para com STOP e depois executa `exec "$@"`. A infraestrutura consome a linha antes de
entregar stdout à CLI, valida o identificador e obtém o PID anunciado pelo próprio
bootstrap. Confirma o horário de início, abre pidfd desse PID e verifica o campo Pid
em `/proc/self/fdinfo`. Em seguida confirma estado parado e cgroup v2 específico antes
de enviar CONT por **esse pidfd**. Identidade, STOP e associação ao scope são
revalidados imediatamente antes da liberação; a mesma janela de 5s cobre todo o protocolo.
Handshake ausente/inválido, saída antecipada, divergência ou prazo excedido recusam o lançamento.

Em `--scope`, a implementação upstream de systemd-run v259 chama `execvpe` depois de
StartTransientUnit e de aguardar a ativação; diferentemente de `--service`, não delega
esse exec ao manager. Os PIDs do processo iniciado e do bootstrap coincidem na versão
avaliada (systemd 259), mas essa igualdade agora é **medida pelo handshake**, não inferida de Process.Id.
A versão que anunciar PID distinto é recusada explicitamente antes do CONT, pois o
monitor usa o processo original do scope; não se tenta adaptar por polling ou liberar
um comando cuja identidade não foi confirmada.
A CLI e todos os filhos criados diretamente por fork/exec nascem nesse grupo, mesmo
com setsid, duplo fork ou saída do pai entre snapshots. Não existe migração posterior
do processo de agente. O PID se conserva no exec. Isto acrescenta um bootstrap shell
interno à AD-17; comandos da CLI continuam em ArgumentList, nunca viram código shell.

XDG_RUNTIME_DIR e DBUS_SESSION_BUS_ADDRESS adicionados para conectar o bootstrap ao
manager são removidos antes de exec, quando não faziam parte do ambiente autorizado
da CLI. Segredos não são passados em --setenv nem em descrições de unidade.
`--expand-environment=no` preserva argumentos literais com `$`.

Ao sair o principal, ou em Kill, a infraestrutura solicita stop **somente dessa unidade**.
O manager encerra o grupo inteiro, inclusive filhos reparentados, e aguarda a parada.
A confirmação lê `cgroup.events: populated 0`; remoção do diretório do escopo numa
hierarquia ainda acessível também demonstra que ficou vazio. Enviar sinal sozinho não
constitui confirmação. Estado desconhecido e falhas de leitura não equivalem a vazio.

Em falhas de inicialização, a limpeza tem três etapas independentes, sempre tentadas:
encerrar/aguardar o bootstrap, parar a unidade e confirmar raiz encerrada + contenção
vazia. Cada etapa tem até 5s e token próprio, independente do lançamento cancelado.
Timeout/sinal recusado não pula stop; falha/timeout de stop não pula confirmação.
A falha original e todos os erros de limpeza são agregados, sem declarar sucesso.
Quando o caminho do cgroup ainda não foi capturado, a confirmação consulta LoadState,
ActiveState e ControlGroup da unidade exclusiva. Unidade ausente só é aceita com
resposta positiva do manager e saída da raiz confirmada; erro genérico não equivale
a unidade inexistente. No encerramento normal, o prazo compartilhado de Completion
permanece 5s. A limpeza de inicialização pode consumir até três prazos separados;
liberar recursos dos subprocessos de controle também tem espera limitada.

Requisitos: cgroups v2, systemd-run com `--expand-environment=no` (systemd >=254),
user manager acessível, `/bin/sh`, `/proc` e libc com pidfd_open/pidfd_send_signal.
Não exige Delegate=yes no Worker, porque a unidade é criada pelo manager fora de seu
cgroup. Não escreve no cgroup do Worker e não usa cgroup.kill compartilhado.

O usuário do serviço precisa poder criar escopos próprios. O código não instala,
reinicia ou modifica unidade persistente. Testes reais usam apenas suas próprias
unidades transitórias. Outro manager/namespace requer acesso explícito no laboratório.

## Windows: Job Object na criação

CreateJobObject + KILL_ON_JOB_CLOSE, sem BREAKAWAY_OK/SILENT_BREAKAWAY_OK.
STARTUPINFOEX inclui JOB_LIST e HANDLE_LIST; CreateProcessW associa o processo
atomicamente ao Job e o mantém suspenso. Confirma IsProcessInJob, abre o handle do
processo original e prepara os pipes antes de ResumeThread. Não há intervalo de código
da CLI executando fora do Job, nem dependência de associação posterior ao Start.

Apenas os três handles filhos dos pipes são herdados. O handle do Job não é herdado.
O ambiente vem do mesmo filtro de AgentProcessStartInfo, em bloco Unicode, e os
argumentos usam escaping Windows, sem cmd.exe intermediário. Falha na criação fecha
Job, pipes, thread e handles; fechamento do último Job mata processos associados.

TerminateJobObject solicita a limpeza. QueryInformationJobObject confirma ActiveProcesses=0
com espera limitada pelo prazo, em consultas espaçadas de 25 ms. Notificações de Job
não são usadas como prova única, pois a documentação não garante todas as mensagens.
A contagem é do grupo nativo, independente da ancestralidade ou de PIDs reparentados.

Requisitos: Windows 10/Server 2016 ou posterior (JOB_LIST) e permissão para Jobs aninhados
se o host já estiver em Job. Hosts/CLIs que exijam breakaway incompatível devem falhar
explicitamente ou usar compatibilidade configurada conscientemente. Não há runner
Windows neste laboratório Linux: compilação não comprova funcionamento do backend.

## Compatibilidade e limites

Claude stream-json e Codex app-server já usam stdin/stdout sem terminal. Mantêm
executable, cwd, argumentos, ambiente filtrado e pipes. Shells/auxiliares normais
herdam a contenção. Processos existentes não são retroativamente incorporados.

Isto é gerenciamento de ciclo de vida, **não sandbox contra código malicioso**:
processos com o mesmo usuário podem solicitar a outros serviços que executem tarefas
fora da árvore; WMI/serviços Windows e criação de unidades Linux externas não são
filhos diretos. Não prometer contenção de brokers nem de processos que deliberadamente
alterem associação com privilégios do host. CLIs reais requerem homologação separada,
incluindo uso de shells, sandbox próprio do Codex, autenticação e MCP; `--version` e
probes/fakes não equivalem a homologação funcional de uma sessão autenticada.

## Validação reproduzível

`DANTE_TEST_NATIVE_CONTAINMENT=1 dotnet test tests/Dante.Tests --filter FullyQualifiedName~ContencaoNativaTests`

Sem opt-in, integração nativa gera skip explícito. Com opt-in, indisponibilidade é falha,
não skip. Os testes de contrato com contenção falsa executam sempre, cobrindo ordem de
Completion, falha de observação e timeout. Testes de protocolo existentes selecionam
compatibilidade explicitamente; não se confundem com evidência de contenção nativa.

O teste da barreira registra PID inicial, PID anunciado, PID do pidfd, horário de início,
unidade, caminho do cgroup e estado T no TRX. Um arquivo marcador, escrito somente pela
CLI simulada, está ausente durante a barreira e só aparece após o CONT. Outro teste
cria dois forks com setsid, aguarda a saída do intermediário, comprova associação dos
filhos reparentados ao mesmo scope e verifica limpeza após morte inesperada da raiz.
Os registros não incluem variáveis de ambiente, argumentos de tarefas ou segredos.

Cobrir filhos após EOF, quatro níveis, saída abrupta, parada forçada, concorrência,
argumentos/ambiente, indisponibilidade do manager, executável ausente e shell com setsid.
Repetir cenários críticos 100 vezes e três suítes completas com PostgreSQL descartável.
Não declarar determinismo Windows sem executar em Windows. Não mascarar falhas
preexistentes de Telegram/continuidade.

Fontes primárias:

- [systemd-run: ordem de criação de scope e exec](https://github.com/systemd/systemd/blob/v259/src/run/run.c).
- [systemd-run: scopes e expansão de ambiente](https://man7.org/linux/man-pages/man1/systemd-run.1.html).
- [cgroups v2: herança e populated](https://docs.kernel.org/admin-guide/cgroup-v2.html).
- [Windows: atributos JOB_LIST e HANDLE_LIST](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute).
- [Windows: Jobs, herança, notificações e fechamento](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).
