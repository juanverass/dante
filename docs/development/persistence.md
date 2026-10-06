# Fundação EF Core + PostgreSQL (#168)

Domain/Application não conhecem EF, Npgsql, DbSet ou IQueryable. A porta permanece
`IRepository<TEntity>` com Guid Id. Na Infrastructure (#196):

| Pasta / namespace | Conteúdo |
| --- | --- |
| `Data` | `DanteDbContext` e a factory de design |
| `Data/Migrations` | migrations EF e snapshot |
| `Persistence` | `Repository<TEntity>`, `UnitOfWork` e a base `EntidadeConfiguration<T>` |
| `Modulos/<Modulo>` | mapping `<Entidade>DbMapping` e repository específico de cada entidade (#197) |
| `Banco` | administração explícita do PostgreSQL (`AdministracaoDoBanco`, `ComandosDoBanco`) |

Cada módulo tem o nome do módulo do Domain e namespace igual à pasta
(`Modulos/Conhecimentos` → `Dante.Infrastructure.Modulos.Conhecimentos`):
`EspacosDeConhecimento`, `Projetos`, `Conhecimentos`, `RelacoesDeConhecimento`,
`CapturaDeConhecimento`, `ContextosDeTrabalho` e `DocumentosFonte`. Filtros globais de
consulta continuam no `DanteDbContext`.

Adapters de consulta de uma feature (busca, auditoria, qualidade, fontes) ficam na
pasta da feature, como `QualidadeDoBrain`.

## Configuração e execução

Sem `ConnectionStrings:Dante`, `AddInfrastructure` não registra persistência e os
hosts continuam sem banco. Configure `ConnectionStrings__Dante` por ambiente ou
secret store externo; nunca grave a conexão no checkout. A variável é removida do
ambiente das CLIs, inclusive quando presente nos bindings do repositório.

Com configuração, contexto, repository genérico e UoW são scoped. Uma operação
compartilha contexto entre seus repositories; use escopo próprio em background
services. DbContext não é thread-safe. DI não conecta nem aplica migrations na
inicialização. Use conta runtime sem DDL e uma conta administrativa para migrations.
Não habilitamos logs de dados sensíveis nem retries automáticos de escritas.

## Migrations explícitas

Instale `dotnet-ef` 10.0.6 e configure a variável externa antes dos comandos:

```bash
dotnet ef migrations add NomeDaMudanca --project src/Dante.Infrastructure --output-dir Data/Migrations
dotnet ef migrations has-pending-model-changes --project src/Dante.Infrastructure
dotnet ef migrations script --idempotent --project src/Dante.Infrastructure
dotnet ef database update --project src/Dante.Infrastructure
```

A factory de design não inicia Worker/Telegram. Migrações publicadas são imutáveis;
novas mudanças geram novas migrations e snapshot. O histórico EF fica em
`brain_meta.__EFMigrationsHistory`. A migration inicial só prepara `brain_data` e
`brain_index`; ainda não há tabelas funcionais. Seu Down retira o registro da
migration e preserva os schemas compartilhados, sem apagar dados externos.
Não use EnsureCreated em produção nem migrations SQL manuais para schema comum.

## Novos mappings e repositories

Cada entidade tem uma `IEntityTypeConfiguration<T>` concreta em `Modulos/<Modulo>`,
com nome `<Entidade>DbMapping` (`EspacoDeConhecimentoDbMapping`, por exemplo), herdando
`EntidadeConfiguration<T>`; o repository específico fica no mesmo módulo, e um teste
de `PersistenciaTests` recusa mapping ou repository fora dele. Chame `base.Configure`, defina tabela/colunas em
snake_case PT-BR e restrições explícitas. Configurações são carregadas pelo assembly
do contexto. A base fixa chave `id` sem geração pelo banco e token shadow `Versao`
mapeado ao `xmin` do PostgreSQL. Nenhum campo técnico é adicionado ao Domain.

Repositories específicos herdam `Repository<TEntity>` e usam Context/DbSet
protegidos em suas queries; ports não expõem EF. Nenhuma entidade funcional Brain
é mapeada por esta entrega; isolamento, repositories específicos e persistência
funcional ficam na #160. O repository genérico não substitui autorização por escopo.

`ObterPorIdAsync` retorna entidade rastreada. Atualizar/remover exigem entidade
carregada no mesmo contexto; cópias desanexadas são recusadas porque não têm o
xmin original. `SalvarAlteracoesAsync` confirma todas as escritas pendentes numa
única transação automática. Conflito traduz-se em `ConflitoDeConcorrenciaException`
sem tipo/exception interna de EF. Falhas e conflitos exigem descartar o escopo e
recarregar antes de nova tentativa. Não há retry que sobrescreva dados concorrentes.
Transações adicionais podem ser compostas dentro de Infrastructure para futuros
casos de uso; não adicionamos uma porta de transação antecipada à Application.

## Validação PostgreSQL real

`PersistenciaTests` usa entidade fictícia só no assembly de testes e DDL gerado
pelo EF para testar CRUD, gravação adiada, transação/rollback, xmin e cancelamento.
Também aplica, reaplica, reverte e reaplica a migration de produção. Configure
`DANTE_TEST_POSTGRES` com conexão administrativa a uma instância **de teste** com
CREATE DATABASE. Cada execução cria/remove seu próprio banco aleatório, com pooling
desabilitado; sem variável o teste de integração é explicitamente pulado.

```bash
dotnet test Dante.sln --filter FullyQualifiedName~PersistenciaTests
```

Full-text e pgvector poderão ter configurações/índices separados em `brain_index`.
Não há extensão vector, embeddings, índice lexical ou dependência deles no canônico.
Documentação técnica: [transações EF](https://learn.microsoft.com/en-us/ef/core/saving/transactions)
e [xmin no Npgsql](https://www.npgsql.org/efcore/modeling/concurrency.html).
