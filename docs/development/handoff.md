# Protocolo de turnos e handoff

Parte normativa do [contrato de desenvolvimento](agent-contract.md).

Define como um worker entrega uma Issue inacabada ao próximo, e como o próximo a retoma
sem reiniciar a tarefa, sem redesenhar a solução e sem depender da sessão anterior.

O protocolo é agnóstico a agente. Ele não modela `Claude → Codex`, e sim:

```text
Worker A → Worker B
```

e por isso vale em qualquer direção e quantas vezes forem necessárias:

```text
Claude → Codex → Claude → ... → PR
```

## Onde vivem os registros

Os registros de turno são **comentários na GitHub Issue**:

```text
## TURNO ASSUMIDO     quem assume o turno
## HANDOFF            quem encerra o turno sem concluir
## TURNO FINALIZADO   quem conclui a implementação e entrega ao revisor
```

```bash
gh issue comment <numero> --body-file <arquivo>
gh issue view <numero> --comments
```

- feedback de review vive no **PR**, não na Issue;
- não existe `docs/HANDOFF.md` nem qualquer arquivo de handoff por sessão no
  repositório;
- registros anteriores não são editados nem apagados: **adiciona-se** um novo
  comentário, e o mais recente vence.

Se o `gh` não funcionar, reporte a limitação ao humano. Não grave estado transitório no
repositório como alternativa.

## Ownership de turno

Uma Issue tem, a cada momento, **no máximo um worker atual**. Issues diferentes podem
estar com workers diferentes; a mesma Issue, nunca com dois ao mesmo tempo.

### Quem é o worker atual

> O **registro de turno vigente** é o comentário mais recente da Issue cujo corpo começa
> com `## TURNO ASSUMIDO`, `## HANDOFF` ou `## TURNO FINALIZADO`. O campo `Estado:`
> dele diz se a Issue tem dono agora.

```text
TURNO ASSUMIDO   + Estado: em andamento           → há worker atual; NÃO assuma
HANDOFF          + Estado: aguardando continuação → livre; trabalho inacabado na branch
TURNO FINALIZADO + Estado: review                 → livre; implementação entregue, PR em revisão
nenhum registro                                   → ambíguo; RECOVERY MODE
```

**Só `em andamento` tem dono.** `aguardando continuação` e `review` são as duas formas de
um turno acabar, e nas duas a Issue fica livre.

```text
aguardando continuação ──assume──→ em andamento
                                     ├─ turno acaba, Issue inacabada → ## HANDOFF → aguardando continuação
                                     └─ implementação concluída, PR aberto → ## TURNO FINALIZADO → review
review ──correção de review pedida──→ em andamento (mesma branch, mesmo PR)
```

Todo caminho que tira um worker da Issue publica um registro. Não existe Issue
"em andamento sem dono" nem "livre com alguém trabalhando".

O estado da Issue no backlog (labels `status:*`) e o ownership de turno são conceitos
diferentes: a label diz em que fase a Issue está; o registro de turno diz **quem** está
nela agora.

### O claim de turno é obrigatório

Publicar `## TURNO ASSUMIDO` **antes da primeira alteração de arquivo** é obrigatório em
todo turno — inclusive no primeiro, e inclusive em RECOVERY. Sem ele, o registro vigente
continua dizendo que a Issue está livre, e um segundo agente pode, seguindo o protocolo
corretamente, começar a trabalhar na mesma branch.

O claim registra uma branch que **já existe**. Por isso a ordem é:

```text
NOVA TAREFA     criar a branch e confirmá-la → ## TURNO ASSUMIDO → primeira alteração
CONTINUAÇÃO     confirmar a branch recebida  → ## TURNO ASSUMIDO → primeira alteração
```

Se a criação da branch falhar, nenhum claim é publicado e a Issue não fica travada em
`em andamento` apontando para uma branch inexistente.

```markdown
## TURNO ASSUMIDO

Issue: #<numero>
Branch: <branch>
HEAD observado: <sha-curto> <assunto>
Worker atual: <claude | codex | ...>
Worker anterior: <quem entregou | nenhum>
Estado: em andamento
Base: <nova tarefa | link do ## HANDOFF continuado | correção de review do PR #<n> | RECOVERY — sem handoff>
Takeover: <não | sim — motivo e quem autorizou>
```

### Concluir a implementação também libera o turno

Abrir o PR encerra o turno, mas **não** é handoff: não há trabalho pela metade. Publicar
`## HANDOFF` ali criaria um registro falso de Issue inacabada. A conclusão tem registro
próprio, publicado depois de abrir o PR:

```markdown
## TURNO FINALIZADO

Issue: #<numero>
Branch: <branch>
HEAD: <sha-curto> <assunto>
Worker anterior: <claude | codex | ...>
Worker atual: nenhum
Estado: review
PR: #<numero do PR>
```

Sem ele, a Issue ficaria presa em `em andamento` e ninguém poderia assumir uma correção
de review sem takeover.

### Correção de review é um turno novo

`Estado: review` significa livre, não encerrado. Quando o revisor pede ajuste, qualquer
worker assume um turno novo:

```text
Estado: review
↓ ajuste solicitado no PR
↓ ## TURNO ASSUMIDO (Base: correção de review do PR #N)
↓ corrige na MESMA branch, push no MESMO PR
↓ ## TURNO FINALIZADO → review
```

Nunca se cria branch ou PR novo para corrigir review. Decision Locks e a implementação
recebida continuam valendo. Se a correção ficar pela metade, o turno termina com
`## HANDOFF` comum.

Num turno de correção, o `## HANDOFF` mais recente pode ser anterior à conclusão e estar
**vencido**: quem dirige o turno é o pedido de revisão no PR. Do handoff antigo
aproveitam-se os Decision Locks e o histórico.

### Encontrei `Estado: em andamento`

Outro worker detém o turno. **Não assuma.** Pare, reporte ao humano (quem, desde quando,
qual HEAD) e não altere nada.

**Takeover** existe porque uma sessão pode morrer depois de assumir o turno. Ele exige
**autorização humana explícita**, só se aplica a `em andamento` e é registrado com
`Takeover: sim — <motivo e quem autorizou>`. Tempo sozinho não autoriza nada. Depois do
takeover, o worker segue em [RECOVERY MODE](#recovery-mode).

`aguardando continuação` e `review` são estados livres: assumir a partir deles é turno
normal, sem takeover.

### Limite conhecido

O claim por comentário é coordenação persistente, não mutex atômico. Dois claims
publicados no mesmo instante não são arbitrados. Isso é aceitável enquanto quem aciona
os agentes é uma pessoa, um de cada vez. Se houver despacho automático concorrente, o
locking precisa ser endurecido (assignee, lock externo).

## Formato do handoff

Formato único, produzido e consumido por qualquer agente:

```markdown
## HANDOFF

Issue: #<numero>
Branch: <branch>
PR: <#numero | nenhum>
HEAD / checkpoint: <sha-curto> <assunto>
Worker anterior: <claude | codex | ...>
Worker atual: nenhum
Estado: aguardando continuação

### Objetivo

<objetivo da Issue em uma ou duas frases>

### Concluído

- ...

### Em andamento

- ...

### Próximos passos

1. ...

### Arquivos principais

- `caminho` — o que há nele

### Decisões tomadas

- ...

### Decision Locks

- ...

### Decisões inferidas

- ...

### Validações

<comandos executados e resultado real: build, testes>

### Problemas / riscos

- ...

### Alterações não commitadas

- ...

### Observações para o próximo worker

- ...
```

Regras:

- **todas** as seções aparecem; sem conteúdo, escreva `Nenhum.` / `Nenhuma.` — seção
  ausente é indistinguível de esquecimento;
- `Estado:` de um `## HANDOFF` é `aguardando continuação` com `Worker atual: nenhum`:
  publicar o handoff **é** liberar o turno;
- o handoff **não** nomeia o próximo worker; qualquer agente autorizado pode assumir;
- `HEAD / checkpoint` é o sha real da branch depois do push, para que o próximo worker
  detecte divergência;
- `Decisões inferidas` só é preenchida após RECOVERY; em turno normal, `Nenhuma.`;
- `Validações` registra o resultado real, não o desejado: teste quebrado é teste
  quebrado.

## Decision Locks

Um **Decision Lock** é uma decisão tomada durante a Issue e declarada fechada:

```text
### Decision Locks

- O resolvedor de contexto fica em Jobs/, não em Telegram/.
- /use não persiste entre reinícios nesta Issue.
```

O worker seguinte **não reabre** um Decision Lock por "eu faria diferente", "é mais
elegante" ou "costumo usar outra abordagem". Preferência de modelo não é evidência.

Um lock só pode ser contestado com evidência concreta:

- requisito conflitante na Issue;
- teste ou código demonstrando incompatibilidade;
- falha técnica objetiva e demonstrável;
- orientação humana explícita.

Havendo evidência:

```text
não redesenhar silenciosamente
↓
registrar o conflito na Issue, com a evidência
↓
interromper somente a parte afetada
↓
escalar ao humano
```

Locks acumulam entre turnos: cada handoff repete os locks ainda válidos e acrescenta os
novos. Um lock só sai da lista com decisão registrada.

## Respeitar a implementação recebida

O trabalho recebido é **presumido válido até prova em contrário**. O worker seguinte não:

- reimplementa o que já existe;
- refatora, renomeia ou troca abstrações por gosto;
- descarta mudanças não commitadas;
- sobrescreve trabalho do worker anterior.

Bug demonstrável se corrige — e o registro seguinte diz o que foi corrigido e por quê.

## Segurança da working tree

A working tree recebida é **evidência**: pode conter trabalho legítimo e não commitado de
um turno interrompido. Antes de entendê-la, são proibidos:

- `git reset --hard`;
- `git checkout -- .` / `git restore .`;
- `git clean`;
- `git stash` automático;
- `git pull` (é fetch + merge);
- force-push;
- troca de branch com árvore suja;
- descarte de arquivos que o agente não reconhece.

`git fetch` é seguro: atualiza refs remotas sem tocar em `HEAD` nem na árvore.

## Checkpoints

Um checkpoint é um commit em **unidade coerente** de trabalho, não um commit a cada
arquivo salvo. Commits e pushes intermediários são recomendados quando aumentam a
resiliência: trabalho no `origin` sobrevive ao fim abrupto de uma sessão.

O staging é **seletivo**:

```bash
git status --porcelain
git diff
git add <caminho> <caminho> ...     # nunca -A, nunca .
git diff --staged --stat
git commit -m "<tipo>: <resumo>"
git push -u origin <branch>
```

Arquivo que você não reconhece fica **fora** do commit — não é seu para commitar nem para
descartar — e é mencionado no handoff.

## Continuar turno

Ordem obrigatória: tudo que **inspeciona** vem antes de tudo que **altera**.

```text
 1. identificar a Issue (informada pelo humano, ou única candidata inequívoca)
 2. confirmar o status da Issue
 3. ler o registro de turno vigente e verificar o ownership
 4. ler contrato, contexto e o handoff vigente
 5. identificar a branch da Issue — nunca criar outra
 6. inspecionar o estado local sem alterá-lo
 7. git fetch
 8. comparar handoff × HEAD local × remoto
 9. sincronizar somente se for seguro
10. publicar ## TURNO ASSUMIDO
11. rodar o baseline do estado recebido
12. carregar Decision Locks e continuar do primeiro próximo passo
```

**1. Identificar a Issue.** Se o humano a informou, é ela. Senão, liste as candidatas
(`status:in-progress`, e `status:review` com correção solicitada no PR). Uma única
candidata inequívoca: use. Várias: desempate pelo campo `Branch:` do registro vigente
contra a branch atual; persistindo a dúvida, pergunte ao humano. Nenhuma: não há turno a
continuar.

**3. Ownership.**

```text
em andamento           → PARE e reporte (takeover só com autorização humana)
aguardando continuação → livre, prossiga
review                 → livre; prossiga só se há correção solicitada no PR
nenhum registro        → RECOVERY MODE
```

**5–6. Branch e estado local.** A branch vem do campo `Branch:` do handoff; sem handoff,
procure-a por `git branch -a` (`issue-<numero>` no nome). Criar branch nova para uma Issue
que já tem branch viola `1 Issue → 1 branch → 1 PR`.

```bash
git branch --show-current
git status --porcelain
git diff
git diff --staged
git log --oneline -10
git stash list
```

**7–8. Comparar.**

```bash
git fetch origin
git log --oneline HEAD..origin/<branch>    # remoto à frente?
git log --oneline origin/<branch>..HEAD    # local à frente, não empurrado?
git log --oneline origin/main..HEAD
```

```text
HEAD local == handoff        → estado é o descrito
HEAD local != handoff        → houve trabalho depois do handoff; o Git está certo
remoto à frente do local     → há commits que você não tem
local à frente do remoto     → commits nunca empurrados: NÃO os perca
árvore suja                  → trabalho não commitado: NÃO sincronize ainda
```

Divergência não é erro: é informação, e vai registrada no claim.

**9. Sincronizar só quando seguro.**

```text
Na branch errada + árvore limpa  → git switch <branch>
Na branch errada + árvore suja   → PARE e reporte
Árvore limpa + remoto à frente   → git merge --ff-only origin/<branch>
Qualquer outro caso              → não sincronize; trabalhe sobre o estado recebido
```

**11. Baseline.** `dotnet build Dante.sln` e `dotnet test Dante.sln` sobre o estado
recebido, para distinguir quebra herdada de quebra introduzida.

## Encerrar turno

Quando o turno acaba **sem** concluir a Issue:

```text
 1. revisar git status e diff
 2. separar o que pertence à Issue do que não pertence
 3. checkpoint com staging seletivo
 4. executar as validações possíveis
 5. push da branch
 6. publicar ## HANDOFF no formato canônico — isso libera o turno
```

- a Issue **não** muda de status (continua `status:in-progress`, ou `status:review` numa
  correção de review);
- depois de publicar o handoff, nenhum trabalho novo é iniciado.

Quando a implementação **foi concluída** neste turno, não é encerramento por handoff: é o
fluxo de conclusão — validação final, PR com `Closes #<numero>`, Issue para
`status:review` e `## TURNO FINALIZADO`. Ver
[Concluir a implementação também libera o turno](#concluir-a-implementação-também-libera-o-turno).

### Definition of Done do turno

```text
□ trabalho coerente commitado com staging seletivo
□ nada de terceiros ou fora do escopo no commit
□ branch empurrada
□ handoff publicado na Issue, com todas as seções
□ HEAD do handoff igual ao HEAD real da branch
□ Decision Locks acumulados
□ validações registradas como estão de fato
□ alterações não commitadas, se houver, declaradas
□ Worker atual: nenhum / Estado: aguardando continuação
```

## RECOVERY MODE

O protocolo não pode depender de o worker anterior ter conseguido encerrar o turno. Sem
handoff, ou com handoff visivelmente defasado em relação à branch, o próximo worker
entra em RECOVERY MODE:

```text
1. ler Issue e PR (escopo, critérios, comentários, review)
2. inspecionar branch e working tree sem modificar
3. git fetch
4. comparar local × remoto
5. revisar commits e diff (git log origin/main..HEAD, git diff origin/main...HEAD)
6. executar o baseline
7. inferir apenas o necessário
8. publicar ## TURNO ASSUMIDO com Base: RECOVERY — sem handoff
9. preservar todas as alterações recebidas
```

Ao reconstruir, separe:

```text
Decisões explícitas   escritas na Issue, em handoff anterior, no PR ou no contrato
Decisões inferidas    deduzidas do código existente, sem registro escrito
```

Nenhuma das duas é revertida automaticamente: uma decisão inferida tem o peso de um
Decision Lock enquanto não houver evidência contra ela. O primeiro handoff após o
recovery registra as inferidas em `Decisões inferidas`, promovendo-as a registro escrito.

Postura conservadora: preserve tudo, prefira completar o que está começado a recomeçar,
e na dúvida entre duas leituras do estado registre as duas em vez de escolher em
silêncio.

## O ciclo completo

```text
Issue #N status:ready
↓ Worker A: claim da Issue, branch, ## TURNO ASSUMIDO em andamento
↓ baseline, implementação parcial, checkpoint
↓ Worker A: ## HANDOFF                               aguardando continuação
↓ Worker B: inspeciona, ## TURNO ASSUMIDO            em andamento
↓ conclui, valida, abre PR, Issue → status:review
↓ Worker B: ## TURNO FINALIZADO                      review
↓ revisão no PR
├─ ajustes → qualquer worker: ## TURNO ASSUMIDO na mesma branch/PR → ... → ## TURNO FINALIZADO
└─ aprovado → merge humano → Issue fechada
```

E quando a sessão morre sem encerrar:

```text
## TURNO ASSUMIDO de A → sessão de A morre → registro vigente segue "em andamento"
↓ Worker B lê o ownership e PARA
↓ humano confirma e autoriza takeover
↓ B: ## TURNO ASSUMIDO com Takeover: sim → RECOVERY MODE
↓ ciclo normal continua
```

Uma Issue, uma branch, um PR — quantos turnos forem necessários.
