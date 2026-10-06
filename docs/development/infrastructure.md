# Organização da Infrastructure

Guia da Epic #195 (#196–#201). `Dante.Infrastructure` implementa as portas do
núcleo; Domain/Application não dependem de EF Core/Npgsql. Worker/WebApi usam
`AddApplication().AddInfrastructure(configuration)` e o dispatcher local
`ComandosDoBrain`, sem declarar schema, repositories ou migrations.

| Diretório | Responsabilidade |
| --- | --- |
| `Data/` | DbContext, factory de design, filtros de leitura e validação de escrita |
| `Data/Migrations/` | migrations EF e snapshot, no assembly da Infrastructure |
| `Modulos/<Modulo>/` | `<Entidade>DbMapping` e `<Entidade>Repository` específicos |
| `Persistence/` | `Repository<T>`, `UnitOfWork`, `EntidadeConfiguration<T>` |
| `Banco/` | migrate, health, backup e restore explícitos |
| `Brain/` | despacho da interface local `--brain`, inclusive operações funcionais |
| `Composicao/` | registros internos de banco, Brain, contexto e agentes |
| pastas de features | adapters de busca, fontes, auditoria, qualidade e métricas |
| `Contextos/`, `Agentes/`, `Uso/` | adapters locais do legado |

Não existe projeto Migrator separado. Startup normal não aplica migrations.
`DependencyInjection.AddInfrastructure` é a única entrada pública da composição;
sem conexão, os adapters locais funcionam e a persistência não é registrada.

## Adicionar persistência de uma entidade

1. Declare a entidade no módulo do Domain e a porta específica na feature da
   Application, seguindo Guid Id/PT-BR e os contratos de domínio existentes.
2. Crie `Modulos/<Modulo>/<Entidade>DbMapping.cs`, com namespace igual à pasta.
   Herde `EntidadeConfiguration<Entidade>`, chame `base.Configure` e defina tabela,
   colunas, limites e FKs explícitos. O módulo tem o mesmo nome do módulo do Domain.
   A base preserva Id gerado pelo domínio e token shadow `Versao`/`xmin`.
3. Crie `<Entidade>Repository.cs` no mesmo módulo, herdando `Repository<Entidade>`
   e implementando a porta da Application. Não exponha EF/IQueryable pela porta.
4. Registre o adapter scoped na composição do Brain, usando o mesmo DbContext/UoW.
   Para entidades Brain, estenda `Data/FiltrosDoBrain` e
   `Data/ValidacaoDeEscritaDoBrain` antes de expor acesso. Uma entidade nova não
   recebe isolamento automaticamente apenas por ter mapping.
5. Gere a migration e confira seu SQL/snapshot. Não altere migrations publicadas.
   Ajuste os testes que enumeram entidades/migrations para refletir a nova entrega.

## Gerar e validar migrations

Configure `ConnectionStrings__Dante` externamente, também para a factory de design.
Não passe credenciais como argumento nem grave conexão no checkout. Use a versão
`dotnet-ef` alinhada ao EF do projeto, conforme [persistência](persistence.md).

```bash
dotnet ef migrations add NomeDaMudanca --project src/Dante.Infrastructure --output-dir Data/Migrations
dotnet ef migrations list --project src/Dante.Infrastructure
dotnet ef migrations has-pending-model-changes --project src/Dante.Infrastructure
dotnet ef migrations script --idempotent --project src/Dante.Infrastructure
dotnet build Dante.sln
dotnet test Dante.sln
```

Histórico: `brain_meta.__EFMigrationsHistory`; dados: `brain_data`; derivados:
`brain_index`. Migrations continuam sendo descobertas pelos IDs originais.

## Operação administrativa

Com a conta administrativa configurada externamente:

```bash
dotnet run --project src/Dante.Worker -- --brain migrate
dotnet run --project src/Dante.Worker -- --brain backup /caminho/fora-do-checkout/brain.dump
dotnet run --project src/Dante.WebApi -- --brain restore /caminho/fora-do-checkout/brain.dump
```

Backup recusa sobrescrita; restore exige outro banco vazio. Guarde também as fontes
brutas locais conforme [operação local](../brain/LOCAL_STORAGE.md). Troque para a
conta runtime com grants mínimos e execute:

```bash
dotnet run --project src/Dante.WebApi -- --brain health
```

Health recusa migration pendente, histórico desconhecido ou conta com privilégios
administrativos. Comandos retornam 0 no sucesso e 1 na falha, com mensagem genérica
sem exception/credenciais. Os hosts encerram antes de iniciar Telegram/HTTP.

## Proteções e validação real

`InfrastructureArchitectureTests` protege os hosts, a localização de migrations e
a única chamada produtiva a migrate; `HexagonalArchitectureTests` protege as
referências do núcleo. `PersistenciaTests` cobre localização de mappings/repositories,
drift do modelo, descoberta/aplicação/reaplicação de migrations, UoW e concorrência.
`AutorizacaoDoBrainTests` verifica filtros e sensibilidade; `BrainEfTests` valida
backup/restore e os dois hosts; `AdministracaoDoBancoTests` cobre health com migration
pendente/histórico desconhecido e falha sem credenciais.

Defina `DANTE_TEST_POSTGRES` externamente com conexão administrativa para servidor
exclusivo de testes em UTF-8, com CREATE DATABASE/CREATE ROLE, e disponibilize `pg_dump` e
`pg_restore` compatíveis no PATH. Cada fixture cria/remove seu próprio banco aleatório.
A autenticação precisa permitir os roles temporários de runtime usados pelos testes.
Nunca use o banco canônico como servidor de testes. Então execute a suíte completa:

```bash
dotnet test Dante.sln
```

Sem a variável, os testes reais são pulados explicitamente; isso não comprova a
aplicação das migrations ou backup/restore. Não habilite as provas com CLIs reais
(`DANTE_LIVE_CLI`) para validar persistência.
