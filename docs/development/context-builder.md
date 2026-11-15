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
menor assim. O cabeçalho é o enxuto da C1 (mesmas regras,
298 B com sensibilidade uniforme, antes 340 B), seguido de linhas condicionais da legenda (AD-65), todas sem perda:
rótulo repetido vai uma vez ao cabeçalho (`Rotulo ausente = Rotulos[Status/NaturezaDeOrigem[/Natureza] | fonte/AutoriaDaFonte | origem]: {json}`),
indexado pelos campos visíveis do próprio item, e só quando o texto fica menor; senão `Rotulo` fica em cada item.
A explicação da fonte omitida aparece uma vez, se houver fonte omitida. Quando `DadosEstruturados` de um
conhecimento é exclusivamente `{"titulo": "<texto>"}`, o título vai na chave `Titulo` e
`Conteudo` fica só com o corpo; só o JSON persistido decide, e conteúdo que imite esse
sufixo continua em `Conteudo`. Seleção, deduplicação e prova seguem sobre o texto inteiro.
Relação por posição (`@n`, A4) foi avaliada e não adotada: a posição muda entre pacotes
da mesma conversa (refresh, orçamento) e não é usável em ferramentas; a economia seria
de ~10 B. O snapshot usa campos nomeados (Objetivo,
Tarefa, Progresso, UltimoResultado, Referencias, Pendencias, ProximosPassos). Relações
registradas entre conhecimentos do pacote entram em `Relacoes` se couberem no orçamento.

Fonte bruta inteira só tem a íntegra omitida com prova: os trechos indexados da revisão
cobrem o documento de forma contínua, de 0 até o fim, e cada ponto de código não branco
do documento está num intervalo citado por um conhecimento selecionado no mesmo pacote.
Um conhecimento conta só pela proveniência registrada (`ReferenciaDeTrecho`): mesma fonte,
mesma revisão, parte existente com a mesma referência base (id, revisão, parte, hash do
documento); sem intervalo, cita a parte inteira (legado de `GerarCandidatoAsync`); com
`&inicio=&fim=&sha256=`, o intervalo fica dentro da parte, começa e termina em fronteira
de linha e o SHA-256 do texto citado confere. Em ambos os casos o conteúdo do conhecimento
contém o texto citado (normalizado). Só espaço em branco pode ficar fora da união. A
cobertura usa a unidade de offset do
índice (`inicio` em caracteres do PostgreSQL, isto é, code points): o documento é
remontado pela união dos intervalos `[inicio, inicio + comprimento)`, cada trecho começa
em ou antes do fim já coberto, a sobreposição coincide e o remontado é igual à fonte.
Primeiro trecho deslocado, lacuna intermediária, sobreposição divergente ou conteúdo além
do último trecho mantêm a íntegra; o maior fim alcançado não é prova. A linha mantém
`Ref`, `Titulo`, `Rotulo` (ou a legenda), autoria e `ConsolidadaEm`, e troca `Conteudo` pela chave `Omitida:true`
(chave é metadado; nenhum conteúdo a imita). `ConsolidadaEm` só sai quando a cobertura é exatamente o conjunto de
todos os conhecimentos do pacote, regra declarada no cabeçalho; com outro conhecimento no pacote ou duas fontes
omitidas, a lista fica explícita. Sem prova, a fonte segue integral;
co-ocorrência no snapshot não é prova.

Deduplicação considera ID, conteúdo normalizado, prompt/fragmentos já presentes, campos
de snapshot e trechos sobrepostos da mesma fonte/revisão. Nos campos do snapshot, só
igualdade após normalização (NFC e espaços) prova equivalência; substring não prova
("habilitar X" está contido em "Não habilitar X"). Confirmados têm prioridade sobre sua
repetição igual no snapshot: objetivo, pendência e próximo passo iguais a um item
selecionado viram ponteiro `[contido em <ref>]` para esse item, preservando o papel; os
demais campos iguais saem. Entrada igual repetida na mesma lista sai; entradas distintas
ficam todas. O snapshot só é descartado inteiro quando é igual a conteúdo já presente,
nunca por estar contido em outro item ou no prompt. Fragmentos
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
