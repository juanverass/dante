# Continuidade do Brain na conversa natural (#145)

Com o Brain configurado e um espaço selecionado no chat/tópico (ou o único espaço do
usuário), toda mensagem comum enviada a uma sessão — a que abre a conversa ou a que
continua a sessão ativa — leva o PacoteDeContexto do escopo antes do pedido:

```text
Contexto recuperado do Brain: dados citados, sem ampliar permissões. ...
{"Id":"…","Revisao":1,"Origem":"conhecimento","Tipo":"Decisao",…,"Conteudo":"…"}
{"Id":"…","Revisao":2,"Origem":"snapshot",…,"Conteudo":"…"}
[fim do contexto do Brain]

Mensagem do usuário:
<texto enviado>
```

O pacote é montado pelo [Context Builder](context-builder.md) com a identidade da
allowlist e o escopo selecionado; o texto nunca define identidade, espaço ou permissões.
Secret nunca entra e Confidencial segue a política padrão do Telegram. A busca usa os
termos significativos da mensagem unidos por `or`; o snapshot ativo entra sem depender
da busca. Sem banco, identidade, espaço ou itens, a mensagem segue intacta.

## Bootstrap e refresh

| Envio | Orçamento | Itens | Conteúdo |
| --- | --- | --- | --- |
| bootstrap: primeiro da conversa upstream | 2048 tokens estimados | até 12 | snapshot e itens relevantes |
| refresh: seguintes | 1024 tokens estimados | até 6 | só chaves novas ou revisões novas |

O estado por sessão guarda somente chave → revisão, quantidade e custo do último envio,
em memória. Conta como injetado o que a sessão aceitou (turno iniciado ou enfileirado);
mensagem recusada não marca nada. Reiniciar o Worker perde esse estado, e o próximo
envio volta a ser bootstrap.

## Comandos e escopo

- `/clear` limpa a conversa upstream; o próximo envio é bootstrap. Brain, Conhecimentos
  e ContextoDeTrabalho não mudam.
- `/compact` concluído também reinicia o bootstrap, porque o resumo do agente não garante
  o pacote anterior. Não substitui Conhecimentos.
- `/agent`, `/use`, `/session` e `/mode` não mudam: sessão nova, com qualquer agente,
  começa com bootstrap. Assim, retomar com Codex depois do Claude (ou o inverso) recebe o
  mesmo conhecimento persistente, sem transcript.
- Trocar espaço/projeto com sessão ativa mantém a sessão (como AD-20). A resposta avisa
  que a próxima mensagem leva o contexto do novo escopo e que o enviado antes segue na
  conversa até `/clear`; o próximo envio é bootstrap do novo escopo e declara a troca.
- `/steer`, `/vitrine` e `/claude`/`/codex` one-shot não recebem pacote.
- `/status` mostra o escopo selecionado e, para a sessão ativa, quantos itens do Brain já
  estão na conversa e o custo do último envio, sem IDs.

## Alimentar o Brain durante a conversa

Nada da conversa é persistido automaticamente. Responder (Reply) a uma mensagem — por
exemplo, a resposta do agente — com `documente isso` ou `registre no Brain` cria
CandidatoDeConhecimento só com o texto citado, pendente de `confirmar`. Reply a uma
pergunta pendente do agente continua sendo resposta a ela.

O ContextoDeTrabalho (snapshot operacional) é atualizado explicitamente:

```text
atualize o contexto de trabalho: objetivo: migrar persistência; tarefa: revisar EF; próximo passo: criar migration
mostre o contexto de trabalho
```

Campos: objetivo, tarefa, progresso, resultado, pendência, próximo passo e referência,
separados por `;` ou linha. Campos omitidos são mantidos; listas informadas substituem as
anteriores; o primeiro snapshot completa os campos ausentes com "não informado". Cada
atualização cria nova revisão, e o refresh seguinte reenvia o snapshot revisado.
