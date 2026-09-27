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
