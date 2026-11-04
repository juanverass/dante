# Busca do Brain (#154)

`BuscaDoBrainAppService` valida proprietário/projeto ativo e política #155. Full-text
PostgreSQL em português indexa conteúdo, JSON e tags; filtros precedem ranking/paginação:
espaço/projeto exatos, tipo/status/sensibilidade, tags sem distinção de caixa, criação e
validade. Busca retorna IDs, origem Conhecimento, scores e explicação mínima; um ID explícito
pode localizar metadados protegidos sem conteúdo/tags/fontes. Status inativo/substituído
não participa da recuperação operacional. Página de 1–100 e deslocamento até 10.000.

A migration EF cria `brain_index`, tsvector/GIN e representações derivadas. Trigger no
mesmo commit canônico atualiza texto/revisão somente para itens não Secret; reclassificação
para Secreto apaga entradas lexicais e representações vetoriais existentes. A migration
ExcluirSegredosDosIndices corrige instalações anteriores sem modificar migrations já
aplicadas nem o conhecimento canônico; desfazê-la não reinstala o trigger inseguro.
Rebuild lexical limpa resíduos Secret e só reconstrói itens permitidos. Locks compartilhados
no canônico serializam rebuild/gravação de vetor com reclassificação, evitando reinserção
concorrente de conteúdo protegido. Revisão/modelo sem vetor permanece como
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

## Provedor local: Ollama + bge-m3

Provedor avaliado na #258 ([relatório](../brain/avaliacao-busca-hibrida/README.md)):
Ollama em processo de usuário, sem unit systemd, com o modelo `bge-m3` (1024 dimensões).
O conteúdo não sai do host: o endpoint é loopback, então `Externo = false`.

```sh
# Release oficial (github.com/ollama/ollama); confira o SHA-256 com o sha256sum.txt da release.
tar --zstd -xf ollama-linux-amd64.tar.zst -C ~/.local/opt/ollama-<versão>
ln -sfn ~/.local/opt/ollama-<versão>/bin/ollama ~/.local/bin/ollama
OLLAMA_HOST=127.0.0.1:11434 ollama serve      # p. ex. numa sessão screen destacada
ollama pull bge-m3 && ollama ps               # PROCESSOR deve indicar 100% GPU
```

O tarball traz o runtime CUDA (cudart/cuBLAS 12 e 13) e precisa só do driver NVIDIA. O
toolkit CUDA não é necessário. Configuração equivalente no host:

```text
DANTE_BRAIN_EMBEDDING_ENDPOINT=http://127.0.0.1:11434/v1/embeddings
DANTE_BRAIN_EMBEDDING_MODEL=bge-m3
DANTE_BRAIN_EMBEDDING_VERSION=<digest curto do modelo em `ollama list`>
DANTE_BRAIN_EMBEDDING_DIMENSION=1024
DANTE_BRAIN_EMBEDDING_PROVIDER=ollama
DANTE_BRAIN_EMBEDDING_MIN_SIMILARITY=<limiar calibrado para este modelo, ex.: 0.52>
DANTE_BRAIN_EMBEDDING_MIN_SIMILARITY_MODEL=<provedor|nome|versão|dimensão em que foi calibrado, ex.: ollama|bge-m3|790764642607|1024>
```

Com o endpoint indisponível, sem modelo, sem coluna `vetor` ou com dimensão divergente, a busca
continua lexical sem erro (`BuscaDoBrainTests`). O harness `ConsultasDeReferenciaDaBuscaTests`
repete a medição só num banco descartável (`DANTE_TEST_POSTGRES` e `DANTE_TEST_EMBEDDING_*`).

## Corte de relevância (#280, AD-62)

- **Limiar obrigatório e vinculado ao modelo**: `DANTE_BRAIN_EMBEDDING_MIN_SIMILARITY` é a
  similaridade cosseno mínima, em cultura invariante e na faixa aberta (0, 1).
  `DANTE_BRAIN_EMBEDDING_MIN_SIMILARITY_MODEL` identifica o modelo em que ela foi calibrada, como
  `provedor|nome|versão|dimensão`, comparado campo a campo com `PROVIDER` (padrão `http`), `MODEL`,
  `VERSION` e `DIMENSION`. Se o limiar estiver ausente, inválido ou vinculado a outro modelo, por
  exemplo depois de trocar a versão sem recalibrar, a consulta não gera embedding e a busca é
  lexical (fail-closed). A indexação de vetores não depende do limiar.
- **Corte antes da fusão**: no SQL, só entra no ranking semântico quem atinge o limiar. Cada lista é
  ranqueada entre os aprovados antes do RRF e da paginação. Sem resultado lexical e sem nada acima do
  limiar, a busca retorna vazio.
- **Lexical**:
  - a correspondência com todos os termos entra sempre, verificada com `plainto_tsquery` (linguagem
    natural, sem interpretar `or`, aspas ou `-` da mensagem);
  - com o híbrido ativo, `TextoIntegral` (a mensagem de onde os termos em OR foram derivados, enviada
    pelo Context Builder) faz a correspondência só parcial exigir apoio semântico;
  - o canal agente (MCP), a conversa natural e o modo lexical ignoram essa regra.
- **Pacote automático**: recebe só os aprovados e não é completado até o limite. A expansão por
  relações parte só deles.
- **Fontes brutas**: `IndiceDeFontesPostgreSql` mantém o seu `0.6` fixo. A divergência em relação ao
  limiar configurável fica para avaliação futura. Sem limiar, as fontes também perdem a parte
  semântica, porque a consulta não gera vetor.

A instância do homelab segue **lexical**. Na validação final, com τ = 0,52 e `bge-m3`, o corte
zerou os pacotes sem resposta e elevou a precisão do pacote para 0,5. Mas as paráfrases ficaram
em 50% de acerto@5, contra os 80% exigidos: paráfrases legítimas e ruído próximo do domínio
ocupam a mesma faixa de similaridade. Calibração, validação e decisão:
[corte de relevância](../brain/avaliacao-busca-hibrida/corte-280/README.md).
