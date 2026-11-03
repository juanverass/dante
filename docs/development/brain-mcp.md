# Brain nas sessões de agentes (#226)

O Worker oferece `dante_brain` às sessões Claude/Codex iniciadas com Brain configurado,
storage acessível, identidade Telegram resolvida e espaço/projeto permitido. Sem esses
requisitos a sessão inicia normalmente, sem ferramentas Brain. One-shot não recebe
esse servidor. O harness de ambas as CLIs declara explicitamente o Brain como capacidade
interna, inclusive quando indisponível, sem descoberta de plugin/connector ou solicitação
de URL externa (#228). Claude recebe o contrato por `--append-system-prompt`; Codex por
`developerInstructions` em toda `thread/start`, incluindo `/clear`. O MCP também anuncia
o contrato em `initialize.instructions`. O contrato distingue captura de candidato,
correção com revisão, proposta de consolidação e confirmação no Telegram; atualização
de conhecimento existente usa `brain_atualizar_conhecimento`: corrige o mesmo ID,
preserva histórico e metadados não alterados e deixa a nova versão inferida.
`brain_consolidar_duplicatas` preserva o destino escolhido, incorpora proveniências
e substitui as duplicatas selecionadas. Ambas preparam proposta para confirmação
Telegram, revalidam todos os IDs/revisões e reutilizam os AppServices existentes.
Consolidação recusa conflito aberto, redução de sensibilidade e troca de confirmação
por inferência. Não há escolha automática de destino.
O escopo autorizado permanece independente do repositório investigado.

`dante_planilhas` continua disponível por um provider independente;
`CompositorDeFerramentas` reúne os providers de `IProvedorDeFerramentas`.

## Canal local e escopo

O processo `--mcp-brain --pipe <endpoint>` implementa JSON-RPC MCP stdio e encaminha
operações por named pipe ao Worker vivo. O Worker conserva conexão e autorização e
chama exclusivamente AppServices. A configuração MCP contém somente executável e
endpoint aleatório, nunca conexão, senha ou identidade escolhida pelo modelo.
O pipe exige o mesmo usuário do sistema, aceita uma conexão, pertence a uma sessão e
é revogado ao encerrar/falhar a sessão ou desconectar o consumidor. Não conectado,
expira em dois minutos. Não existe credencial durável em disco. Como as CLIs locais,
o pipe pressupõe confiança na conta do sistema: não isola processos maliciosos do
mesmo usuário que já possam ler a memória/configuração local da CLI.

A identidade, chat/tópico e escopo vêm da mensagem autenticada que inicia a sessão.
Cada operação resolve novamente o acesso via TelegramBrain e valida storage/permissões.
Trocar espaço/projeto invalida as operações do canal anterior: inicie nova sessão para
receber ferramentas do novo escopo. Enviar a sessão por outra conversa/tópico também
recusa o canal anterior. Reinício do Worker não restaura capacidades.

## Ferramentas

| Ferramenta | Comportamento |
| --- | --- |
| brain_obter_escopo | Nomes do espaço/projeto autorizado, sem seleção livre de IDs |
| brain_buscar_conhecimento | Busca lexical/híbrida com texto, tipo, tags e paginação |
| brain_mostrar_origem | Proveniência e histórico autorizado de candidato/conhecimento |
| brain_capturar_conhecimento | Um candidato por chamada; título, conteúdo, tags, tipo, natureza, sensibilidade e justificativa |
| brain_listar_candidatos | Pendentes com IDs e revisões, filtros opcionais por tags e texto, sem pedir GUID ao usuário |
| brain_atualizar_conhecimento | Prepara correção auditada no mesmo ID, revisão esperada e confirmação Telegram |
| brain_consolidar_duplicatas | Prepara consolidação de 1–20 duplicatas explícitas em um destino, com revisões de todos |
| brain_corrigir_candidato | Correção com revisão esperada, sem alterar natureza ou escopo |
| brain_confirmar_candidato | Prepara proposta de consolidação, sem promovê-la |
| brain_cancelar_candidato | Prepara descarte auditado, sem aplicá-lo |
| brain_criar_relacao | Prepara relação tipada entre alvos autorizados com revisões |
| brain_listar_relacoes | Vizinhança autorizada de um conhecimento |
| brain_obter_contexto_de_trabalho | Snapshot operacional ativo |
| brain_atualizar_contexto_de_trabalho | Snapshot com revisão esperada, sem criar Conhecimento |

Ferramentas de leitura são `readOnlyHint=true`; correção, descarte e substituição do
snapshot são `destructiveHint=true`. Mutações seguem o modo de aprovação da CLI.
Isso não substitui confirmação de negócio: propostas de consolidação, descarte ou
relação devem ser apresentadas ao usuário, que envia `confirmar` ou `cancelar` no
Telegram. Há uma proposta por conversa, válida por cinco minutos e consumida uma vez.
Uma nova mensagem/proposta ou fim de sessão invalida a anterior. Confirmação revalida
escopo e revisão; conflitos não reaplicam a operação automaticamente.

Captura é explícita e individual. Conteúdo classificado como dito pelo usuário precisa
estar literalmente na mensagem de origem; mensagem citada só serve como origem
literal quando foi escrita pelo próprio usuário autenticado, no mesmo chat/tópico.
Conclusão do agente conserva o tipo semântico informado e a natureza `ConclusaoDoAgente`;
permanece `Inferido` após consolidação. Título e tags opcionais vivem no histórico JSON existente
do candidato; promoção conserva o título em `DadosEstruturados.titulo` e as tags no
Conhecimento. Na #226 não houve migration nem alteração do conteúdo para embutir esses metadados;
a autoria de documentos persistida na #264 está descrita adiante.

A captura conserva a deduplicação atual por conteúdo/classificação/escopo. Repetição
retorna o candidato existente e sinaliza possível duplicidade; origem equivalente
é auditada. Equivalência semântica requer busca e análise do agente: os resultados
são possibilidades para apresentar, nunca autorização para consolidar silenciosamente.
Proveniência registra MCP/agente, sessão, referência da mensagem e trecho selecionado.
A sessão é gravada como `sessao:<instância>:<S00000N>`: o número curto recomeça a cada
processo do Worker, e a instância (a mesma usada nas métricas de continuidade) o torna
único entre reinícios. Registros anteriores, no formato `sessao:<S00000N>`, não são
reescritos e continuam legíveis como estão.
Não registra transcript completo ou raciocínio privado.

Confidential/Secret não recebem grants adicionais. Falta de autorização falha fechado;
conteúdo/histórico passam pelas policies de sensibilidade e proteção de segredos atuais.

## Semântica AND/OR da busca (#254)

Decisão: manter explicitamente a diferença entre consulta do agente e recuperação
automática de contexto (alternativa **c** da Issue). O agente pode escrever uma
consulta precisa; o contexto automático precisa recuperar itens a partir de uma
mensagem inteira, que também contém instruções e palavras sem relação com o conhecimento.

| Caminho | Preparo e combinação lexical |
| --- | --- |
| `brain_buscar_conhecimento` | Preserva o texto, até 2000 caracteres. Termos adjacentes usam AND implícito; `or` indica alternativas; aspas delimitam frase; `-` é aceito, com a ressalva de exclusão descrita abaixo. Usa `websearch_to_tsquery('portuguese', texto)` com parâmetro SQL. |
| Contexto automático do Telegram | `TelegramBrain.Termos` extrai tokens de letras/números (3 a 64 caracteres, admitindo `_`, `.` e `-` internos), normaliza caixa, remove palavras vazias da lista local, deduplica e seleciona até 16, unidos por `or`. Depois aplica a mesma função lexical do PostgreSQL. |

Não foi adicionado parâmetro `modo`: OR já é expressável na consulta. Aplicar o
extrator automático ao MCP transformaria frases e operadores e limitaria
consultas existentes a 16 termos. Por isso não há normalizador compartilhado nem
mudança em runtime, ranking ou busca híbrida. O schema descreve a consulta preservada.

O dicionário português do PostgreSQL aplica suas próprias stopwords e stemming nos
dois caminhos. A lista local adicional e o limite de 16 são exclusivos do preparo
automático. Se o extrator não produzir termos, o Context Builder usa a mensagem
original limitada a 2000 caracteres como fallback, preservando o comportamento atual.
A ausência de correspondência lexical não prova ausência de conhecimento;
sinônimos e paráfrases sem lexemas comuns não são garantidos. Em modo híbrido,
resultados semânticos podem complementar os lexicais, sem alteração nesta entrega.

Caso da #147, conhecimento `704dbde5` sobre integração do Brain ao PostgreSQL, com
tags `brain` e `postgres` (testado com conteúdo equivalente em banco temporário):

| Consulta | Resultado lexical esperado |
| --- | --- |
| MCP: `quando o Brain passou a usar Postgres?` | Não encontra: exige também termos ausentes como `passou` e `usar`. |
| MCP: `Brain Postgres` | Encontra: ambos os termos existem no conteúdo/tags. |
| MCP: `Brain or passou or usar or Postgres` | Encontra por termos alternativos. |
| Contexto automático: `quando o Brain passou a usar Postgres?` | Encontra após preparo `brain or passou or usar or postgres`. |

Aspas, hífen e `or` são sintaxe **da consulta lexical**, não interpolação SQL ou
expressões de `to_tsquery` fornecidas diretamente. Entrada com aspas incompletas ou
pontuação continua sendo tratada por `websearch_to_tsquery`, sem montar SQL a partir
do texto. Filtros de escopo, sensibilidade e natureza continuam obrigatórios.
**Ressalva do hífen:** o parser reconhece negação, mas a seleção atual dos índices
é baseada em `ts_rank_cd(...) > 0`, sem impor correspondência por `@@`. Por isso
`Brain -Postgres` pode retornar tanto `Brain SQLite` quanto `Brain Postgres`. Não
use o hífen como garantia de exclusão. Esta Issue registra e testa a limitação, sem
alterar ranking ou SQL de recuperação.

A listagem de candidatos da #253 mantém substring literal; não usa essa sintaxe.

## Filtros de candidatos pendentes (#253)

`brain_listar_candidatos` aceita `tags` e `texto` opcionais. Todas as tags informadas
devem existir na **revisão atual** do candidato, com igualdade sem distinguir
maiúsculas. Tags ausentes ou nulas no histórico legado equivalem a nenhuma tag.
Tags removidas por correção deixam de recuperar o candidato.

`texto` é uma substring literal, sem distinguir maiúsculas, no título **ou** conteúdo
atual. Não interpreta operadores, curingas (`%`, `_`) nem busca semântica. Com ambos
os filtros, exige tags **e** texto. Exemplo: `{"tags":["teste-147"],"texto":"IPv6"}`.
Texto, quando informado, tem de 1 a 2000 caracteres e não pode ser só espaços;
tags aceitam até 50 valores de 1 a 100 caracteres, não vazios nem só espaços.

Os filtros são parametrizados no PostgreSQL, antes da ordenação por ID e da paginação.
Continuam valendo os filtros globais de usuário/tenant, espaço, projeto exato (inclusive
projeto nulo) e sensibilidade, além de `Permitida` no MCP. Sem texto e com tags ausentes
ou `tags: []`, a listagem mantém o comportamento anterior. Só retorna candidatos pendentes, com estado,
natureza e revisão; promovidos, rejeitados e descartados são excluídos. Não inclui
candidatos em `brain_buscar_conhecimento` nem no pacote de contexto.

## Limites e validação

Uma captura/relação por chamada; conteúdo até 10 mil caracteres, título até 300,
até 50 tags de 100 caracteres. Listagens até 25 itens/página, deslocamento até 10 mil.
Entrada até 32 mil caracteres, resposta MCP até 60 mil, mil chamadas por sessão e
30 segundos por operação. Resposta excessiva retorna orientação para reduzir a consulta.
Operações com erro/resultado incerto não são repetidas automaticamente.

## Erros classificados (#251)

Toda recusa do canal MCP retorna `isError: true` e, no texto, um objeto JSON com
`erro` (código estável), `mensagem` (fixa por código) e, quando útil, `detalhe`
(texto fixo do adapter). Nenhum erro contém mensagem de exceção, stack trace, nome de
tabela, GUID ou conteúdo de outro escopo. Conjunto fechado:

| Código | Quando | O agente deve |
| --- | --- | --- |
| `entrada_invalida` | argumento fora do schema, tipo JSON errado, enum/limite inválido, conteúdo factual ausente da mensagem de origem, credencial no conteúdo, sensibilidade acima de Trabalho, redução de sensibilidade, mudança de natureza, relação com tipos incompatíveis, ID do tipo errado **visível** ao agente, resposta grande demais, pedido acima de 32 mil caracteres (descartado até o fim da linha sem acumular; stdio e canal do Worker respondem e continuam). Se a quebra não vier em até 1 Mi caracteres excedentes ou 30 s, a resposta traz detalhe de canal encerrado e o canal fecha de forma controlada | corrigir os argumentos; repetir igual não muda o resultado |
| `revisao_desatualizada` | revisão esperada diferente da atual, candidato já encerrado, conflito de concorrência/UNIQUE ou estado que não permite a operação (`InvalidOperationException` lançada pelo Domain/Application) | consultar de novo e usar a revisão atual |
| `nao_encontrado_ou_nao_autorizado` | **unificado de propósito**: inexistente, de outro espaço/projeto/usuário, sensibilidade não permitida, conteúdo protegido ou item inativo/substituído | não insistir; a resposta é idêntica byte a byte em todos esses casos e nunca leva `detalhe` |
| `escopo_mudou` | sessão revogada, troca de conversa/espaço/projeto, espaço/projeto arquivado | iniciar nova sessão; o Worker encerra o canal após responder |
| `indisponivel` | banco/driver, timeout de 30 s, pipe com o Worker fechado, limite de chamadas ou erro inesperado (inclusive `InvalidOperationException` de EF/Npgsql/LINQ e violação de integridade que não seja UNIQUE, por exemplo FK de item removido em concorrência) | não repetir automaticamente operação de resultado incerto |

Sem oráculo de enumeração: a resolução de ID devolve a mesma exceção para todos os
motivos de não acesso. Um ID de candidato enviado a uma ferramenta de conhecimento
(ou o contrário) só vira `entrada_invalida`, com detalhe indicando a ferramenta certa,
quando o outro item já é visível ao agente no mesmo escopo (sensibilidade permitida);
caso contrário a resposta é `nao_encontrado_ou_nao_autorizado`. `brain_criar_relacao`
aplica a regra de tipos do domínio (`ResolvidoPor`: Incidente → Solucao;
`ProduziuAprendizado`: Solucao → Aprendizado) já na preparação, com os tipos que o
agente já leu.

A confirmação de proposta no Telegram usa as mesmas categorias para explicar a recusa
ao usuário ("o item mudou", "tipos incompatíveis com a relação", "não encontrado ou não
autorizado", "escopo mudou", "Brain indisponível") em vez da mensagem única anterior.

```bash
dotnet test Dante.sln --filter 'FullyQualifiedName~BrainMcpTests|FullyQualifiedName~BrainMcpErrosTests|FullyQualifiedName~SessionDriverTests'
```

`BrainMcpTests` usa `DANTE_TEST_POSTGRES` em instância de teste com CREATE DATABASE;
cria e remove bancos isolados. Testa 24 capturas por IPC, deduplicação, título/tags,
confirmação, recuperação em nova sessão, isolamento e revisão.
E2E opt-in com CLIs reais:

```bash
DANTE_LIVE_BRAIN_MCP=1 dotnet test Dante.sln --filter FullyQualifiedName~LiveBrainMcpEvidenceTests
```

Com `DANTE_TEST_POSTGRES` definido, cada agente recebe os nomes Desenvolvimento e
D.A.N.T.E. em banco isolado, captura três itens separadamente pelo MCP stdio,
confirma um pelo fluxo natural e recupera conhecimento/proveniência em outra sessão.
A conexão de teste nunca vai à CLI. Não habilite esse teste com banco produtivo.

O MCP usa a mensagem autenticada do turno ativo, preservada junto à entrada na fila.
Mensagens recebidas/enfileiradas não substituem essa evidência; entradas sem contexto
autenticado revogam o acesso. Captura e correção factual exigem conteúdo na origem.
Relações são projetadas somente quando ambos os alvos permitem leitura; sua prova
bruta não é exposta no canal MCP. Sessão ausente/revogada falha fechado.

Steer nativo aceito pelo driver atualiza a evidência autenticada do Brain; steer
recusado mantém a origem anterior. Respostas textuais a perguntas do agente não
alimentam evidência factual no MCP neste MVP. A listagem de relações percorre
páginas internas de até 100 itens até preencher o limite de relações permitidas
ou esgotar a vizinhança; o indicador de limite conta apenas relações autorizadas.
O prazo por operação continua limitando o percurso, sem retornar lista parcial
como se a vizinhança tivesse sido esgotada.

A regressão #228 usa pedidos naturais nas duas CLIs reais, sem nomear ferramentas.
`AgenteInvestigaRepositorioECapturaInferenciaQueOutraSessaoRecupera` cria repositório
Git descartável com README e marcador único, pede análise e captura como `Nota` de natureza `ConclusaoDoAgente`,
confirma pelo Telegram e recupera conteúdo e proveniência em nova sessão. É fixture
local, sem acesso ao repositório real do usuário. Testes MCP/PostgreSQL verificam
atualização sem duplicata, confirmação/cancelamento, revisão concorrente, escopo,
sensibilidade, conflitos e preservação de proveniências na consolidação.

## Autoria de mensagens e documentos (#264)

Pedir um registro ou confirmá-lo não transfere a autoria da fonte ao solicitante.
Na captura, correção de candidato e atualização pelo MCP, `DitoPeloUsuario` e
`FonteSelecionada` exigem conteúdo literal na mensagem autenticada (texto/legenda)
ou em mensagem respondida do próprio usuário, no mesmo chat e tópico. Mensagem
respondida do bot, de outro usuário ou sem autor não é prova de declaração do usuário.
O agente deve usar `ConclusaoDoAgente` para suas conclusões; essa classificação
permanece inferida mesmo após a confirmação humana.

Na captura natural, responder à própria mensagem produz `FonteSelecionada`;
responder a mensagem identificada como bot produz `ConclusaoDoAgente`.
Autor ausente ou outro usuário é recusado como fonte. A proveniência da seleção
registra `autoria-da-fonte:Usuario|Agente|Desconhecida`, separadamente do responsável
pela operação. O transporte da sessão conserva ID e indicador de bot do autor citado.

Opção A: `DocumentoFonte.Autoria` persiste `Usuario` ou `Agente` na importação e
na nova revisão. Os fluxos explícitos de importação do usuário (conversa e arquivo
local indicado na CLI) informam `Usuario`; chamadas programáticas devem informar a
autoria efetiva, inclusive `Agente` para documentos produzidos pelo agente. Importar
não confirma o conteúdo. A geração de candidato respeita a autoria persistida:
`Usuario` produz `FonteSelecionada`; `Agente` produz `ConclusaoDoAgente`.

A migration `20261102150504_AutoriaDasFontes` acrescenta apenas a coluna `autoria`,
com `Desconhecida` para documentos antigos. Não reclassifica candidatos/conhecimentos
nem modifica textos, hashes ou revisões existentes. Documentos antigos continuam
consultáveis, mas não geram candidatos até reimportação explícita com autoria e
revisão esperada. Escritas novas não aceitam `Desconhecida`. Uma mudança apenas de
autoria também gera nova revisão, preservando a concorrência otimista.

## Natureza do conhecimento e confirmação (#265, AD-59)

`Conhecimento` conserva `NaturezaDeOrigem` imutável e `Natureza` da revisão vigente.
Cada nova `RevisaoDoConhecimento` registra sua própria natureza; revisões anteriores
não são alteradas por confirmação, correção, invalidação ou consolidação. Os DTOs de
leitura expõem esses campos. Em revisões antigas, `Natureza = null` significa
**natureza não registrada**, diferente de uma declaração do usuário.

Criação e correção exigem natureza explícita. `Desconhecida` é reservada ao legado;
não pode ser enviada para criar conhecimento/candidato ou conteúdo de nova revisão.
A natureza de origem recebida em DTO de correção é ignorada. MCP propaga a natureza
validada da atualização; a correção natural usa a declaração literal do usuário.
As verificações de autoria da #264 continuam valendo para texto e mensagem citada.

A regra única está no Domain: `Conhecimento.PodeSerConfirmado` exige natureza de
origem **e** natureza vigente `DitoPeloUsuario` ou `FonteSelecionada` de autoria do
usuário. `Conhecimento.Confirmar`, promoção de candidato e
`ConhecimentoAppService.ConfirmarAsync` obedecem a essa mesma regra, independentemente
do tipo semântico. Nenhum construtor aceita `Status = Confirmado`. A promoção
repassa a natureza do candidato; conclusão do agente permanece `Inferido`.

Uma correção/atualização do agente registra `ConclusaoDoAgente` na nova revisão e
fica `Inferido`, inclusive sobre conteúdo antes confirmado ou temporário. Correção
humana de item de origem do agente registra natureza humana na nova revisão, mas
não torna o item confirmável: a origem permanece do agente. Para afirmar o conteúdo
como próprio, o usuário precisa de um **novo** candidato/conhecimento de autoria
humana, com confirmação explícita; `DerivadoDe` pode ligá-lo ao original sem alterar
a natureza, o status ou o histórico do original. Esse vínculo usa o fluxo de relações
existente e também exige confirmação Telegram. Não há criação ou relação implícita.

Consolidar uma duplicata do agente em um destino confirmado incorpora a proveniência,
conserva conteúdo/status/naturezas do destino e substitui a duplicata. Não promove a
duplicata nem reescreve as naturezas de revisões existentes.

A migration `20261102220449_NaturezaDosConhecimentos` acrescenta duas colunas com
`Desconhecida` (valor 3, preservando os valores anteriores do enum). Preenche somente
`natureza_de_origem`, exclusivamente pela natureza do candidato que referencia o
conhecimento em `IdConhecimento`, no mesmo espaço/projeto. Sem candidato, ou com
candidatos de naturezas conflitantes, a origem permanece desconhecida. Não se deduz
autoria de tipo, conteúdo ou texto de proveniência. A natureza da revisão atual
permanece desconhecida no legado, pois o candidato não prova quem escreveu revisões
posteriores. Nenhum status, texto, timestamp ou JSON histórico é reescrito: itens
antigos confirmados continuam assim, mas novas confirmações exigem a regra atual.
A migration é aplicada apenas por administração explícita; startup não migra.

A preservação do tipo semântico na captura/correção do agente está descrita abaixo
(#266); filtros e rótulos de busca/Context Builder seguem as regras da #267 abaixo.

### Tipo semântico independente da autoria (#266)

Captura, correção, atualização e promoção preservam o tipo informado, independentemente
da natureza e do status. Nas correções pelo MCP, `tipo` é opcional: omiti-lo conserva
o tipo atual; informá-lo permite reclassificação explícita, registrada no histórico.
`ConclusaoDoAgente` identifica autoria e permanece `Inferido`, sem confirmação de
negócio. O Telegram distingue registro de conclusão e confirmação com base na
natureza e no status persistidos, nunca no tipo.

`Inferencia` permanece disponível para leitura e filtro de registros legados, sem
reclassificação retroativa. Não há migration nesta entrega. A deduplicação continua
considerando tipo, natureza, sensibilidade e conteúdo no escopo autorizado.
A filtragem por natureza da #267 é descrita a seguir.

### Natureza na busca, contexto, inspeção e exportação (#267)

`brain_buscar_conhecimento` aceita `natureza` opcional: `DitoPeloUsuario`,
`FonteSelecionada`, `ConclusaoDoAgente` ou `Desconhecida`. Esse filtro compara
**exclusivamente a natureza de origem**, não a natureza da revisão atual. É aplicado
antes da paginação, em conjunto com o tipo/tags e o escopo/sensibilidade já autorizados.
Na Application, o campo correspondente é `BuscaDoBrainSearchDto.NaturezaDeOrigem`.
O filtro também é revalidado nos conhecimentos acrescentados por relações e pelo
snapshot no Context Builder, sem resolver novas referências tipadas (#255).

A resposta de busca expõe `Item.NaturezaDeOrigem`, `Item.Natureza`, `Item.Status` e
`Item.Rotulo`. A consulta de origem de um conhecimento também traz esses campos,
sem remover proveniência/histórico. O tipo continua semântico: `tipo=Inferencia`
continua filtrando legados e `tipo=Fato` não comprova confirmação.

O mesmo rótulo é usado no JSON injetado pelo Context Builder, na consulta natural,
na inspeção e nas exportações Markdown/JSON. Origem do agente é sempre identificada
como “Conclusão do agente, não confirmada”, mesmo após correção humana. Revisão do
agente sobre origem humana é identificada como “Revisão do agente, não confirmada”.
Origem desconhecida é “Autoria de origem desconhecida, não comprovada”; revisão
antiga sem natureza é “Natureza da revisão não registrada; autoria não comprovada”.
Avisos acumulam quando necessário. Confirmados com origem e revisão humanas mantêm
“Confirmado”; inferidos/temporários humanos recebem “não confirmado”.

Os campos e rótulos são **projeções de leitura**, não modificam status, natureza ou
histórico persistidos. O JSON de auditoria mantém formato 1 com campos aditivos;
revisões exportadas recebem rótulo usando a origem do item e a natureza daquela
revisão. Não há migration, reclassificação de `Inferencia` ou inferência de autoria
por texto/proveniência. Legado `Confirmado` de autoria desconhecida conserva o
status armazenado e recebe o aviso de autoria não comprovada.

Fontes brutas continuam separadas: expõem autoria explicitamente registrada e
rótulo de fonte não confirmada, sem natureza de conhecimento artificial. Quando
há filtro por natureza, **fontes brutas são excluídas**, inclusive no filtro
`Desconhecida`; sem filtro, participam como antes. Snapshots continuam contexto
operacional sem tipo/status/natureza de conhecimento.

Cabeçalho, ranking, limites, deduplicação, algoritmo de estimativa e orçamento do
pacote permanecem os mesmos. A serialização dos campos/rótulos novos é contabilizada
antes da seleção: mais metadados podem reduzir o conteúdo que cabe no mesmo orçamento.
Regras de autorização e redação de conteúdo sensível permanecem aplicadas em todos
os canais; nada autoriza acesso ao banco canônico para testes.
