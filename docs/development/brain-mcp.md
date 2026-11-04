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
| brain_vincular_candidatos | Registra a intenção de `ResolvidoPor` entre candidato Incidente e candidato Solucao pendentes; não cria relação |
| brain_listar_relacoes | Vizinhança autorizada de um conhecimento |
| brain_registrar_evidencia | Uma evidência Markdown de teste/validação como fonte bruta de autoria do agente; retorna `fonte:<id>@<rev>` |
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

## Referências tipadas no snapshot (#255)

O campo textual `referencias` aceita `conhecimento:<guid>@<revisao>`,
`candidato:<guid>@<revisao>` e `fonte:<guid>@<revisao>`. Use IDs e revisões obtidos
nas consultas autorizadas; não peça GUID ao usuário. A revisão precisa ser um inteiro
positivo. Texto livre, prefixos desconhecidos e referências malformadas continuam
armazenados e apresentados como texto operacional, sem erro nem resolução heurística.
Não há campo novo, migration ou mudança no modelo de candidatos.

O Context Builder resolve referências completas em itens separados e remove suas
strings do texto operacional. Para cada item, verifica revisão, escopo exato,
sensibilidade e filtros; aplica redação de segredos antes da projeção. Referências
inexistentes, encerradas, removidas, obsoletas, conflitantes ou sem autorização são
descartadas com registro, sem revelar conteúdo protegido. `Secreto` nunca entra no
contexto automático, mesmo com permissão de leitura canônica. `Confidencial` exige
autorização explícita no acesso usado para construir o pacote.

| Referência | Apresentação no pacote |
| --- | --- |
| conhecimento | Mantém tipo, status, natureza e rótulos existentes; passa também pelos filtros de qualidade e validade. |
| candidato | Somente estado Pendente; origem `candidato`, rótulo **Pendente, não confirmado**, natureza preservada e Tipo/Status de conhecimento nulos. Não é fato confirmado. |
| fonte | Documento inteiro da revisão indicada, origem `fonte_bruta`, autoria preservada e Tipo/Status nulos. Só entra se couber integralmente no orçamento. |

Revisão divergente não é substituída pela revisão atual. A restrição vale também
quando busca/relações encontram o mesmo conhecimento, ou a busca encontra trechos
da fonte. Fontes tipadas substituem seus trechos recuperados para evitar duplicação.
Duas revisões diferentes para o mesmo tipo/ID são conflito: ambas são descartadas.
Duplicatas idênticas são resolvidas uma vez.

Prioridade de seleção: instruções confirmadas, decisões confirmadas, demais
conhecimentos confirmados, demais referências tipadas, texto do snapshot,
conhecimentos restantes e fontes recuperadas. Dentro da faixa mantêm-se relevância
e chave estável. A busca não mudou de ranking. O indicador `Referenciado` no DTO
serve à seleção; todos os conteúdos usam o estimador, o `OrcamentoDeTokens` e o
`LimiteDeItens` comuns. Não há truncamento silencioso de uma fonte para fazê-la caber.

Chaves `conhecimento:<id>`, `candidato:<id>` e `fonte:<id>`, acompanhadas da revisão
em `JaInjetados`, impedem reinjeção da mesma versão. Uma revisão nova só entra com
a referência correspondente atualizada. `IdsDecisoesConfirmadas` continua sujeito
à confirmação exigida pela #256, mesmo quando o mesmo ID também for uma referência tipada.

## Decisões confirmadas no snapshot (#256)

`brain_atualizar_contexto_de_trabalho` aceita o campo opcional
`ids_decisoes_confirmadas`, uma lista de IDs de **conhecimentos** obtidos pela busca.
Não use ID de candidato nem peça GUID ao usuário. Os demais campos obrigatórios e a
revisão esperada continuam iguais. A lista tem semântica de substituição completa:

- Campo omitido: conserva a lista atual (ou vazia no primeiro snapshot).
- Lista informada: substitui as referências anteriores; para remover somente uma,
  envie os IDs que devem permanecer.
- `[]`: remove todas as referências.

São aceitas até 20 entradas, antes de deduplicar. Duplicatas dentro desse limite
são removidas ao salvar, mantendo a primeira ocorrência. `null`, IDs vazios,
malformados ou listas acima do limite são recusados. Isso não altera a lista
textual `referencias`; a resolução dos formatos estritos da #255 está descrita acima.

Cada ID precisa ser de `Tipo = Decisao`, `Status = Confirmado`, válido no instante
da atualização, no mesmo espaço e projeto exatos (inclusive projeto nulo), permitido
pela sessão e compatível com a sensibilidade do snapshot. A Application reutiliza
`ContextoDeTrabalhoAppService.SubstituirAsync`; o adapter conserva a resolução
autorizada e os erros classificados da #251. Inexistente, outro escopo e conteúdo
protegido não revelam a causa do não acesso. Erro não grava snapshot parcial.

Não há elevação automática de sensibilidade: snapshot MCP novo continua `Pessoal`;
um conhecimento `Trabalho` exige snapshot já classificado como `Trabalho` e acesso
compatível. Omitir o campo preserva IDs, mas não dispensa sua revalidação pela
Application. Se uma referência ficar inválida, envie a lista sem ela para removê-la.

Fluxo completo:

1. O usuário declara literalmente a decisão.
2. O agente captura um candidato `Decisao` / `DitoPeloUsuario`, com origem literal
   validada, e apresenta a proposta de consolidação.
3. O usuário envia `confirmar` no Telegram. Aprovação da CLI não confirma negócio.
4. O agente consulta `brain_buscar_conhecimento` e verifica tipo `Decisao` e status
   `Confirmado`; o ID do conhecimento consolidado é diferente do ID do candidato.
5. O agente consulta a revisão do snapshot e atualiza seus campos operacionais,
   enviando o ID em `ids_decisoes_confirmadas`.
6. `brain_obter_contexto_de_trabalho` retorna a lista persistida. Em nova sessão,
   o Context Builder recupera a decisão mesmo sem correspondência lexical e revalida
   seu estado antes de injetar. Se perder a confirmação, a referência sozinha não a
   transforma em fato confirmado nem a injeta como decisão confirmada.

Decisões do usuário devem ser referenciadas por ID, em vez de afirmadas pelo agente
em texto livre do snapshot. Não há heurística para bloquear texto livre, nem nova
regra de autoria/confirmação. A inclusão/remoção foi exposta somente pelo MCP; o
fluxo natural de atualização do contexto mantém sua gramática e preserva a lista.

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

## Vínculo incidente → solução entre candidatos (#259)

`brain_vincular_candidatos` recebe `incidente`, `revisao_incidente`, `solucao` e
`revisao_solucao`, todos obrigatórios. Os dois IDs precisam ser de candidatos pendentes
do escopo exato da sessão, visíveis ao agente (Publico, Pessoal ou Trabalho), com tipos
`Incidente` e `Solucao`, nas revisões informadas. A recusa segue a #251: inexistente,
outro espaço/projeto/usuário ou sensibilidade não permitida produzem a mesma resposta
`nao_encontrado_ou_nao_autorizado`; ID de conhecimento é entrada inválida com detalhe;
revisão desatualizada ou candidato encerrado é `revisao_desatualizada`; tipos trocados
recebem detalhe próprio. Repetir o mesmo par devolve a intenção existente.

Registrar o vínculo **não** cria relação, não confirma conteúdo e não pede confirmação.
A relação nasce na confirmação de negócio dos candidatos, pelo Telegram, e só quando os
dois lados estiverem consolidados: na mesma transação em que o segundo candidato é
promovido, o Brain cria `ResolvidoPor` uma única vez (`ObterEquivalenteAsync` e índice
único) e marca a intenção como aplicada. O status epistêmico não é exigido nem alterado:
conclusões do agente consolidadas continuam `Inferido` (AD-59 I1), e a relação não as
torna fatos. Ao preparar a confirmação, a resposta lista `vinculosPendentes`; a mensagem
de confirmação informa se a relação foi registrada ou se aguarda o outro candidato.

Descartar ou rejeitar um lado cancela a intenção na mesma transação. Na consolidação,
lado inativo, fora do escopo ou que deixou de ser Incidente/Solucao (correção de tipo)
também cancela a intenção, sem relação órfã e sem falhar a confirmação. Correções que só
mudam a revisão mantêm a intenção. Confirmações concorrentes dos dois lados conflitam
pelo token de concorrência da intenção; a repetição posterior efetiva o vínculo.
Escopo alterado antes do `confirmar` recusa a operação, como nas demais propostas.
Para conhecimentos já consolidados, use `brain_criar_relacao`.

## Evidências de teste como fonte bruta (#257)

`brain_registrar_evidencia` preserva resultados detalhados de testes e validações que
não cabem no snapshot. Argumentos: `titulo` (obrigatório, até 200), `conteudo` Markdown
(obrigatório, até 20.000), `tags` (até 50 de 100), `sensibilidade` (Publico, Pessoal
padrão, ou Trabalho) e `justificativa` opcional (até 2000). Não há argumento de autoria,
origem, formato ou escopo: o schema recusa extras. A fonte nasce com autoria `Agente`,
mesmo quando o usuário pediu o registro ou a mensagem contém o texto (AD-59 P2), no
espaço/projeto da sessão, com origem `evidencia:<guid>` gerada pelo servidor e
proveniência de sessão única (#249) acrescida da mensagem autenticada.

Credenciais em qualquer campo são recusadas com o detalhe de credencial; marcadores de
transcript/raciocínio privado (os mesmos do snapshot), com detalhe próprio.
Confidencial/Secreto são recusados. Uma evidência por chamada; nada é editado ou removido
pelo MCP. Registrar não cria candidato nem conhecimento e não confirma nada.
O teto de 32 mil caracteres vale para o JSON bruto da chamada: escapes (quebras de linha,
acentos codificados como `\uXXXX`) contam, e uma evidência perto de 20 mil caracteres pode
ser recusada como entrada grande. Divida evidências extensas em registros separados.

A resposta traz `referencia` (`fonte:<id>@<rev>`), para usar em `referencias` do snapshot,
que o Context Builder entrega como `fonte_bruta` na revisão indicada (#255). Repetir o mesmo
conteúdo cria outro registro, preservando a proveniência e os metadados de cada ocorrência;
`possivelDuplicidade` e `duplicidades` apenas apontam as fontes visíveis com o mesmo hash.
Na busca, a evidência aparece como `FonteBruta`, rotulada como autoria do agente, e o
filtro `tags` também alcança fontes com todas as tags pedidas.

## Limites e validação

Uma captura/relação/evidência por chamada; conteúdo até 10 mil caracteres (evidência:
20 mil), título até 300 (evidência: 200), até 50 tags de 100 caracteres. Listagens até 25 itens/página, deslocamento até 10 mil.
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
