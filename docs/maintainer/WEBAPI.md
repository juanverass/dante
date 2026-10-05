# Host HTTP independente (#170)

`Dante.WebApi` é um adapter de entrada ASP.NET Core em .NET 10. Não referencia
Worker/Telegram, não inicia agentes e não precisa de bot/configuração de banco.
Compartilha `AddApplication()` e `AddInfrastructure(configuration)`; seu Program
registra apenas composição e pipeline HTTP.

## Rodar localmente

Na raiz do clone, com SDK .NET 10:

```bash
dotnet build Dante.sln
dotnet run --project src/Dante.WebApi -- --urls http://127.0.0.1:5080
curl -i http://127.0.0.1:5080/health
```

`GET /health` retorna 200 e `{"estado":"saudavel"}` quando o host está saudável.
É health **operacional do host**; não afirma disponibilidade de PostgreSQL, Brain,
Telegram ou autenticação das CLIs. Nesta entrega não há checks dessas dependências.
Checks registrados no futuro usam HealthCheckService: degradado retorna 200 com
`estado=degradado`, indisponível retorna 503 com `estado=indisponivel`. O resultado
não expõe mensagens internas de checks ou conteúdo de exceções.

Não é necessário iniciar Worker junto. A porta acima é exemplo local explícito;
`--urls`/ASPNETCORE_URLS e configuração padrão do ASP.NET Core controlam o bind.
O deploy systemd existente continua sendo somente do Worker.

## Erros e OpenAPI

Exceções inesperadas retornam 500 em ProblemDetails, com título PT-BR e traceId,
sem mensagem original/stack trace/SQL/segredos. `UseExceptionHandler` é usado em
todos os ambientes, inclusive Development. 404/405 e outros status sem corpo
passam por `UseStatusCodePages`/ProblemDetails; clientes devem aceitar JSON.
O contrato não transforma exceções de domínio em códigos de negócio nesta fundação;
essas traduções virão com os resultados/casos de uso específicos.

```bash
curl -i -H 'Accept: application/json' http://127.0.0.1:5080/nao-existe
```

OpenAPI JSON é exposto somente em **Development**, em `/openapi/v1.json`:

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project src/Dante.WebApi -- --urls http://127.0.0.1:5080
curl http://127.0.0.1:5080/openapi/v1.json
```

Production/Staging retornam 404 para essa rota. Documento inclui health e seus
contratos de resposta 200/503. Não há UI Swagger/Scalar nem endpoints funcionais
Brain nesta entrega. Pacotes novos: Microsoft.AspNetCore.OpenApi 10.0.6,
Microsoft.AspNetCore.Mvc.Testing 10.0.6 (testes) e Microsoft.OpenApi 2.7.6 fixado
para evitar a versão transitiva vulnerável ao
[GHSA-v5pm-xwqc-g5wc](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc).
Versões de pacotes existentes não foram alteradas.

## Estrutura para próximos endpoints

- Program: composição compartilhada e `UsarPipelineHttp`.
- `ConfiguracaoHttp`: serviços/pipeline e rota operacional de health.
- `TratamentoDeErros`: tradução neutra de falhas inesperadas, sem dados internos.
- DTO operacional `SaudeDto`: contrato próprio PT-BR, sem entidades de domínio/EF.

Endpoints funcionais novos recebem DTO → chamam AppService/caso de uso de
Application → retornam DTO/resultado. Não acessar repositories/DbContext no
endpoint. Regras não vivem na WebApi, e futuros mappings vêm de AddApplication,
sem duplicar configuração entre os hosts. DTOs/AppServices específicos continuam
PT-BR: ProjetoDto, ConhecimentoSearchDto, IEspacoDeConhecimentoAppService etc.
Health usa serviço operacional do ASP.NET Core; não é caso de uso de negócio.

## Verificar

```bash
dotnet test Dante.sln --filter 'FullyQualifiedName~WebApiTests'
```

Oito testes HTTP com WebApplicationFactory cobrem host independente, health/503,
404/405 padronizados, sanitização de exceção em Development e OpenAPI por ambiente.
Os testes arquiteturais verificam referências das camadas. Nenhum teste chama
Telegram, CLIs ou banco real para validar este host.

Referências primárias: [ProblemDetails](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling-api?view=aspnetcore-10.0),
[OpenAPI](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/aspnetcore-openapi?view=aspnetcore-10.0)
e [testes de integração](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0).
