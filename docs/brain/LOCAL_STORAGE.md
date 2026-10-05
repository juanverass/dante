# Persistência local do Brain (#160)

A persistência opcional usa EF Core + PostgreSQL em Dante.Infrastructure. Domain define
EspacoDeConhecimento, Projeto e Conhecimento; Application define repositories e AppServices.
Não existem BrainRecord, migrations SQL manuais ou identidade Telegram no storage.

Configure `ConnectionStrings__Dante` fora do checkout. Sem ela, o Worker continua sem banco.
AddInfrastructure registra DbContext, UnitOfWork, repositories e AppServices scoped; AddApplication
registra o mapper compartilhado. O mesmo contexto coordena a transação por SaveChanges.
O runtime não migra automaticamente. Os stores JSON legados e as sessões não são migrados.

## Preparar e operar

Use PostgreSQL 17+ e clientes pg_dump/pg_restore da mesma versão major do servidor no PATH.
A conta migradora deve ser proprietária do banco de destino e criar schemas/tabelas, sem ser
superuser. Execute com essa conta (em ConnectionStrings__Dante):

```bash
dotnet run --project src/Dante.Worker -- --brain migrate
```

Crie uma conta runtime LOGIN sem SUPERUSER, CREATEDB, CREATEROLE ou BYPASSRLS. Após migrar,
com a conta administrativa, conceda (substitua `dante_runtime` pela conta escolhida):

```sql
GRANT USAGE ON SCHEMA brain_data, brain_meta TO dante_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA brain_data TO dante_runtime;
GRANT SELECT ON ALL TABLES IN SCHEMA brain_meta TO dante_runtime;
```

Não conceda CREATE no schema, ownership ou membership na conta migradora. Reaplique os grants
após novas migrations/restore. Configure o runtime com essa conta e verifique:

```bash
dotnet run --project src/Dante.Worker -- --brain health
```

Retorno 0 indica conexão, migrations atuais e ausência dos privilégios elevados verificados;
1 indica falha. Health não substitui a revisão administrativa de grants/membership. WebApi aceita
os mesmos comandos e encerra sem servir HTTP. Nenhum comando imprime exceptions do provider.
Credenciais ConnectionStrings__Dante, DANTE_BRAIN* e PG* são removidas do ambiente dos agentes,
inclusive overrides explícitos. Nunca vincule credenciais do banco como ambiente de repositório.

## Modelo e integridade

Schemas brain_data/brain_meta, tabelas espacos_de_conhecimento/projetos/conhecimentos. Guid Id
preservado, FKs com delete Restrict; FK composta impede conhecimento ligado a projeto de outro
espaço. Conteúdo e classificações têm colunas próprias; tags usam text[], dados estruturados e
histórico imutável do agregado usam JSONB. Histórico e estado atual são gravados na mesma transação.
Proveniência, autoria, revisões e substituição sobrevivem ao restart. xmin shadow impede atualização
concorrente silenciosa e UnitOfWork traduz conflitos para ConflitoDeConcorrenciaException.
Recarregue em novo scope antes de resolver conflito; não reutilize o contexto que falhou.

Listagens filtram usuário/espaço/projeto antes de ordenar e limitar. IDs opacos não são autorização:
a policy fail-closed é a #150. AppServices validam espaço/projeto ativo. Não expor os repositories
ou CRUD por HTTP/Telegram sem a policy. Não há full-text, pgvector ou busca semântica nesta entrega.

## Backup e restore

Pare writers antes do backup. O dump custom do PostgreSQL inclui as entidades, revisões e migrations,
sem owners/ACLs. Não há fontes em filesystem nesta issue; o adapter de originais é da #158.

```bash
dotnet run --project src/Dante.Worker -- --brain backup /caminho/privado/brain.dump
dotnet run --project src/Dante.Worker -- --brain restore /caminho/privado/brain.dump
```

Backup recusa arquivo existente e publica por rename após sucesso; arquivo temporário é privado
0600 no Linux. Windows exige diretório com ACL privada. Restore usa transação única e exige **banco
vazio separado**, escolhido por ConnectionStrings__Dante. Somente backups confiáveis: dumps podem
conter SQL executável. Não restaurar por cima do banco ativo. Reaplique grants, health e compare
contagem/conteúdo antes de ativar o destino. Índices futuros são reconstruíveis; backup canônico
não depende de pgvector. Cancelamento mata o processo cliente; stderr não expõe dados/credenciais.

## Validação

```bash
DANTE_TEST_POSTGRES='Host=127.0.0.1;Username=conta_teste;Database=postgres' dotnet test Dante.sln
```

Use servidor exclusivo de testes e conta com CREATE DATABASE/ROLE. Cada teste cria e remove banco
aleatório; backup/restore exige clientes no PATH. Sem a variável, testes PostgreSQL são explicitamente
ignorados. A suíte verifica migrations idempotentes, rollback, concorrência, restart/histórico,
FKs/escopos, runtime limitado e restauração em destino separado. Nunca usar conta de produção.
