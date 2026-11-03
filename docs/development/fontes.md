# Fontes brutas do Brain

DocumentoFonte guarda o texto original e metadados: escopo imutável, origem, formato,
hash SHA-256 do UTF-8 exato, revisão, classificação, responsável e data. Uma origem é
única no espaço/projeto (inclusive sem projeto). Reimportação exige revisão esperada;
mesmo conteúdo/classificação é idempotente, alteração incrementa revisão e invalida
índices anteriores. Origem/formato não mudam. Conflitos exigem recarregar o escopo.

ImportarAsync aceita nota/texto ou Markdown. ImportarArquivoAsync só lê arquivos
explicitamente selecionados sob DANTE_BRAIN_IMPORT_ROOT absoluto, sem links e até
1 MB UTF-8 válido. Formatos: md/markdown, txt, csv, json, yaml/yml e log. Sem download,
execução, leitura de diretório inteiro ou promoção automática. Markdown mantém seus
cabeçalhos, blocos e caracteres exatos; não é renderizado como HTML ativo.

brain_index.partes_fontes é derivado: trechos de até 1600 pontos de código Unicode,
com passo 1440 (sobreposição de 160), início zero-based e número zero-based. O trigger
reconstrói as partes na mesma transação da fonte. A referência contém documento,
revisão, número e hash; seleção copia o trecho exato para a proveniência do candidato.
Confirmação posterior segue #139. Alterar o documento não reescreve prova histórica.

BuscaDoBrain combina conhecimento e fontes, identifica FonteBruta explicitamente e
pagina o ranking combinado. Filtros de tipo/status/natureza canônicos excluem fontes, que não possuem esses estados.
O filtro de tags inclui fontes que tenham todas as tags pedidas, sem distinguir maiúsculas;
fontes sem tags (importações e legado) não aparecem quando há filtro de tags. Busca lexical não exige pgvector. Quando habilitado
por --brain search-enable-vector, embeddings de partes usam provider/modelo/versão/dimensão
da busca e revision guard com lock na fonte. Secret nunca entra em índices; Confidential
exige autorização e nunca segue para provedor externo. Conteúdo com segredo real é
recusado; apenas referência opaca é permitida. Nenhum trecho ganha autoridade de instrução.

RemoverAsync apaga o texto bruto, marca a fonte removida e elimina todas as partes e
vetores na mesma transação. Conhecimentos/candidatos já selecionados permanecem, com
IDs, hash e citação histórica; o original removido não pode mais ser consultado. Não há
armazenamento de versões integrais antigas: provas selecionadas preservam a citação.
Reimportação explícita pode reativar a origem com revisão nova. ReconstruirAsync refaz
partes da fonte canônica sem alterar a revisão e invalida seus embeddings, que podem
ser regenerados por BuscaDoBrain.ReindexarAsync. Nenhuma fonte inteira vira fato.

A citação de proveniência conserva whitespace inicial/final para manter o trecho
literal. Origem/referência/revisão continuam aparadas; a mudança não reescreve provas
já persistidas em versões anteriores.

## Evidências registradas por agente (#257)

`DocumentoFonteAppService.RegistrarEvidenciaAsync` cria uma fonte Markdown nova por
chamada, com autoria `Agente` fixa, origem `evidencia:<guid>` gerada pelo servidor e
o escopo do acesso autorizado. Metadados de registro ficam em colunas próprias,
fora do conteúdo e do hash: título (até 200), tags (até 50 × 100, aparadas e sem
repetição ignorando maiúsculas), justificativa opcional (até 2000) e proveniência de
quem registrou (até 500; no MCP, `MCP/agente:<agente>;sessao:<instância>:<sessão>;conversa:<chat>:<tópico>:<mensagem>`).
O conteúdo vai até 20.000 caracteres. Credenciais e marcadores de transcript/raciocínio
privado são recusados com a mesma regra do snapshot. Só Publico, Pessoal e Trabalho.

Mesmo conteúdo registrado de novo cria outra fonte, com sua própria proveniência e
metadados; a resposta informa as referências visíveis (`fonte:<id>@<rev>`) com o mesmo
hash, sem fundir nem reaproveitar registros e sem revelar fontes protegidas. Nunca cria
candidato ou conhecimento; gerar candidato de um trecho segue `GerarCandidatoAsync`,
que classifica a evidência como conclusão do agente (AD-59 I9), inferida mesmo após
confirmação. A migration 20261103205445 acrescenta os metadados com valores neutros
(null/vazio) para fontes existentes; importação natural/CLI e a unicidade
(espaço, projeto, origem) não mudam. Não há edição nem remoção de evidências pelo MCP.
