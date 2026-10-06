# Construção de contexto do Brain

ConstrutorDeContextoAppService recebe identidade/escopo autorizado e PedidoDeContextoDto.
Mensagem é o pedido atual; Filtros.Texto pode conter o termo extraído da intenção. Sem
termo explícito, busca a mensagem. Não infere identidade, espaço ou permissões do texto.
BuscaDoBrain oferece lexical/híbrida conforme disponibilidade; não há fallback ao
transcript inteiro ou a outros espaços. Snapshot é operacional, não fato confirmado.

Recuperação tem até 100 resultados iniciais, até 200 IDs canônicos após expansão,
profundidade 0..3, 100 relações por nível e snapshot ativo opcional. Revalida filtros,
validade atual, sensibilidade e conflitos abertos; metadados de contradições inacessíveis
bloqueiam reinjeção sem revelar o outro lado. Secret nunca entra em contexto automático,
mesmo com permissão de leitura manual. Fontes mudadas/removidas são descartadas.

Rerank: instrução confirmada, decisão confirmada, outros confirmados, snapshot,
conhecimento inferido/temporário, fonte bruta. Dentro de cada classe, relevância de
busca/expansão e chave estável. O pedido atual tem precedência; o pacote é dado citado,
sem autoridade de sistema nem ampliação de ferramentas/permissões. Conteúdo é serializado
como objetos JSON, com ID/revisão/tipo/status/classificação/referência e corpo autorizado.

Deduplicação considera ID, conteúdo normalizado, prompt/fragmentos já presentes, campos
de snapshot e trechos sobrepostos da mesma fonte/revisão. Confirmados têm prioridade
sobre sua repetição no snapshot. Fragmentos aceitos são limitados e não representam
retenção de transcript. Orçamento explícito: 64..32000 tokens estimados e até 100 itens.
Estimativa neutra: ceil(bytes UTF-8 / 3), incluindo JSON/escaping/cabeçalho; não é contagem
exata do tokenizer de Claude/Codex nem estimativa de preço. Itens excedentes são omitidos
por inteiro, com motivo; custo reporta tokens/itens/caracteres/bytes efetivos do pacote.

Todos os candidatos possuem registro de seleção/descarte e motivo, inclusive budget.
Construir não significa injetar: RegistrarInjecao só aceita chaves selecionadas e deve
ser chamado pelo adapter após envio bem-sucedido. Não há marcação otimista de injeção,
log de conteúdo ou armazenamento de transcript. Resultado vazio tem texto/custo zero.

`JaInjetados` (chave → revisão, até 500) descarta o que já está na conversa upstream na
mesma revisão, com motivo próprio; revisão nova volta a ser elegível. O JSON mantém texto
Unicode legível, sem `\uXXXX`, e continua escapando caracteres sensíveis a markup.

A integração às sessões da conversa natural é da #145: [continuidade](continuidade-brain.md).
Observabilidade operacional pertence à #148.
