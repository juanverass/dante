# D.A.N.T.E.

**Distributed Agent Network for Task Execution**

Base do serviço local em .NET 10. As integrações com agentes e Telegram serão adicionadas nas próximas etapas do MVP.

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

`IAgentProcessExecutor` oferece `IsAvailable(AgentKind)` e `ExecuteAsync(AgentProcessRequest, CancellationToken)`. O pedido informa o agente, um diretório de trabalho absoluto e uma lista de argumentos. A implementação resolve apenas os executáveis `codex` e `claude` no `PATH`; no Windows, procura binários nativos `.exe` ou `.com`. Scripts `.cmd` e `.bat` não são iniciados, pois exigiriam um shell. A infraestrutura é registrada no contêiner de serviços do Worker; os runners específicos de cada agente serão adicionados nas próximas issues.

O resultado contém stdout, stderr, código de saída, horários de início e fim e estado (`Succeeded`, `Failed` ou `Cancelled`). Uma CLI ausente ou uma falha ao iniciar o processo retorna `Failed` com uma mensagem de erro. O cancelamento encerra a árvore de processos iniciada.
