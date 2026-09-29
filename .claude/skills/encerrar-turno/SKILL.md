---
name: encerrar-turno
description: Encerra o turno atual numa Issue. Se a implementação está incompleta, faz checkpoint seletivo, push e publica ## HANDOFF na Issue, liberando o ownership. Se está concluída, segue o fluxo de PR e publica ## TURNO FINALIZADO. Use quando a sessão vai acabar, o contexto está se esgotando, o trabalho precisa passar a outro worker ou a implementação/correção de review terminou.
---

# Encerrar turno

Adaptador do Claude Code para o procedimento
[*Encerrar turno*](../../../docs/development/handoff.md#encerrar-turno).

Esta skill **não define regras**. A semântica vive em
`docs/development/handoff.md`. Em qualquer divergência, **o protocolo vence**.

Leia antes: `docs/development/handoff.md` — *Encerrar turno*, *Formato do handoff*,
*Decision Locks* e *Checkpoints*.

## 1. É fim de turno ou fim da implementação?

```text
Implementação da Issue concluída neste turno?
├── SIM (tarefa nova ou continuação) → caminho B: PR + ## TURNO FINALIZADO
├── SIM (correção de review)         → caminho B, no PR existente
└── NÃO                              → caminho A: checkpoint + ## HANDOFF
```

Nunca publique `## HANDOFF` para concluir: ele afirma trabalho pela metade que não existe.

## 2. Inspecionar a árvore

```bash
git status --porcelain
git diff
git diff --staged
git log --oneline origin/main..HEAD
```

Decida arquivo a arquivo o que pertence à Issue. Arquivo que você não reconhece fica
**fora** do commit e é citado no registro.

## 3. Checkpoint (somente se houver o que commitar)

```text
Há alterações da Issue na árvore (modificadas, staged ou novas)?
├── SIM → staging seletivo + commit
└── NÃO → não há checkpoint a criar: o HEAD atual É o checkpoint; siga para o push
```

Com alterações da Issue:

```bash
git add <caminho> <caminho> ...
git diff --staged --stat
git commit -m "<tipo>: <resumo> (#<numero>)"
```

Proibido como receita: `git add .`, `git add -A`, `git commit -a`. Não tente
`git commit` com a árvore limpa: ele falha com *nothing to commit* e não é erro do turno.

Em qualquer dos casos, empurre a branch — inclusive commits locais de checkpoints
anteriores ainda não empurrados:

```bash
git log --oneline origin/<branch>..HEAD    # commits ainda não empurrados (pode ser vazio)
git push -u origin <branch>                # "Everything up-to-date" também é sucesso
```

## 4. Validações reais

```bash
dotnet build Dante.sln
dotnet test Dante.sln
git log -1 --format='%h %s'
```

Registre o resultado real — teste quebrado é teste quebrado.

## Caminho A — trabalho inacabado: `## HANDOFF`

Publique o handoff no
[formato canônico](../../../docs/development/handoff.md#formato-do-handoff), com **todas**
as seções (`Nenhum.`/`Nenhuma.` quando vazias):

```bash
gh issue comment <numero> --body-file <arquivo>
```

Campos que não se negociam:

```text
HEAD / checkpoint: <sha real da branch após o push — o HEAD atual se não houve commit novo>
Worker anterior: claude
Worker atual: nenhum
Estado: aguardando continuação
```

- a Issue **não** muda de status;
- o handoff não nomeia o próximo worker;
- Decision Locks de handoffs anteriores ainda válidos são repetidos;
- confira a *Definition of Done do turno* e mostre o link do comentário ao humano;
- **depois disso, não inicie trabalho novo.**

## Caminho B — implementação concluída: `## TURNO FINALIZADO`

1. validação final (build + testes) e revisão do diff completo;
2. push; abrir o PR para `main` com `Closes #<numero>` (ou, em correção de review, push
   no PR existente e resposta ao review no PR);
3. Issue `status:in-progress → status:review` (em correção de review já está `review`):

   ```bash
   gh issue edit <numero> --remove-label status:in-progress --add-label status:review
   ```

4. publicar na Issue:

   ```markdown
   ## TURNO FINALIZADO

   Issue: #<numero>
   Branch: <branch>
   HEAD: <sha> <assunto>
   Worker anterior: claude
   Worker atual: nenhum
   Estado: review
   PR: #<pr>
   ```

5. relatório final (ver contrato) no PR e ao humano; working tree limpa.

## Limites

Esta skill nunca:

- usa `git add .`, `git add -A` ou `git commit -a`;
- commita arquivo que não pertence à Issue, ou descarta um que não é seu;
- devolve a Issue para `status:ready` ao encerrar com handoff;
- publica `## HANDOFF` para uma implementação concluída;
- maquia o estado das validações;
- edita ou apaga registros anteriores — publica um novo comentário;
- faz merge.
