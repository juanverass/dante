---
name: continuar-turno
description: Assume o turno de uma Issue já em andamento (status:in-progress) ou uma correção de review (status:review com ajuste pedido no PR), na mesma branch e no mesmo PR, seguindo o protocolo de turnos do repositório. Use quando o trabalho foi iniciado por outro worker ou por uma sessão anterior, quando o revisor pediu ajustes, e também quando não houver handoff (RECOVERY MODE).
---

# Continuar turno

Adaptador do Codex para o procedimento
[*Continuar turno*](../../../docs/development/handoff.md#continuar-turno).

Esta skill **não define regras**. A semântica vive em
`docs/development/agent-contract.md` e `docs/development/handoff.md`. Em qualquer
divergência, **o protocolo vence**.

## 1. Carregar as fontes

Leia, antes de qualquer outra ação:

1. `docs/development/handoff.md` — *Ownership de turno*, *Continuar turno*,
   *Decision Locks*, *Segurança da working tree* e *RECOVERY MODE*;
2. `docs/development/agent-contract.md` — *Backlog e modos de execução* e *Workflow Git*;
3. `docs/context/PROJECT_CONTEXT.md` e `docs/context/CURRENT_STATE.md`;
4. `AGENTS.md`.

## 2. Issue, status e ownership — antes de tocar no repositório

Se o humano informou a Issue, é ela. Senão:

```bash
gh issue list --state open --label status:in-progress
gh issue list --state open --label status:review
```

Uma candidata inequívoca → use. Várias → desempate pelo `Branch:` do registro vigente
contra a branch atual; persistindo a dúvida, pergunte ao humano. `status:review` só é
candidata com correção pedida no PR. Nenhuma → reporte e pare.

```bash
gh issue view <numero>
gh issue view <numero> --comments          # registros de turno e handoff
gh pr list --state open --search "<numero> in:body"
gh pr view <pr> --comments                  # review, em correção de review
```

Registro de turno vigente = comentário mais recente que começa com `## TURNO ASSUMIDO`,
`## HANDOFF` ou `## TURNO FINALIZADO`:

```text
em andamento           → PARE e reporte. Só prossiga com takeover autorizado por humano.
aguardando continuação → livre; continue o trabalho inacabado.
review                 → livre; continue só se há correção pedida no PR.
nenhum registro        → RECOVERY MODE.
```

## 3. Inspecionar o estado local — sem alterar nada

```bash
git branch --show-current
git status --porcelain
git diff
git diff --staged
git log --oneline -10
git stash list
git branch -a --list "*issue-<numero>*"
```

A branch é a do campo `Branch:` do handoff (ou a única branch `*issue-<numero>*`). Nunca
crie outra.

## 4. Remoto e comparação

```bash
git fetch origin
git log --oneline HEAD..origin/<branch>
git log --oneline origin/<branch>..HEAD
git log --oneline origin/main..origin/<branch>
```

Compare com o `HEAD / checkpoint` do handoff. Divergência é informação: o Git está certo.

## 5. Sincronizar só quando seguro

```text
branch errada + árvore limpa  → git switch <branch>
branch errada + árvore suja   → PARE e reporte
árvore limpa + remoto à frente → git merge --ff-only origin/<branch>
outro caso                    → não sincronize; registre a divergência no claim
```

## 6. Publicar o claim — antes da primeira alteração

```bash
gh issue comment <numero> --body-file <arquivo>
```

```markdown
## TURNO ASSUMIDO

Issue: #<numero>
Branch: <branch>
HEAD observado: <sha> <assunto>
Worker atual: codex
Worker anterior: <claude | codex | nenhum>
Estado: em andamento
Base: <link do ## HANDOFF | correção de review do PR #<n> | RECOVERY — sem handoff>
Takeover: <não | sim — motivo e quem autorizou>
```

Não refaça o claim `ready → in-progress`: em continuação ele já aconteceu.

## 7. Baseline do estado recebido

```bash
dotnet build Dante.sln
dotnet test Dante.sln
```

Em WSL sem SDK Linux: `"/mnt/c/Program Files/dotnet/dotnet.exe"`.

## 8. Reportar e continuar

```text
Modo:              CONTINUAÇÃO | CORREÇÃO DE REVIEW | RECOVERY
Issue:             #<n> — <título> (<status>)
Ownership:         livre (aguardando continuação | review) | takeover autorizado por <quem>
Claim:             <link>
Branch / HEAD:     <branch> / <sha> <assunto>
HEAD × handoff:    coincide | divergente
Árvore recebida:   limpa | suja (<n> arquivos, preservados)
Local × remoto:    em dia | local à frente (<n>) | remoto à frente (<n>)
Concluído:         ...
Próximos passos:   ...
Decision Locks:    ...
Decisões inferidas: ... (só em recovery)
Baseline:          build <ok|falha> / testes <n ok, n falhas>
```

Continue do primeiro item de *Próximos passos* — ou, em correção de review, do primeiro
ponto bloqueante do review no PR.

## Limites

Esta skill nunca:

- trabalha sem publicar `## TURNO ASSUMIDO`;
- assume `em andamento` sem takeover autorizado; nem exige takeover para `aguardando
  continuação` ou `review`;
- cria branch ou PR novos para Issue que já tem, inclusive em correção de review;
- executa `git pull`, `git switch`, `reset --hard`, `checkout -- .`, `clean`, `stash`
  automático ou force-push antes de inspecionar a árvore — nem depois, sobre trabalho
  recebido;
- troca de branch ou sincroniza com a árvore suja;
- reabre Decision Lock por preferência, muda escopo ou reimplementa o que existe.
