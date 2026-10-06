# Brain por conversa natural

Com ConnectionStrings:Dante configurada e migrations aplicadas, o Worker intercepta
intenções Brain antes de chamar as CLIs. A mesma allowlist Telegram resolve identidade
persistente; texto nunca define identidade/permissões. Usuário seleciona espaço/projeto
por nome ou número de uma lista recente. Sem configuração, mensagens comuns mantêm o
fluxo legado. Sem identidade ou escopo resolvido, o Brain falha fechado.

Exemplos:

- `criar espaço Pessoal`, `listar espaços`, `usar espaço Trabalho`;
- `criar projeto Dante`, `listar projetos`, `usar projeto Dante`, `usar sem projeto`;
- `o que você sabe sobre Guid?`, `já resolvemos algo parecido com deadlock?`;
- `documente como resolvemos isso` (pede o trecho), ou `registre no Brain: texto`;
- `registre no Brain: incidente: descrição`, `solucao: ...`, `aprendizado: ...` etc.;
- `essa informação está errada`, `corrija a primeira para texto correto`;
- `invalide a segunda`, `esqueça isso`;
- `essa solução resolveu aquele incidente`, ou `relacione a solução 2 ao incidente 1`;
- `de onde veio essa informação?`, `mostre a origem da primeira`;
- `confirmar`, `cancelar`, `listar candidatos`, `confirmar primeira`;
- `inspecione o Brain`, `exporte o Brain`; `/brain ajuda` é fallback explícito.

Intenções são determinísticas, sem chamada LLM para autorização/seleção. Busca de
experiência filtra incidente/solução/aprendizado/procedimento antes da paginação.
Consulta informa tipo/status/classificação/origem; fonte bruta não é apresentada como
fato. Origem mostra documento/revisão/evidência, sem exigir IDs internos. Exportação
conversacional tem limite de 12000 caracteres; arquivos maiores usam o fallback local.

Captura explícita cria CandidatoDeConhecimento, nunca fato silencioso. Responder
citando um trecho seleciona só aquele trecho; não persiste transcript completo. Sem
conteúdo selecionado, pede descrição. Confirmar consolida; inferência continua inferida.
Cancelar descarta candidato pendente e preserva auditoria. Lista de candidatos permite
retomar aprovação após reinício, sem pedir IDs. Capture <=10000 caracteres; queries <=2000.

Correção/invalidação/relação precisam de alvos inequívocos e confirmação adicional.
Selecionar um número aponta para o resultado exibido, não para um ID fornecido no texto.
Confirmação valida revisão e é consumida atomicamente uma vez. Correção mantém histórico
e volta a Inferido; esquecer invalida, preserva histórico e impede reinjeção, sem apagar
fisicamente evidência. Uma nova intenção cancela proposta anterior; nada é alterado
quando o alvo é ambíguo, mudou ou a confirmação expirou.

Seleção e propostas ficam em memória, isoladas por tenant/usuário/chat/tópico/escopo;
propostas expiram em 5 minutos e resultados em 10. Não são memória permanente nem
transcript. Troca de espaço/projeto e /clear ou /compact limpam somente esse estado
transitório; não gravam/apagam Brain automaticamente. Reclassificação/grants de
Confidential/Secret não são inferidos da conversa; Telegram usa permissões padrão.

Não há editor web, autenticação comercial, modelo de linguagem exigido ou integração
externa adicional. Integração automática dos pacotes do Context Builder nas sessões
continua na #145; intenções Brain são atendidas pelo núcleo e não enviadas ao agente.

Fontes textuais também podem ser enviadas sem editar o banco:
`importe nota Título: conteúdo` ou `importe Markdown Título: # Cabeçalho ...`.
Consultar recupera fontes brutas; `consolide a primeira fonte` prepara candidato do
trecho exato com documento/revisão/hash/parte, ainda exigindo confirmação. Reimportar
uma origem existente pede confirmação da atualização. Remover um resultado de fonte
explica que apagará a fonte inteira e seus índices, preservando conhecimento consolidado,
e exige confirmação adicional. Tópicos Telegram distintos não compartilham propostas.

Documentação local explicitamente selecionada tem fallback administrativo, restrito
à raiz DANTE_BRAIN_IMPORT_ROOT já configurada pelo host:

```bash
dotnet run --project src/Dante.Worker -- --brain source-import USUARIO SPACE PROJETO /raiz/documento.md REVISAO
```

`-` representa ausência de projeto/revisão inicial. Reimportação exige a revisão atual;
o comando não sobrescreve fontes silenciosamente e usa o tenant configurado no host.

Correção natural altera o conteúdo textual e preserva demais campos, inclusive JSON,
tags, validade e classificação; não aplica patches implícitos aos dados estruturados.
