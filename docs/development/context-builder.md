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
sem autoridade de sistema nem ampliação de ferramentas/permissões.

Formato compacto (#147): cabeçalho com aviso e legenda, depois um objeto JSON por linha.
Só as chaves são metadados; título, Markdown e snapshot vão sempre em valores de string,
com quebras de linha, aspas, U+2028/2029/0085 escapados, então não abrem linha nem criam
chave. `Ref` (`origem:id@revisão` ou a URI do trecho) substitui ID/revisão/origem;
proveniência diferente vai em `Fonte`. Campos nulos são omitidos; `Natureza` só aparece
quando diverge de `NaturezaDeOrigem`; `Sensibilidade` vai ao cabeçalho quando uniforme e
menor assim. `Rotulo` está sempre presente. O snapshot usa campos nomeados (Objetivo,
Tarefa, Progresso, UltimoResultado, Referencias, Pendencias, ProximosPassos). Relações
registradas entre conhecimentos do pacote entram em `Relacoes` se couberem no orçamento.

Fonte bruta inteira só tem a íntegra omitida com prova: todos os trechos indexados da
revisão cobrem o documento e cada um está contido num conhecimento selecionado no mesmo
pacote cuja proveniência registrada é exatamente a referência do trecho (id, revisão,
parte, hash). A linha mantém `Ref`, `Titulo`, `Rotulo`, autoria e `ConsolidadaEm`. Sem
prova, a fonte segue integral; co-ocorrência no snapshot não é prova.

Deduplicação considera ID, conteúdo normalizado, prompt/fragmentos já presentes, campos
de snapshot e trechos sobrepostos da mesma fonte/revisão. Confirmados têm prioridade
sobre sua repetição no snapshot; objetivo, pendência e próximo passo repetidos viram
ponteiro `[contido em <ref>]` para o item que os contém, preservando o papel. Fragmentos
aceitos são limitados e não representam retenção de transcript. Orçamento explícito: 64..32000 tokens estimados e até 100 itens.
Estimativa neutra: ceil(bytes UTF-8 / 3), incluindo JSON/escaping/cabeçalho; não é contagem
exata do tokenizer de Claude/Codex nem estimativa de preço. A seleção estima cada item na
forma mais longa (sensibilidade por item, sem omissão); o pós-processamento só reduz o
texto ou, para relações, é conferido contra o orçamento. Itens excedentes são omitidos
por inteiro, com motivo; custo reporta tokens/itens/caracteres/bytes efetivos do pacote.

Todos os candidatos possuem registro de seleção/descarte e motivo, inclusive budget.
Construir não significa injetar: RegistrarInjecao só aceita chaves selecionadas e deve
ser chamado pelo adapter após envio bem-sucedido. Não há marcação otimista de injeção,
log de conteúdo ou armazenamento de transcript. Resultado vazio tem texto/custo zero.

`JaInjetados` (chave → revisão, até 500) descarta o que já está na conversa upstream na
mesma revisão, com motivo próprio; revisão nova volta a ser elegível. O JSON mantém texto
Unicode legível, sem `\uXXXX` para acentos nem escapes HTML (o destino é prompt, não página).

A integração às sessões da conversa natural é da #145: [continuidade](continuidade-brain.md).
Observabilidade operacional pertence à #148.
