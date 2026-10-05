# AD-34 — Armazenamento e operação local do D.A.N.T.E. Brain

Status: decisão para implementação (#135, Epic #133); **não implementada no Worker**.
PostgreSQL foi escolhido pelo humano na Issue #135. Não reabrir a escolha de banco.
A #134 define domínio e fronteiras; este documento define a tradução física e os
requisitos de persistência para a #160, sem criar classes, migrations ou contratos
de domínio de produção. O Worker atual continua funcionando sem banco.

## Decisão e limites

PostgreSQL será a fonte de verdade estruturada. Full-text search nativo fornece
recuperação lexical; pgvector fornece um índice semântico derivado. Filesystem é
reservado a fontes/binários originais quando apropriado. Markdown + JSON oferecem
exportação independente do banco. Não haverá SQLite alternativo, banco vetorial
separado nem Markdown/filesystem como banco principal nesta Epic.

A implementação permanece local-first: banco e Worker no ambiente local suportado,
sem serviços cloud. Preparar isolamento e IDs não introduz organizações, SaaS,
autenticação web, workers remotos ou processos de execução durável.

## Canônico versus derivado

| Conteúdo | Autoridade | Persistência e recuperação |
| --- | --- | --- |
| Tenant/User e mapeamento de identidade | Canônico | PostgreSQL; ID do Telegram é associação, não chave física universal. |
| Knowledge Spaces e Projects | Canônico | PostgreSQL; Project referencia Space, identidade estável independente de paths/aliases. |
| Knowledge Items e revisões | Canônico | Conteúdo, tipo, dados estruturados, status, validade, autoria, tags e sensibilidade no PostgreSQL. |
| Knowledge Relations | Canônico | Endpoints, tipo, escopo e proveniência; integridade referencial sem grafo externo. |
| Proveniência, candidatos e decisões de consolidação | Canônico | Evidência e ator/revisão/timestamp persistidos; inferência não vira confirmação por indexação. |
| SourceDocument e metadados | Canônico | ID lógico, versão/hash, origem, classificação, storage reference e relações persistidos no banco. |
| Original externo | Canônico como evidência | Bytes no filesystem privado quando necessário; sua referência/hash permanece no PostgreSQL. |
| Working Context Snapshot | Canônico operacional | Revisão ativa, origem, tamanho/validade e escopo no banco; substituível, separado de conhecimento. |
| Chunks, tsvector/GIN e embeddings | Derivado | Reconstruídos de fontes/itens autorizados e suas revisões; não alteram verdade/status. |
| Resumos automáticos e Context Packs/cache | Derivado | Reconstruíveis, com referência de revisão/gerador; não substituem fonte ou confirmação. |
| Ledger de migrations | Metadado operacional canônico | Versão/checksum e histórico de aplicação para interpretar/restaurar o schema. |

Um KnowledgeItem do tipo Summary explicitamente consolidado é um item canônico,
com proveniência/status. Um resumo gerado para recuperação é derivado. Índices
não podem ser a única cópia de texto, evidência, metadados ou decisões.

Jobs, sessões, approvals e retries atuais continuam em memória; esta decisão não
migra `settings.json`/`repositories.json` nem persiste transcript inteiro.

## Organização física esperada

Separar responsabilidades em schemas:

- `brain_data`: dados canônicos estruturados, em tipos nativos portáveis (UUID,
  texto, timestamps UTC, valores numéricos e JSONB para extensões estruturadas).
- `brain_meta`: ledger de migrations, versão de formato e estado operacional de
  integridade/reindexação; não guardar credenciais.
- `brain_index`: chunks, representações lexicais/vetoriais, extensão vector e
  metadados de geração. Pode ser reconstruído sem alterar IDs/revisões canônicas.

Nomes físicos acima são direção para a #160, não tabelas disponíveis hoje.
Schemas canônicos não dependem de tipos, funções ou FKs da extensão vector ou de
`brain_index`. Assim é possível restaurar conteúdo sem semântica funcionando.
O adapter pode usar PostgreSQL/Npgsql; o domínio não expõe DbConnection, SQL,
JSONB, tsvector, vector ou exceções do provider.

IDs são gerados no lado do D.A.N.T.E. e preservados em export/restore. Nunca usar
sequence de banco, path físico ou embedding como identidade lógica. Mapear IDs
opacos a UUID no adapter não muda sua semântica no domínio.

Cada registro e query carrega TenantId + UserId + KnowledgeSpaceId e ProjectId
quando aplicável. FKs/uniqueness compostas validam que Project pertence ao Space
correto, fonte/revisão existe e endpoints de relações têm escopo compatível.
IDs globalmente únicos não substituem autorização. O adapter não oferece busca
sem filtro de escopo; RLS pode ser defesa adicional, não substituto da policy #150.
A revisão esperada produz conflito de concorrência, nunca last-write-wins silencioso.

## Full-text search

Usar mecanismos [nativos do PostgreSQL](https://www.postgresql.org/docs/17/textsearch.html):
`tsvector`, consulta parametrizada e ranking, com
[GIN para acesso lexical](https://www.postgresql.org/docs/17/textsearch-indexes.html).
Configuração/idioma e versão do indexador são registrados por representação;
`simple` serve como baseline previsível para texto misto/código, com configurações
linguísticas quando a língua estiver estabelecida. A #154 validará relevância em
português, código, identificadores e notas multilíngues.

O índice retorna IDs/revisões/score e proveniência, com autorização/status/validade
antes da seleção. A falta do índice GIN não elimina a informação: uma consulta
lexical sobre projeção válida pode continuar, com custo maior. Projeção desatualizada
não autoriza retornar conteúdo revogado: revalidar sempre o canônico.

## pgvector e ciclo de reindexação

A [extensão pgvector](https://github.com/pgvector/pgvector) mantém vetores no mesmo
PostgreSQL. Cada representação registra entidade/chunk e revisão/hash da origem,
modelo/provedor/versão, dimensão, normalização, métrica, versão do indexador e
instante/estado de geração. Alterar dimensão/modelo cria geração nova; não misturar
scores/vetores de espaços incompatíveis. Não fixar um modelo de embeddings nesta
Issue nem presumir que assinatura Claude/Codex oferece uma API de embeddings.
Classificação e autorização também precedem geração de embeddings: conteúdo
sensível não pode ser enviado a um provider externo apenas para criar o índice.

Busca exata pode servir ao conjunto inicial; HNSW/IVFFlat são otimizações a validar
na #154. Filtros de acesso devem integrar a consulta e revalidação do resultado;
limites/recall de busca aproximada após filtros precisam de testes, não garantia
por possuir um índice vetorial.

Gravação de item/revisão/proveniência/relações ocorre em transação canônica. No
mesmo commit, registrar trabalho de indexação recuperável (estado pendente/geração,
sem introduzir fila de execução de jobs). Após commit, gerar derivados; falha deixa
pendente/retry, sem rollback do conhecimento confirmado. Reinício retoma pendências.
Excluir/inativar/modificar item invalida versões derivadas e caches correspondentes.

Rebuild: capturar revisões elegíveis → construir geração nova → verificar hashes,
dimensões, escopo e progresso → ativar geração válida → remover antiga. Atualizações
concorrentes continuam pendentes e não são perdidas. Enquanto embeddings, extensão
ou gerador estiverem indisponíveis, informar modo lexical, sem corromper canônico.
Não prometer economia de tokens ou qualidade sem os testes #147/#148.

## Fontes no filesystem

Fonte tem ID lógico independente de caminho. O banco guarda origem, hash SHA-256,
tamanho, media type, revisão, sensibilidade e storage reference relativa a uma raiz
privada (direção inicial: `~/.dante/brain/sources`). O adapter resolve caminhos,
valida containment/symlinks e não expõe caminhos arbitrários ao agente.

Não reutilizar anexos temporários do Telegram como armazenamento permanente: eles
expiram. Copiar somente fontes explicitamente ingeridas/permitidas (#158), sem
transformar todo documento em fato. Conteúdo pequeno pode ficar no banco conforme
política de ingestão; o original externo faz parte do conjunto de backup.

A transação do banco não engloba filesystem. Para publicar fonte externa:

1. Escrever arquivo temporário na mesma raiz, calcular hash/tamanho e finalizar
   bytes imutáveis com rename atômico.
2. Commit de metadados/referências somente após o arquivo final existir.
3. Falha antes do commit pode deixar órfão, removível por varredura de integridade;
   nunca confirmar fonte cujo arquivo falhou. Bytes de revisão publicada não mudam.
4. Exclusão remove/invalida referências e derivados primeiro; limpeza de bytes
   depois do commit verifica se há outra referência autorizada. Integridade deve
   detectar bytes ausentes/corrompidos e reportar indisponibilidade, não inventar origem.

Backup congela também ingestão, edição e garbage collection; não basta o snapshot
transacional do PostgreSQL para obter consistência com arquivos externos.

## Migrations e requisitos de bootstrap da #160

Migrations explícitas, ordenadas e versionadas; scripts SQL físicos ficam no
adapter, não no domínio. O ledger guarda ID, checksum, versão aplicada e timestamp.
Uma migration aplicada é imutável; corrigir com nova migration. Separar migração
canônica de instalação/rebuild derivado, permitindo lexical sem vector.

- Bootstrap deve criar banco/roles/schemas apenas de forma explícita, com usuário
  privilegiado separado. Runtime não recebe superuser nem permissão de DDL.
- Runner de migrations adquire lock exclusivo, valida checksum/versão e executa
  mudanças transacionais quando suportadas. DDL incompatível com transação deve
  ter etapa explícita e recuperável; nunca avanço de versão falso após falha.
- Duas inicializações não aplicam a mesma migration concorrente. Schema mais novo
  que o adapter é incompatível: negar operações Brain, sem tentar downgrade automático.
- Upgrade exige backup anterior; preferir correção forward. Restaurar backup em
  instância nova é o caminho de reversão, não migration destrutiva automática.
- Health distingue canônico disponível/indisponível, schema compatível, integridade
  de fontes, lexical pronto e semântica disponível/pendente/indisponível. Semântica
  degradada não derruba gravação canônica ou lexical; falha canônica não confirma write.
- Configuração de conexão é opt-in, validada fora do repositório; credenciais de
  banco são somente do adapter/host e não são herdadas pelos processos dos
  agentes; a ausência de Brain
  não impede funcionalidades atuais. Nomes de settings/comandos/runner ficam para
  #160 e devem ser documentados quando existirem, sem comandos fictícios nesta ADR.

A versão SQL não é a versão do formato lógico de exportação, nem a versão do modelo
vetorial. São três dimensões distintas, todas verificáveis.

## Backup, restore e portabilidade

### Backup operacional

Para o MVP local, usar janela de manutenção: parar Worker e outros writers,
confirmar operações finalizadas, congelar GC/ingestão, criar dump custom do banco
canônico + cópia das fontes + manifesto. O
[`pg_dump`](https://www.postgresql.org/docs/17/app-pgdump.html) fornece snapshot
consistente do banco, não dos arquivos externos nem dos roles globais.

O manifesto inclui BackupId, instante UTC, versões de PostgreSQL/schema/formato,
ledger, hashes do dump e dos originais, contagens e referências lógicas. Proteger
backup privado com permissões restritas/criptografia conforme sensibilidade; não
logar conteúdo nem commitá-lo. Falha em uma etapa deixa conjunto incompleto, não
backup válido. Só retomar writers depois de concluir/validar o conjunto.

Dump canônico seleciona `brain_data` + `brain_meta` e exclui qualquer dependência
de vector/derivados. Essa restauração sem índice é critério da #160: schema canônico
usa somente tipos builtin ou inclui explicitamente suas dependências. Antes de
confirmar backup, inspecionar o TOC e validar que nenhuma extensão/tabela de índice
é necessária. Backup completo incluindo `brain_index` pode acelerar restauração,
mas não é requisito de recuperação do conhecimento.

Roles/credenciais são provisionados separadamente; não exportar hashes de senha
com `pg_dumpall` como parte do pacote portátil. Usar clientes compatíveis com a
major do servidor; não restaurar automaticamente em versão anterior. Troca de
major usa dump/restore e teste, nunca reutilização cega do volume físico.

### Restore

1. Usar ambiente/banco vazio separado; não sobrescrever instalação saudável.
2. Validar manifesto/hashes e versões; provisionar roles e schemas necessários.
3. Restaurar dump canônico com `pg_restore --exit-on-error --single-transaction`
   e sem herdar ownership/ACLs originais; aplicar grants explícitos do bootstrap.
4. Restaurar originais privados, remapear storage root sem mudar SourceDocumentId
   e conferir todos os hashes/referências. Fonte ausente é erro de integridade.
5. Validar schema/ledger, IDs, escopos, relações/proveniência e contagens. Não marcar
   pronto se o restore estiver parcial. Não executar migration nova antes de checar
   a versão restaurada e guardar a cópia do backup original.
6. Recriar `brain_index`, projeções lexicais e, quando disponível, embeddings.
   Migração de schema e rebuild são operações distintas. Executar health e consultas
   de autorização negativas antes de apontar o Worker para a instância restaurada.

Retenção e exclusão: apagar conteúdo no banco ativo não o apaga de backups antigos.
Definir retenção explícita e descarte seguro, e reaplicar exclusões posteriores ao
backup antes de servir um restore antigo. Export redigido não substitui backup.

### Export lógico

Markdown é leitura humana; JSON versionado preserva IDs, escopo, tipos/status,
validade, sensibilidade permitida, revisões, relações, proveniência e referências
lógicas/hashes de fontes. Export autorizado inclui originais permitidos ou declara
omissões; não contém connection string, credenciais nem índices reconstruíveis.
IDs/referências excluídos por policy não podem reaparecer como vazamento no grafo.

JSON não usa tipos PostgreSQL ou paths como identidade. Mudança de backend pode
usar export/import lógico sem remodelar o domínio. Import, implementação de export
(#149) e rebuild não são funcionalidades entregues nesta Issue.

## Operação local no WSL (roteiro preparatório)

O exemplo usa PostgreSQL **17** e pgvector **0.8.7**, uma combinação publicada pelo
upstream, sem tratá-la como mínimo do domínio. #160 deve fixar/validar versões e
registrar digest da imagem usada nas evidências. Docker é uma opção de operação,
não requisito arquitetural do Worker atual. Instalação nativa também pode usar o
[repositório PostgreSQL APT](https://www.postgresql.org/download/linux/ubuntu/)
e o pacote pgvector correspondente à major escolhida.

Para o caminho Docker no WSL, instale/inicie Docker Desktop e habilite integração
com a distro conforme a [documentação oficial](https://docs.docker.com/desktop/features/wsl/).
Confira `docker version` antes de continuar. Os comandos abaixo criam recursos de
banco locais; não foram executados nesta Issue, pois o daemon não está acessível
nesta distro. Não são uma instrução para instalar banco no ambiente atual do usuário.

### Criar instância local de preparação

Na raiz do clone, em Bash/WSL. O arquivo de bootstrap guarda somente credenciais
do banco e não é o `dante.env` do Worker:

```bash
umask 077
mkdir -p "$HOME/.config/dante"
python3 - <<'PY'
from pathlib import Path
import secrets
p = Path.home() / '.config/dante/brain-db.env'
with p.open('x') as file:
    file.write('POSTGRES_DB=dante_brain\nPOSTGRES_USER=brain_admin\n')
    file.write('POSTGRES_PASSWORD=' + secrets.token_hex(32) + '\n')
p.chmod(0o600)
PY
docker volume create dante-brain-pg17
docker run -d --name dante-brain-db --restart unless-stopped \
  --env-file "$HOME/.config/dante/brain-db.env" \
  -p 127.0.0.1:55432:5432 \
  --mount type=volume,source=dante-brain-pg17,target=/var/lib/postgresql/data \
  pgvector/pgvector:0.8.7-pg17
```

Criação exclusiva do arquivo impede sobrescrever senha existente. O
[entrypoint PostgreSQL](https://hub.docker.com/_/postgres) usa essas variáveis na
primeira inicialização do volume; mudar o arquivo não altera a senha de banco já
criado. Volume/nome são dedicados; não remover um volume com dados para "consertar"
credenciais. Secrets de ambiente podem ser vistos por administradores locais do
Docker: o MVP pressupõe host confiável. Bind só em loopback, sem exposição pública.

```bash
docker exec dante-brain-db pg_isready -U brain_admin -d dante_brain
docker exec -i dante-brain-db psql -X -v ON_ERROR_STOP=1 -U brain_admin -d dante_brain <<'SQL'
CREATE SCHEMA IF NOT EXISTS brain_data;
CREATE SCHEMA IF NOT EXISTS brain_meta;
CREATE SCHEMA IF NOT EXISTS brain_index;
CREATE EXTENSION IF NOT EXISTS vector WITH SCHEMA brain_index;
SELECT extname, extversion FROM pg_extension WHERE extname = 'vector';
SELECT to_tsvector('simple', 'incidente resolvido') @@ plainto_tsquery('simple', 'incidente') AS lexical_ok;
SQL
```

Espere readiness antes do SQL; se já existir vector em outro schema, verifique a
instalação em vez de supor que IF NOT EXISTS moveu a extensão. Estes schemas vazios
verificam somente ambiente, não migrations/persistência do Brain. `brain_admin` é
administrador de preparação; **não** conectar o Worker como esse usuário. A #160
provisionará role de migrations e role de runtime limitada, com login próprio,
revogação de CREATE em public e grants por schema/tabela. Conexão fica no host,
fora do Git/logs; nunca usar autenticação TCP trust como atalho.

### Backup/restore canônico: exemplo de comandos

Após a #160 criar schema/dados reais, parar writers e GC conforme a estratégia
acima. `brain-sources` neste exemplo é uma cópia privada da raiz das fontes:

```bash
umask 077
brain_backup_dir="$HOME/.dante/backups/brain-$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$brain_backup_dir"
docker exec dante-brain-db pg_dump -U brain_admin -d dante_brain \
  --format=custom --schema=brain_data --schema=brain_meta --no-acl \
  > "$brain_backup_dir/brain.dump"
docker exec -i dante-brain-db pg_restore --list < "$brain_backup_dir/brain.dump" \
  > "$brain_backup_dir/brain.toc"
cp -a "$HOME/.dante/brain/sources" "$brain_backup_dir/brain-sources"
sha256sum "$brain_backup_dir/brain.dump" > "$brain_backup_dir/brain.dump.sha256"
```

Exigir exit code zero em cada etapa e construir o manifesto de hashes/contagens
antes de marcar concluído. Se não houver fontes externas, registrar isso no
manifesto, sem criar uma cópia fictícia. `brain.dump.sha256` sozinho não é o
manifesto completo nem prova restaurabilidade. A #160 entregará rotina/testes
para evitar marcar sucesso após falha ou omitir fonte.

Criar banco de ensaio vazio e restaurar sem apagar o original:

```bash
docker exec dante-brain-db createdb -U brain_admin -T template0 dante_brain_restore
docker exec -i dante-brain-db pg_restore -U brain_admin -d dante_brain_restore \
  --exit-on-error --single-transaction --no-owner --no-acl \
  < "$brain_backup_dir/brain.dump"
```

Reaplicar grants e a rotina de integrity/rebuild da #160 nesse banco, restaurar e
validar os originais em raiz separada e comparar conteúdo/IDs antes da troca.
`pg_restore --list` apenas lista objetos, não é um teste de restore. Nenhum comando
de rebuild do produto existe ainda. Não usar o banco de ensaio como destino de
runtime privilegiado.

Para operação: `docker stop dante-brain-db`, `docker start dante-brain-db` e
`docker logs --tail 30 dante-brain-db`. Não colar logs/inspeções com credenciais.
Subir container não equivale a ter integração Brain; mudanças na configuração do
banco devem ser refletidas no bootstrap/ambiente que a #160 implementará.

## Contrato de entrega e testes obrigatórios da #160

| Operação do adapter | Garantia a verificar |
| --- | --- |
| Bootstrap/health | Setup explícito local, versão compatível, runtime com privilégio mínimo, status separado de vector. |
| Scoped read/write | Identidade/escopo obrigatório e parâmetros SQL; IDs estáveis; escopo ausente ou cruzado rejeitado. |
| Commit de revisão | Transação de conteúdo/proveniência/relações/pendência de índice; revisão esperada e conflito tipado. |
| Persistência de fontes | Finalização/rename antes de commit, hash validado, órfão recuperável, ausência reportada. |
| Migrate | Ledger/checksum e lock; migration repetida, falha parcial, concorrência e schema futuro testados. |
| Rebuild derivado | Canônico não muda; alterações concorrentes não se perdem; lexical funciona sem embeddings/vector. |
| Backup/restore | Dump + fontes + manifesto; restauração em banco vazio, integridade/IDs/escopos; restaurar sem índice vetorial. |
| Configuração | Connection strings e senhas fora do Git/logs/Telegram; Brain opt-in sem quebrar o Worker atual. |

Não criar entidades de domínio vazias ou schema completo de Knowledge Core antes
das respectivas Issues. A #160 deve traduzir estes requisitos e os contratos da
#134 em interfaces/infraestrutura mínimas e evidência PostgreSQL real. Nenhum
ORM/versão de pacote é adicionado por esta ADR. RLS, modelo de embeddings e tuning
aproximado não são requisitos de bootstrap e devem respeitar suas Issues futuras.

## Referências técnicas

- [PostgreSQL — full-text search](https://www.postgresql.org/docs/17/textsearch.html).
- [PostgreSQL — pg_dump](https://www.postgresql.org/docs/17/app-pgdump.html) e
  [pg_restore](https://www.postgresql.org/docs/17/app-pgrestore.html).
- [pgvector — instalação e busca](https://github.com/pgvector/pgvector).
- [Imagem PostgreSQL](https://hub.docker.com/_/postgres) e
  [Docker Desktop no WSL](https://docs.docker.com/desktop/features/wsl/).
