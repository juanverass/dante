# Núcleo de Conhecimento (#138)

`Conhecimento : EntidadeBase` no Domain, com Guid Id e escopo imutável:
IdEspacoDeConhecimento obrigatório e IdProjeto opcional. Application oferece
IConhecimentoRepository/IConhecimentoAppService, ConhecimentoAppService,
ConhecimentoDto e ConhecimentoSearchDto sobre a base CRUD (AD-39).
Não há captura automática de conversa, transcript, embeddings ou resumo de índice
nessa entidade. Um Resumo explicitamente registrado é conteúdo canônico com origem,
não substitui suas fontes. Incidente, Solucao e Aprendizado são tipos independentes;
as relações entre eles serão da #153.

## Conteúdo e evidência

Há conteúdo textual e/ou objeto JSON opcional; pelo menos um é obrigatório. JSON
é validado no domínio, sem consulta a fonte externa. Conteúdo/JSON têm limite de
100000 caracteres cada. Tags são aparadas e deduplicadas sem distinguir maiúsculas,
até 50 tags de 100 caracteres, sem caracteres de controle. Limites são defensivos
para validar entradas, não promessa de orçamento de recuperação (#140).

ProvenienciaDoConhecimento é valor imutável: IdResponsavel, Origem (até 200),
ReferenciaDaFonte (até 2000), RevisaoDaFonte (até 200) e TrechoDaFonte (até 10000).
Responsável/origem são obrigatórios; revisão/trecho exigem referência. Identidade
lógica da fonte não é path a ler nem fonte bruta automaticamente confirmada.
IdAutor vem do primeiro responsável e nunca muda. A origem pode identificar um
humano ou processo; ela não autentica nem autoriza esse ator (#150).

Sensibilidade usa Publico, Pessoal, Trabalho, Confidencial e Secreto, traduções dos
cinco níveis conceituais da AD-33. DTO novo usa Pessoal por padrão; isso não
implementa policy de leitura, captura, injeção ou exportação (#155).

## Estados, revisões e validade

Criação admite Inferido ou Temporario, nunca Confirmado/Substituido/Inativo. Não
há confirmação por valor alto de confiança (opcional, finito entre 0 e 1).
Confirmar exige revisão esperada, responsável/origem e registra nova revisão.
Tipo Inferencia não pode ser confirmado: reclassifique com evidência por Corrigir
e faça uma confirmação separada. Status e tipo continuam explícitos no DTO.

Corrigir/Atualizar do CRUD validam tudo antes de mudar o item, preservam identidade,
escopo, autoria original e snapshots anteriores. Correção de Confirmado retorna a
Inferido: a confirmação antiga não cobre conteúdo novo. Temporario continua
Temporario ao corrigir. Cada mutação recebe revisão esperada e timestamp não
anterior à última revisão, evitando sobrescrita silenciosa. Na Application,
timestamps vêm do relógio do host; timestamps e histórico recebidos no DTO são
ignorados. Cada snapshot guarda os campos canônicos, ator/origem e instante do ato.
Coleções do domínio são somente leitura; mappings fazem cópias para os DTOs.

Invalidar torna Inativo e preserva conteúdo/histórico. Substituir exige outro item
existente, não inativo/substituído, do mesmo espaço e mesmo projeto (inclusive
ambos sem projeto), grava IdConhecimentoSubstituto no anterior e o marca
Substituido. O substituto não é alterado. Inativo/Substituido são terminais nesta
entrega; não há reativação implícita, substituição por si mesmo ou ciclos por itens
já substituídos. Não implementamos o grafo geral da #153.

ValidoDesde/ValidoAte delimitam [desde, até), cada limite opcional, e um intervalo
com ambos deve ter até > desde. EstaValidoEm testa elegibilidade temporal/estado;
não promove inferências nem comprova verdade permanente. Não há expiração
automática gravando status: validade e status são eixos separados.

## Application e limites de integração

Criação exige espaço existente/ativo; projeto opcional precisa existir, estar ativo
e pertencer ao espaço. Todas as escritas existentes conferem o escopo armazenado,
recusando espaço/projeto arquivado. DTO de atualização não muda espaço/projeto,
IdAutor, estado ou substituto. AtualizarAsync é correção e usa dto.Revisao como
revisão esperada; ConfirmarAsync/InvalidarAsync/SubstituirAsync recebem esse valor
explicitamente. Cada sucesso confirma UoW uma vez; rejeição ou ausência não confirma.
Erro de UoW propaga, sem resposta falsa de sucesso; escopo deve ser descartado após
falha, conforme AD-43.

RemoverAsync do CRUD é exclusão explícita, distinta de invalidar: remove o agregado
(incluindo conteúdo/histórico) pelo repository. Remoção física, referências externas,
derivados e backups dependem de #160/#149/#153; esse método não oferece ainda o
fluxo de exclusão completo do produto. Nenhum endpoint/Telegram é adicionado.

Pesquisa exige espaço, limite 1–100, e pode filtrar projeto/tipo/status/tag e
ValidoEm. Projeto null pesquisa todo o espaço; SomenteSemProjeto restringe aos
itens diretos do espaço. Por padrão o repository omite Inativo/Substituido;
IncluirInativosOuSubstituidos permite inspeção explícita. ValidoEm opcional filtra
antes da limitação; sem ele não há filtro temporal (pesquisa para inspeção, não
Context Builder). Consultas e filtros pertencem ao repository específico.

Assim como Espaços/Projetos, AppService não é registrado por AddApplication antes
de existir implementação específica do repository (#160). Nenhuma tabela/migration
ou alteração do DbContext é feita na #138. Autorização/tenant/escopo por identidade
é da #150: pesquisa escopada e validação de associação não substituem essa policy;
a leitura por Id da base CRUD também permanece interna e sem autorização de canal.

Testes ConhecimentoTests/ConhecimentoAppServiceTests usam Domain, mappings reais e
fakes de repositories/UoW, sem banco, filesystem, Telegram ou CLIs.
