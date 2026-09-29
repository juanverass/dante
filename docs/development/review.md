# Protocolo de implementação e revisão

Parte normativa do [contrato de desenvolvimento](agent-contract.md).

Define o ciclo implementador → revisor → correção sem depender de chat: toda informação
necessária para revisar, corrigir ou decidir o próximo passo está na Issue, no PR e no
Git.

```text
Issue
  ↓
worker implementador
  ↓
branch + commits + testes
  ↓
PR  +  ## TURNO FINALIZADO na Issue
  ↓
worker revisor
  ↓
## REVIEW no PR
  ├─ Veredito: pronto para merge → merge humano
  └─ Veredito: ajustes necessários
         ↓
     ## TURNO ASSUMIDO (qualquer worker) — MESMA branch, MESMO PR
         ↓
     correções + testes
         ↓
     ## CORREÇÃO DE REVIEW no PR + ## TURNO FINALIZADO na Issue
         ↓
     nova revisão
```

**Onde vive cada coisa:** estado de turno na **Issue**; feedback de review e respostas a
ele no **PR**.

## Papéis padrão

| Papel | Worker padrão |
| --- | --- |
| Implementação (nova tarefa, continuação, correção de review) | **Claude** |
| Revisão (`## REVIEW`) | **Codex** |

É convenção, não restrição: o humano pode sobrescrever explicitamente o papel de
qualquer Issue ou PR (por exemplo, "Codex implementa a #N" ou "Claude revisa o PR #M"),
na instrução da tarefa ou em comentário na Issue/PR. O override vale para aquela Issue/PR
e não muda o padrão. Sem instrução humana, vale a tabela acima.

Os papéis não mudam a autoridade nem as regras: os dois agentes seguem o mesmo contrato,
e o protocolo de turnos é o mesmo qualquer que seja o worker. Um handoff continua sem
nomear o próximo worker; o papel padrão só orienta quem o humano aciona.

Implementador e revisor podem, por override, ser o mesmo agente, ou o mesmo agente em
sessões diferentes. Em todos os casos, quem revisa reconstrói o contexto a partir das
fontes persistidas — nunca da memória de ter implementado.

## Implementador

Responsável por entregar ao revisor um PR que se explica sozinho:

- implementar só o escopo da Issue, com testes para regra nova relevante;
- validar (build + testes) e revisar o próprio diff completo antes do PR;
- abrir o PR com `Closes #<numero>`, o que foi feito, e as **validações reais**
  executadas (comando e resultado);
- registrar no PR decisões relevantes e pendências fora do escopo;
- mover a Issue para `status:review` e publicar `## TURNO FINALIZADO` na Issue.

Quando a Issue passou por vários workers, o corpo do PR cobre a Issue inteira.

## Revisor

### Fontes obrigatórias

```bash
gh issue view <issue>                   # escopo e critérios de aceite
gh issue view <issue> --comments        # turnos, handoffs, Decision Locks
gh pr view <pr> --comments              # descrição, reviews e respostas anteriores
gh pr diff <pr>                         # diff completo
gh pr checks <pr>                       # checks, quando houver
```

Mais: [contrato](agent-contract.md), `docs/context/` e os Decision Locks da Issue. O
revisor roda build e testes sobre o HEAD do PR quando possível, sem alterar a branch:

```bash
git fetch origin
git switch --detach origin/<branch>     # só com a própria árvore limpa
dotnet build Dante.sln && dotnet test Dante.sln
```

### O que o revisor avalia

- critérios de aceite da Issue atendidos;
- escopo respeitado (nada faltando, nada além);
- correção: bugs demonstráveis, casos de borda, segurança;
- testes cobrindo a regra nova; testes existentes preservados;
- consistência com o contrato, com `ARCHITECTURE_DECISIONS.md` e com os Decision Locks;
- validações declaradas no PR batem com o que o revisor observou.

### O que o revisor não faz

- não redesenha a solução por gosto: preferência não é bloqueio;
- não reabre Decision Lock sem a evidência exigida pelo
  [protocolo](handoff.md#decision-locks);
- não faz commits na branch do PR: a correção é turno de um worker, registrado na Issue;
- não publica registros de turno na Issue: revisar não é assumir o turno;
- não faz merge.

### Classificação do feedback

| Classe | Significado | Efeito |
| --- | --- | --- |
| **Bloqueante** | defeito objetivo: critério de aceite não atendido, bug demonstrável, teste faltando para regra nova, violação do contrato ou de Decision Lock, validação quebrada | impede o merge; exige correção |
| **Não bloqueante** | melhoria local razoável, clareza, nome, comentário | não impede o merge; o implementador pode acatar na correção ou justificar |
| **Fora do escopo** | ideia futura, refactor maior, feature adjacente | não entra neste PR; vira Issue se o humano quiser |

Todo bloqueante é **objetivo e acionável**: aponta onde (`arquivo:linha`), qual a
evidência e o que se espera. "Eu faria diferente" não é bloqueante.

### Formato do review

Publicado no PR como review do tipo **COMMENT**:

```bash
gh pr review <pr> --comment --body-file <arquivo>
```

```markdown
## REVIEW

PR: #<pr>
Issue: #<issue>
HEAD revisado: <sha-curto>
Revisor: <claude | codex | humano>
Veredito: <pronto para merge | ajustes necessários>

### Bloqueantes

1. `arquivo:linha` — problema — evidência — ação esperada

### Não bloqueantes

- ...

### Fora do escopo

- ...

### Validações executadas

<comandos e resultado real>
```

Todas as seções aparecem; vazias levam `Nenhum.`. Veredito `pronto para merge` exige
`Bloqueantes: Nenhum.`; qualquer bloqueante implica `ajustes necessários`.

**Compatibilidade com o GitHub.** A mesma conta pode ser autora e revisora do PR, e o
GitHub impede `APPROVE`/`REQUEST_CHANGES` no próprio PR. Por isso o sinal canônico é o
campo `Veredito:` de um review `COMMENT`. `APPROVE`/`REQUEST_CHANGES` podem ser usados
quando possíveis, mas nada no protocolo depende deles.

## Correção de review

Veredito `ajustes necessários` torna a Issue candidata a
[correção de review](handoff.md#correção-de-review-é-um-turno-novo):

1. qualquer worker publica `## TURNO ASSUMIDO` na Issue com
   `Base: correção de review do PR #<pr>` (via `continuar-turno`);
2. trabalha na **mesma branch**; nunca cria branch ou PR novo;
3. corrige os bloqueantes; não bloqueantes acatados ou justificados;
4. valida e faz push no **mesmo PR**;
5. responde no PR:

   ```markdown
   ## CORREÇÃO DE REVIEW

   Review respondido: <link do ## REVIEW>
   HEAD: <sha-curto> <assunto>

   ### Bloqueantes

   1. <resumo> — corrigido em <sha/arquivo> | contestado: <evidência>

   ### Não bloqueantes

   - <acatado | não acatado: motivo>

   ### Validações

   <comandos e resultado real>
   ```

6. publica `## TURNO FINALIZADO` na Issue — o PR volta ao revisor.

A Issue permanece `status:review` durante toda a correção. Se a correção ficar pela
metade, o turno termina com `## HANDOFF` comum.

Um bloqueante que o implementador considera incorreto é **contestado com evidência** na
resposta, não ignorado; a decisão final, se persistir o desacordo, é humana.

## Qual é o próximo passo?

Uma sessão nova decide o que fazer numa Issue `status:review` lendo, nesta ordem, o
registro de turno vigente na Issue e o `## REVIEW` mais recente no PR:

```text
registro vigente "em andamento"                       → alguém corrigindo; aguardar
TURNO FINALIZADO sem ## REVIEW posterior               → aguardando revisão (revisar, se pedido)
## REVIEW posterior com "ajustes necessários"          → correção de review (continuar-turno)
## REVIEW posterior com "pronto para merge"            → aguardar merge humano
## CORREÇÃO DE REVIEW mais recente que o último REVIEW → aguardando nova revisão
```

"Posterior" compara datas: o `## REVIEW` só vale para o HEAD que revisou. Um review
anterior ao `## TURNO FINALIZADO` mais recente já foi respondido.

## Merge

Merge é decisão humana, depois de veredito `pronto para merge`, por **squash merge**. O
`Closes #<numero>` fecha a Issue.
