# Busca do Brain (#154)

`BuscaDoBrainAppService` valida proprietário/projeto ativo e política #155. Full-text
PostgreSQL em português indexa conteúdo, JSON e tags; filtros precedem ranking/paginação:
espaço/projeto exatos, tipo/status/sensibilidade, tags sem distinção de caixa, criação e
validade. Busca retorna IDs, origem Conhecimento, scores e explicação mínima; um ID explícito
pode localizar metadados protegidos sem conteúdo/tags/fontes. Status inativo/substituído
não participa da recuperação operacional. Página de 1–100 e deslocamento até 10.000.

A migration EF cria `brain_index`, tsvector/GIN e representações derivadas. Trigger no
mesmo commit canônico atualiza texto/revisão, deixando revisão/modelo sem vetor como
trabalho pendente durável. Consulta sempre compara revisão atual; revisão antiga nunca
entra mesmo após interrupção. Busca exata pgvector por cosseno, sem HNSW/IVFFlat (recall
após filtros preservado), combinada com lexical por Reciprocal Rank Fusion. Índices
não alteram confirmação, identidade, proveniência ou conteúdo canônico.

Migrations e lexical funcionam sem pgvector. Administrador habilita explicitamente:

```sh
dotnet run --project src/Dante.Worker -- --brain search-enable-vector
dotnet run --project src/Dante.Worker -- --brain search-rebuild-lexical
dotnet run --project src/Dante.Worker -- --brain search-reindex ID_USUARIO ID_ESPACO ID_PROJETO
# Use - para projeto nulo. WebApi oferece os mesmos comandos.
```

`ReindexarAsync` processa até 100 pendências por chamada; repetir em nova scope até zerar.
Reconstruir=true remove apenas vetores do modelo/escopo escolhido antes de reconstruir,
mantendo lexical disponível. Mudança de provedor/modelo/versão/dimensão cria chave nova;
vetores antigos não são misturados, ficam disponíveis para inspeção/limpeza administrativa.
Revisão concorrente durante geração não é gravada como atual e permanece pendente.
Gerações válidas são ativadas por item/revisão, com cobertura gradual durante rebuild;
não há promessa de cobertura semântica total durante a construção.

Configure no host (fora do checkout/ambiente dos agentes):
DANTE_BRAIN_EMBEDDING_ENDPOINT (URL exata), MODEL, VERSION, DIMENSION, PROVIDER opcionais
com o mesmo prefixo; API_KEY opcional. API HTTP aceita input/model e devolve
`data[0].embedding`. Sem configuração completa/serviço/extensão, retorno é lexical.
Nenhum modelo é presumido a partir da assinatura Claude/Codex. Endpoint local HTTP permitido;
externo exige HTTPS e DANTE_BRAIN_EMBEDDING_ALLOW_EXTERNAL=true, sem redirects.
Secreto não é indexado; Confidencial só por gerador local autorizado. Timeout 20s, resposta
limitada a 512 KB, dimensão/finitude/norma verificadas; falhas de HTTP/JSON degradam para lexical.
Todas essas variáveis já são excluídas do ambiente das CLIs pelo prefixo DANTE_BRAIN.

Fontes brutas não são retornadas: DocumentosFonte/chunks ainda dependem da #158.
OrigemDoResultado reserva FonteBruta para essa integração, sem confundir com conhecimento.
Testes de semântica usam conceitos/vetores controlados para medir a recuperação pgvector
sem correspondência literal; qualidade do modelo real depende da configuração e avaliação
posterior. Referências: [full-text PostgreSQL](https://www.postgresql.org/docs/current/textsearch-controls.html)
e [pgvector](https://github.com/pgvector/pgvector).
