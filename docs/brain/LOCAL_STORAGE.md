# Persistência local do Brain (#160)

A fundação física da [AD-34](POSTGRESQL_STORAGE.md) está implementada. O Worker
continua operando sem banco por padrão. Habilitar o módulo registra `IBrainStorage`
e `IBrainDerivedIndex` no monólito; não adiciona comandos Telegram, ingestão,
Knowledge Core, autorização de produto, embeddings ou integração aos agentes.

## Contratos e limites

`BrainRecord : EntityBase` é um envelope de persistência útil para conteúdo JSON,
texto, proveniência, revisão e original opcional; não é a entidade semântica da
#138. `EntityBase.Id` identifica o registro de acordo com `Kind`: KnowledgeItem,
SourceDocument ou WorkingContext. Tenant/User/Space/Project são âncoras mínimas de
integridade. As propriedades usam **Id no início**: `IdTenant`, `IdUser`,
`IdKnowledgeSpace`, `IdProject`, `IdFrom`, `IdTo`, `IdBackup`. IDs são UUIDs gerados
no D.A.N.T.E., preservados após restart/restore e independentes do PostgreSQL.

`ResolveIdentityAsync` mantém associação idempotente de usuário Telegram a IDs
locais; o tenant local é estável. Isso não autoriza o usuário. A integração com
allowlist/policy e seleção de Space/Project continua nas respectivas issues.
`RegisterScopeAsync` é uma operação explícita para chamadores confiáveis; não
reatribui ownership de IDs existentes. Leituras/escritas sempre recebem escopo
completo, incluindo Project ou ausência explícita dele. FKs compostas impedem
referências cruzadas; não há busca global ou fallback entre Projects. A camada
física não substitui a autorização da #150.

`CommitAsync` exige revisão esperada (zero para criação), grava conteúdo,
proveniência, histórico, links e pendência de índice na mesma transação. Conflito
é tipado; link inválido faz rollback completo. Links de saída são substituídos
pelo conjunto enviado. Exclusão elimina registro/histórico/links e derivados;
bytes externos permanecem órfãos, sem apagar outra referência. Os contratos
não expõem Npgsql/SQL; erros do provider são convertidos em mensagens neutras,
sem connection string, SQL, payload ou credenciais.

Originais são publicados via stream em raiz privada, finalizados antes do commit
e validados por SHA-256/tamanho. Referências só aceitam nomes gerados pelo adapter;
traversal e symlinks são rejeitados. Health verifica originais de todas as revisões
e conta órfãos. Órfão não é perda de conteúdo confirmado. Para limpeza, parar todos
os writers e verificar referências de **todas as revisões** antes de remover bytes;
não há garbage collector automático nesta entrega.

Migrations canônicas são SQL embutido, com ledger/checksum e lock transacional.
Repetição é idempotente; checksum alterado/schema futuro são recusados. Runner e
runtime exigem PostgreSQL **17**. A versão lógica do backup é **1**, independente
da migration **1**. Npgsql **10.0.3** é o único pacote novo; versões existentes
não foram alteradas.

Rebuild lexical é uma operação administrativa em transação e com lock exclusivo
contra writers. Recria `brain_index.lexical`/GIN de conteúdo canônico, com revisão,
configuração `simple` e versão de indexador 1; atualização concorrente fica pendente
ou entra no rebuild, sem ser perdida. `brain_index` não é necessário para gravar
canônico. Pendências sobrevivem a restart e são processadas pelo rebuild explícito;
não há scheduler nesta fundação. Não há busca de produto ainda (#154).
pgvector é opcional e não constitui índice pronto apenas por estar instalado;
`SemanticAvailable` permanece false enquanto não houver gerador/representações.

## Instância e bootstrap explícitos (Bash/WSL)

Instale servidor e clientes PostgreSQL 17. Pode usar o container do
[roteiro da AD-34](POSTGRESQL_STORAGE.md#criar-instância-local-de-preparação),
com clientes 17 acessíveis no WSL; esses comandos não instalam banco ao iniciar o
Worker. A validação desta entrega usou PostgreSQL **17.11**, clientes 17 e uma
instância temporária nativa em loopback; o container Docker não foi exercitado.

Use instância dedicada e autenticada. Não coloque senhas em argumentos de shell,
Git ou logs. Os exemplos usam `read -s`; `.pgpass` privado (0600) também pode
fornecer a autenticação administrativa. Trocar de ambiente não autoriza alterar
um banco existente ou apagar volume. Execute da raiz do clone:

```bash
export PGHOST=127.0.0.1 PGPORT=55432 PGDATABASE=postgres PGUSER=brain_admin
read -r -s -p 'Senha administrativa: ' PGPASSWORD; echo
export PGPASSWORD
read -r -s -p 'Senha nova de migrations: ' BRAIN_MIGRATOR_PASSWORD; echo
export BRAIN_MIGRATOR_PASSWORD
read -r -s -p 'Senha nova de runtime: ' BRAIN_RUNTIME_PASSWORD; echo
export BRAIN_RUNTIME_PASSWORD
psql -X -v ON_ERROR_STOP=1 -f deploy/brain/bootstrap.sql
unset PGPASSWORD BRAIN_MIGRATOR_PASSWORD BRAIN_RUNTIME_PASSWORD
```

O script cria `dante_brain`, `brain_migrator` e `brain_runtime`, restringe acesso
público e configura privilégios padrão. Senhas distintas com pelo menos 16 caracteres são definidas explicitamente;
nova execução também é rotação dessas contas dedicadas. O migrator é dono de
banco/schemas, sem superuser/CREATEDB/CREATEROLE; runtime não tem DDL nem UPDATE
no ledger. Schemas de preparação já existentes precisam pertencer ao migrator;
IF NOT EXISTS não transfere ownership. Não aplicar esse script casualmente em
cluster compartilhado ou sobre roles de outro produto.

Se desejar pgvector, administrador instala a extensão no schema `brain_index`
com pacote compatível com PostgreSQL 17. Isso é separado das migrations canônicas:

```sql
CREATE EXTENSION IF NOT EXISTS vector WITH SCHEMA brain_index;
```

O runtime valida seus privilégios antes de operar; conectar como administrador ou
migrator é recusado. CLI administrativa usa configuração separada e não inicia
Telegram/hosted services. Configuração opt-in:

| Setting/env | Uso |
| --- | --- |
| `Brain__Enabled=true` | Registra o módulo no Worker; padrão ausente/false. |
| `Brain__ConnectionString` | Conta `brain_runtime`, somente runtime e health. |
| `Brain__MigrationConnectionString` | Conta `brain_migrator`, somente CLI administrativa. Não colocar no ambiente permanente do Worker. |
| `Brain__SourcesRoot` | Caminho absoluto privado; padrão `~/.dante/brain/sources`. |
| `Brain__ToolsDirectory` | Diretório absoluto dos clientes `pg_dump`/`pg_restore` 17; ausente usa PATH. |

Connection strings têm formato Npgsql: Host/Port/Database/Username/Password.
Guarde-as em configuração privada do host, com leitura apenas pelo usuário de
serviço. Para senha arbitrária com delimitadores, use quoting Npgsql apropriado;
não construir connection strings por concatenação de texto de usuário.
Para uma sessão administrativa interativa, ler uma connection string inteira
não a coloca no histórico nem na linha de argumentos do processo:

```bash
read -r -s -p 'Conexão migrations: ' Brain__MigrationConnectionString; echo
export Brain__MigrationConnectionString
read -r -s -p 'Conexão runtime: ' Brain__ConnectionString; echo
export Brain__ConnectionString
dotnet run --project src/Dante.Worker -- --brain migrate
```

Depois de migrate, aplicar grants com o usuário de migrations (via `.pgpass` ou
`PGPASSWORD` lido privadamente). A allowlist de ambiente do serviço não exige
credenciais administrativas persistentes; para systemd/WSL, use EnvironmentFile
privado conforme o deploy existente. Toda variável `Brain__*`, `Brain:*` e `PG*`
é removida do ambiente dos processos Claude/Codex, inclusive de overrides por
repositório. Não cadastrar credenciais de banco no ambiente de um repositório.

```bash
export PGHOST=127.0.0.1 PGPORT=55432 PGDATABASE=dante_brain PGUSER=brain_migrator
read -r -s -p 'Senha migrations: ' PGPASSWORD; echo
export PGPASSWORD
psql -X -v ON_ERROR_STOP=1 -f deploy/brain/runtime-grants.sql
unset PGPASSWORD
dotnet run --project src/Dante.Worker -- --brain health
dotnet run --project src/Dante.Worker -- --brain rebuild
```

Health JSON distingue disponibilidade canônica, compatibilidade do schema,
integridade das fontes, lexical pronto, semântica disponível, pendências e órfãos.
Falha canônica/schema/fontes retorna exit code 1. Índice pendente/semântica ausente
são degradação, sem derrubar o canônico; health 0 não garante embeddings ou relevância.
Não há migration automática no startup. Fazer backup antes de upgrade.

## Backup e restore mínimo

Parar Worker e **todos** os outros writers/ingestão/GC antes da operação. Os
adapters obedecem a lock de manutenção, mas ferramentas SQL externas não obedecem
automaticamente. Backup usa `pg_dump` custom apenas `brain_data` + `brain_meta`,
valida originais de todas as revisões, copia referências e escreve manifesto
com IdBackup/timestamp/versões/ledger/hashes/contagens. Manifesto é marcador de
conclusão; falha deixa diretório parcial sem um manifesto válido. Tratar arquivos
como privados e aplicar retenção/criptografia conforme sensibilidade.

```bash
dotnet run --project src/Dante.Worker -- --brain backup "$HOME/.dante/backups/brain-ensaio"
```

Destino deve ser absoluto e novo. Um diretório existente é recusado. A senha dos
clientes PostgreSQL vai somente no ambiente isolado do subprocesso, sem argumentos
ou logs; administradores do host podem inspecionar esse ambiente.

Restore exige **outro banco vazio**, pertencente a `brain_migrator`, e raiz de
fontes vazia separada. Criar como administrador sem mexer no original:

```bash
# Com PGUSER/PGPASSWORD administrativos configurados privadamente:
createdb -T template0 -O brain_migrator dante_brain_restore
```

Mudar `Brain__MigrationConnectionString` e `Brain__ConnectionString` para esse
banco e `Brain__SourcesRoot` para um diretório novo; manter o original parado e
preservado. A CLI valida manifesto, SHA-256, versão e originais antes de restaurar
com `--single-transaction --exit-on-error --no-owner --no-acl`. Compara ledger,
contagens e referências restauradas. Não executa upgrade automático. O dump não
precisa de pgvector/`brain_index`; rebuild ocorre depois:

```bash
dotnet run --project src/Dante.Worker -- --brain restore "$HOME/.dante/backups/brain-ensaio"
dotnet run --project src/Dante.Worker -- --brain rebuild
# Configurar PGDATABASE=dante_brain_restore, PGUSER=brain_migrator e senha privada:
psql -X -v ON_ERROR_STOP=1 -f deploy/brain/runtime-grants.sql
dotnet run --project src/Dante.Worker -- --brain health
unset Brain__MigrationConnectionString
```

Trocar o Worker somente após sucesso de restore/rebuild/grants/health e consultas
negativas de escopo na integração. Falha durante cópia posterior ao restore pode
deixar banco preenchido/arquivos parciais: essa instalação não está pronta; usar
novo destino de ensaio, sem apagar o original. Restore é operação administrativa
sobre backup confiável, não importação de SQL arbitrário de terceiros.
Exclusões posteriores ao backup devem ser reaplicadas antes de servir um backup
antigo. Export lógico autorizado Markdown/JSON permanece na #149.

## Validação reproduzível

```bash
dotnet build Dante.sln
dotnet test Dante.sln
# Somente em servidor PostgreSQL 17 exclusivo de testes, com credenciais privadas:
export DANTE_BRAIN_TEST_CONNECTION
# Opcional: diretório absoluto dos clientes 17, caso não estejam no PATH.
export DANTE_BRAIN_TEST_TOOLS
dotnet test Dante.sln --filter 'FullyQualifiedName~Brain'
```

`DANTE_BRAIN_TEST_CONNECTION` aponta para banco administrativo do cluster de
**testes**; a conta precisa criar bancos e roles. Os testes criam/removem apenas
bancos/role de prefixo aleatório `brain_test_*`. Nunca usar cluster de produção.
Sem a configuração, seis testes PostgreSQL são explicitamente ignorados; os
cinco testes de contratos/arquivos/configuração continuam rodando. A suíte real
cobre FKs, revisão/rollback, isolamento, restart, perda/rebuild de índices,
concorrência, migration/checksum/schema futuro, privilégios, originais e
backup/restore sem índice. Bootstrap e CLI também foram exercitados com contas
separadas em TCP/SCRAM no cluster temporário, sem Telegram ou CLIs dos agentes.
