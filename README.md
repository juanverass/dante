# D.A.N.T.E.

**Distributed Agent Network for Task Execution**

Serviço local em .NET 10 com runners de agentes e transporte Telegram básico. Os comandos de execução e o controle de acesso serão adicionados nas próximas etapas do MVP.

## Requisitos

- SDK .NET 10

## Desenvolvimento

```bash
dotnet build Dante.sln
dotnet test Dante.sln
dotnet run --project src/Dante.Worker
```

O Worker permanece ativo até receber um sinal de encerramento (por exemplo, `Ctrl+C`). A configuração padrão de logging está em `src/Dante.Worker/appsettings.json`. Configurações locais e segredos devem ficar fora do repositório; use variáveis de ambiente quando necessário.

## Execução de agentes

`IAgentProcessExecutor` oferece `IsAvailable(AgentKind)` e `ExecuteAsync(AgentProcessRequest, CancellationToken)`. O pedido informa o agente, um diretório de trabalho absoluto e uma lista de argumentos. A implementação resolve apenas os executáveis `codex` e `claude` no `PATH`; no Windows, procura binários nativos `.exe` ou `.com`. Scripts `.cmd` e `.bat` não são iniciados, pois exigiriam um shell. A infraestrutura é registrada no contêiner de serviços do Worker; os runners de agentes usam essa infraestrutura.

O resultado contém stdout, stderr, código de saída, horários de início e fim e estado (`Succeeded`, `Failed` ou `Cancelled`). Uma CLI ausente ou uma falha ao iniciar o processo retorna `Failed` com uma mensagem de erro. O cancelamento encerra a árvore de processos iniciada.

## Runner do Codex

`ICodexRunner.RunAsync(prompt, workingDirectory, cancellationToken)` executa `codex exec` no diretório informado e devolve o resultado completo da execução. O diretório deve existir, ser absoluto e pertencer a um repositório Git. O runner fixa `--approve-for-me`, que permite alterações no workspace e usa revisão automática quando uma ação exigir aprovação. O prompt é passado como um único argumento, sem interpretação por shell; comandos, opções e executável são fixos pelo runner. O runner é registrado no contêiner de serviços do Worker para uso pela futura integração com Telegram.

O Codex CLI precisa estar instalado e autenticado localmente (`codex login`). No Windows, coloque o executável nativo `codex.exe` ou `codex.com` no `PATH`, conforme a política da infraestrutura de processos. A saída de erro da CLI, inclusive mensagens de autenticação emitidas por ela, fica em `StandardError`; `ErrorMessage` informa a falha da execução. Uma CLI ausente retorna `Failed` com mensagem explícita. Um `CancellationToken` cancela a execução e encerra o processo.

## Runner do Claude

`IClaudeRunner.RunAsync(prompt, workingDirectory, cancellationToken)` executa `claude --print --permission-mode auto --permission-prompts none` no diretório informado e devolve o resultado completo da execução. O modo `auto` permite que o Claude avalie ações sem aguardar aprovação humana; ações que ainda precisariam de intervenção são negadas. O prompt é passado como um único argumento após `--`, sem interpretação por shell ou alteração das opções fixadas pelo runner. O runner é registrado no contêiner de serviços do Worker para uso pela futura integração com Telegram.

O Claude Code CLI precisa estar na versão 2.1.259 ou superior, instalado e autenticado localmente (`claude auth login`). No Windows, coloque o executável nativo `claude.exe` ou `claude.com` no `PATH`, conforme a política da infraestrutura de processos. A ausência da CLI, falhas de autenticação e outros erros de execução são retornados como `Failed`. Consulte `StandardOutput`, `StandardError` e `ErrorMessage` para obter os diagnósticos; falhas internas do Claude em modo `--print` podem ser emitidas em stdout. Um `CancellationToken` cancela a execução e encerra o processo.

## Telegram

Crie um bot com o BotFather e configure o token localmente pela variável de ambiente `Telegram__BotToken`. Por exemplo, no PowerShell: `$env:Telegram__BotToken = "<token>"`; no Bash: `export Telegram__BotToken="<token>"`. Não grave o token em arquivos versionados. Sem token, o Worker continua em execução com o polling desativado.

Com o Worker em execução, envie `/ping` ao bot. Ele responde `pong` no mesmo chat. O bot usa long polling e não requer webhook nem porta de entrada. Falhas de comunicação são registradas sem o token e o polling tenta novamente; ao encerrar o Worker, a requisição em andamento é cancelada. Os comandos de execução de agentes e a restrição de acesso serão adicionados nas próximas issues do MVP.
