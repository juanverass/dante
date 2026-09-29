# Backlog e status de Issues

Parte normativa do [contrato de desenvolvimento](agent-contract.md).

As **GitHub Issues deste repositório são a fila oficial de trabalho** do D.A.N.T.E. O
que não está numa Issue não está planejado, e o que não está `status:ready` não está
disponível como trabalho novo — nem para uma pessoa, nem para um agente.

## Tipos de item

| Tipo | Como se reconhece | Implementável? |
| --- | --- | --- |
| **Epic** | label `type:epic` (título `[Epic] ...`) | **Não.** Agrupa Issues filhas por checklist; não gera branch nem PR. |
| **Issue executável** | qualquer Issue sem `type:epic` | Sim: `1 Issue → 1 branch → 1 PR`. |

Uma Issue grande demais para um PR revisável deve ser decomposta sob uma Epic.

## Labels

### `status:*` — em que fase a Issue está

| Label | Significado |
| --- | --- |
| `status:backlog` | Identificada, ainda não pronta (falta refinamento, decisão ou priorização). Não iniciar. |
| `status:ready` | Refinada, sem dependência aberta. **Única fila de trabalho novo.** |
| `status:in-progress` | Assumida; implementação em andamento. Não é trabalho novo, mas pode ser **continuada** pelo protocolo de turno. |
| `status:blocked` | Dependência ou impedimento não resolvido. Não iniciar. |
| `status:review` | Implementação entregue, PR aberto. Só volta a um agente quando há correção de review solicitada. |

Toda Issue aberta e executável tem **exatamente um** `status:*`. Epics não precisam de
`status:*`: seu estado é o checklist das filhas.

### `type:epic`

Marca uma Epic. É a única label de tipo exigida pelo processo; as labels padrão do
GitHub (`bug`, `enhancement`, `documentation`) podem ser usadas para classificar o resto,
mas nada no processo depende delas.

Não se cria taxonomia adicional (prioridade, área, paralelização) sem necessidade real.

## Dependências

Toda Issue executável declara dependências no corpo:

```text
## Dependências

Depende de:
- #123
```

ou `Nenhuma.` quando não houver. A relação com a Epic (`Relacionado à #40`) não é
dependência.

**Dependência aberta bloqueia a execução, mesmo que a label diga `status:ready`.** Nesse
caso o agente aponta a inconsistência em vez de começar. Uma exceção: uma Issue pode ser
executada em PR **empilhado** sobre o PR da dependência quando o humano pedir ou a
Epic indicar essa ordem, desde que o PR declare a pilha.

## Fluxo de status

```text
status:backlog → status:ready → status:in-progress → status:review → closed
                                        ↑
                              status:blocked (quando surge impedimento)
```

| Momento | Transição | Quem |
| --- | --- | --- |
| Issue criada | `status:backlog` | template / humano |
| Refinada, sem dependência aberta | `backlog → ready` | humano |
| Dependência ou impedimento aparece | `→ blocked` | humano (agente recomenda) |
| Selecionada, **antes da primeira alteração** | `ready → in-progress` | worker que assume |
| PR de conclusão aberto | `in-progress → review` | worker que conclui |
| Correção de review solicitada | permanece `status:review` | — |
| Turno encerrado com handoff | permanece `status:in-progress` | — |
| Execução abandonada sem PR e sem handoff | `in-progress → ready` (ou `blocked`), com motivo | worker |
| PR mesclado | Issue fechada por `Closes #N` | GitHub |

Encerrar um turno sem concluir **não** devolve a Issue para `ready`: ela continua
`status:in-progress` e a continuidade segue pelo [protocolo de turnos](handoff.md).

## Selecionar trabalho novo

```bash
gh issue list --state open --label status:ready
```

Um agente só assume como trabalho novo uma Issue que:

```text
□ tem status:ready
□ não tem type:epic
□ não tem dependência aberta
```

Então, **antes da primeira alteração**:

```bash
gh issue edit <numero> --remove-label status:ready --add-label status:in-progress
git switch -c <prefixo>/issue-<numero>-<slug>
git branch --show-current
gh issue comment <numero> --body-file <claim>      # ## TURNO ASSUMIDO, com a branch já criada
```

Se a criação da branch falhar, não publique o claim de turno.

Nenhuma candidata: reporte ao humano. Não escolha Issue `backlog`, `blocked`,
`in-progress` ou `review` como trabalho novo, e não implemente Epic.

## Estado da Issue × ownership de turno

São dois conceitos distintos:

| Conceito | Pergunta | Onde vive | Muda |
| --- | --- | --- | --- |
| **Status da Issue** | em que fase a Issue está? | label `status:*` | poucas vezes por Issue |
| **Ownership de turno** | quem está trabalhando nela agora? | registro de turno vigente (comentário) | a cada turno |

O claim da Issue (`ready → in-progress`) acontece **uma vez**: tira a Issue da fila de
trabalho novo. O claim de turno (`## TURNO ASSUMIDO`) acontece **a cada turno**: impede
dois workers na mesma Issue ao mesmo tempo. Ver
[Ownership de turno](handoff.md#ownership-de-turno).

```text
status:in-progress + registro vigente "em andamento"           → alguém trabalhando; não tocar
status:in-progress + registro vigente "aguardando continuação" → continuar pelo protocolo
status:review      + registro vigente "review"                 → com o revisor; só com correção pedida
```

## Concorrência

- `status:in-progress` impede que uma segunda execução selecione a Issue como trabalho
  novo;
- `## TURNO ASSUMIDO` impede que dois workers continuem a mesma Issue ao mesmo tempo;
- Issues diferentes podem rodar em paralelo apenas em working trees isoladas (worktree
  ou clone), uma branch por Issue. Dois agentes nunca alteram a mesma working tree ao
  mesmo tempo.

## O que o agente não decide

Pode: executar Issues `ready`, continuar Issues em andamento, detectar dependência não
documentada, apontar inconsistência, recomendar bloqueio ou decomposição — informando.

Não pode, sem pedido humano explícito: criar itens no roadmap, marcar Issue como
`status:ready`, desbloquear `status:blocked`, remover dependência, redefinir escopo ou
fazer merge.

As transições `ready → in-progress` e `in-progress → review` são exceção: fazem parte da
execução da Issue e são obrigação do worker.

## Templates

- [`.github/ISSUE_TEMPLATE/task.yml`](../../.github/ISSUE_TEMPLATE/task.yml) — Issue
  executável (aplica `status:backlog`);
- [`.github/ISSUE_TEMPLATE/epic.yml`](../../.github/ISSUE_TEMPLATE/epic.yml) — Epic
  (aplica `type:epic`);
- [`.github/pull_request_template.md`](../../.github/pull_request_template.md) — PR com
  `Closes #`, implementação e validação.

## Migração das Issues existentes

Issues criadas antes deste processo não tinham labels de status. A migração é aditiva e
não altera título nem corpo:

- Epics recebem `type:epic` (o prefixo `[Epic]` no título é mantido);
- Issues do Agent Harness recebem o status correspondente ao seu estado real;
- as demais Issues abertas ainda não refinadas recebem `status:backlog`. Promovê-las a
  `status:ready` é decisão humana de refinamento.

Assim, desde a migração, toda Issue aberta e executável tem exatamente um `status:*`.
Uma Issue encontrada sem `status:*` é inconsistência: o agente não a executa e reporta
ao humano.
