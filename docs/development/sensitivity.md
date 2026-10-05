# Sensibilidade e referências de segredo (#155)

A classificação canônica usa Publico, Pessoal, Trabalho, Confidencial e Secreto.
`LeituraDoBrainAppService` valida proprietário e escopo exato (inclusive projeto) antes de
produzir `LeituraProtegidaDto`. O adapter deve obter identidade/permissões de uma entrada
autorizada; flags não são inferidas do prompt. A autorização de canais da #150 permanece separada.

`PoliticaDeSensibilidade` centraliza leitura, busca, contexto automático, exportação e
indexação externa. Confidencial exige permissão explícita; Secreto só admite leitura
explícita autorizada, nunca busca de conteúdo, exportação, contexto automático ou embedding
externo. Metadados permitidos limitam-se a IDs/classificação/revisão: tags, conteúdo,
referência e histórico são omitidos quando protegidos. Exportadores e o futuro Context
Builder devem usar esta projeção, e nunca o DTO administrativo de CRUD.

Captura e correção rejeitam padrões óbvios de credencial em conteúdo, JSON, tags,
justificativa e proveniência antes de persistir. A mensagem não ecoa o valor. A defesa
não identifica todo segredo: a classificação explícita continua necessária. Use
`secret://host/NOME` como referência lógica; o Brain não resolve nem armazena seu valor.
Redaction é conservadora e protege o campo inteiro se houver padrão sensível.

Classificação pode ser corrigida pelo fluxo auditável existente (revisão esperada e
proveniência); histórico não é devolvido na projeção protegida. Nenhum conteúdo Brain
é enviado aos agentes no runtime atual; política está disponível antes dessa integração.
