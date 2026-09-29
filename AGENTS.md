# Instruções para agentes (Codex)

Este arquivo é um adaptador fino. As regras permanentes vivem uma única vez no
[contrato de desenvolvimento](docs/development/agent-contract.md), que vale igualmente
para Claude Code, Codex e qualquer agente futuro. Em caso de divergência, o contrato
vence. Não redefina arquitetura, escopo ou backlog a partir de uma conversa.

## Antes de começar qualquer tarefa

1. Leia o [contrato de desenvolvimento](docs/development/agent-contract.md).
2. Leia [PROJECT_CONTEXT](docs/context/PROJECT_CONTEXT.md) e
   [CURRENT_STATE](docs/context/CURRENT_STATE.md). Se a tarefa tocar arquitetura,
   leia também [ARCHITECTURE_DECISIONS](docs/context/ARCHITECTURE_DECISIONS.md).
3. Leia a Issue/PR da tarefa e o registro de turno vigente na Issue. Confirme o
   ownership antes de assumir; siga o [protocolo de turnos](docs/development/handoff.md).
4. Verifique Git, código e testes atuais: memória de sessão não substitui o estado real.
5. Descubra o modo pelo status da Issue ([backlog](docs/development/backlog.md)): trabalho
   novo só de `status:ready`, nunca Epic. Em tarefa nova, faça o claim
   `ready → in-progress` e crie a branch; em continuação ou correção de review, confirme
   a mesma branch e o mesmo PR. Com a branch estabelecida, publique `## TURNO ASSUMIDO`
   na Issue antes da primeira alteração de arquivo.

## Durante e ao final

Siga o contrato: workflow Git, disciplina de escopo, staging seletivo, validação e
Pull Request.

Skills: `$continuar-turno` e `$encerrar-turno` (em [`.agents/skills/`](.agents/skills/)) executam os
procedimentos de continuar e encerrar turno do protocolo.

Ao revisar um PR, siga o [protocolo de review](docs/development/review.md): reconstrua o
contexto pela Issue, PR e diff, e publique `## REVIEW` no PR com veredito explícito.

Ao encerrar: implementação concluída → PR e `## TURNO FINALIZADO`; trabalho inacabado →
checkpoint e `## HANDOFF`. Os dois vivem como comentários na Issue, nunca em arquivo do
repositório.
