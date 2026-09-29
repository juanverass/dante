# Contrato de desenvolvimento para agentes

Contrato permanente de trabalho do D.A.N.T.E. Vale para **todo desenvolvedor deste
repositório — humano ou agente de IA** — e para toda tarefa executada aqui, sem
depender do histórico de nenhuma conversa.

Este é o documento neutro: nenhuma regra daqui depende da ferramenta que está
executando. Cada agente tem um adaptador fino que aponta para cá:

```text
              docs/development/agent-contract.md
                (este arquivo — fonte de verdade)
                    /                    \
               CLAUDE.md               AGENTS.md
                   │                       │
              Claude Code                Codex
```

Os adaptadores dizem **onde ler**; as regras permanentes existem uma única vez, aqui.

## Vocabulário

| Termo | Significado |
| --- | --- |
| **agente** / **worker** | Um desenvolvedor de IA trabalhando neste repositório. Hoje: Claude Code e Codex. |
| **worker atual** | O agente que detém o turno de uma Issue em andamento naquele momento. |
| **turno** | O período em que um worker trabalha numa Issue, de quando assume até encerrar ou ser interrompido. |
| **handoff** | O registro persistente do estado de uma Issue inacabada, que permite ao próximo worker continuar. |
| **Decision Lock** | Decisão tomada durante uma Issue que o worker seguinte não reabre por preferência. |

Claude e Codex têm **a mesma autoridade** e obedecem ao **mesmo contrato**, inclusive
quando um atua como implementador e o outro como revisor. Não existe agente principal e
agente reserva; existe worker atual e próximo worker.

## Fonte de verdade

O repositório, seus testes e os registros do GitHub (Issues e PRs) são a fonte de
verdade. Ordem de precedência em caso de conflito:

1. requisito explícito da Issue/tarefa atual;
2. código, testes e decisões arquiteturais vigentes;
3. este contrato;
4. o adaptador do agente (`CLAUDE.md`, `AGENTS.md`).

Um adaptador **nunca** contradiz este contrato. Se contradisser, é bug do adaptador:
registre e siga o contrato. Se ainda restar ambiguidade material, registre-a no PR ou na
Issue em vez de inventar comportamento.

**A memória de conversa não é fonte de verdade.** Nem a sessão anterior, nem o contexto
interno de qualquer modelo, nem um resumo colado no chat descrevem o estado do código ou
de uma Issue. Nenhum agente redefine arquitetura, escopo ou backlog com base apenas em
conversa. Se uma lembrança de sessão divergir do Git, **o Git vence**.

Estes documentos são infraestrutura de governança: uma tarefa de feature não os altera
casualmente. Mudanças no contrato, nos adaptadores ou nos protocolos são intencionais e
vão em PR própria ou claramente justificada.

## O projeto

D.A.N.T.E. (*Distributed Agent Network for Task Execution*) é um serviço local em .NET
que recebe comandos pelo Telegram e executa as CLIs locais do Claude Code e do Codex,
em um workspace geral isolado ou em repositórios cadastrados.

| O quê | Onde |
| --- | --- |
| Uso, configuração e comandos | `README.md` |
| Código do serviço | `src/Dante.Worker` |
| Testes (xUnit) | `tests/Dante.Tests` |
| Processo auxiliar dos testes de execução | `tests/Dante.ProcessProbe` |
| Composição/DI | `src/Dante.Worker/Program.cs` |
| Target framework e pacotes | os próprios `.csproj` |

Não altere versão de pacote ou target framework sem pedido explícito.

## Convenções de código

* identificadores de código (tipos, membros, arquivos) em **inglês**, como o código
  existente;
* textos voltados ao usuário final (respostas do Telegram, mensagens de erro exibidas)
  em **português**, como o código existente;
* documentação do repositório em português;
* siga o estilo do arquivo que está editando: densidade de comentários, nomenclatura,
  primary constructors, records, `sealed`;
* nenhum comando aceita shell arbitrário: agentes são executados com `ArgumentList`,
  sem interpolação em linha de comando;
* segredos nunca em arquivo versionado, log ou resposta do Telegram.

## Build e testes

```bash
dotnet build Dante.sln
dotnet test Dante.sln
```

Em WSL sem SDK Linux, use o SDK do Windows por interop antes de concluir que não é
possível validar:

```bash
command -v dotnet || ls "/mnt/c/Program Files/dotnet/dotnet.exe"
"/mnt/c/Program Files/dotnet/dotnet.exe" test Dante.sln
```

Os testes não dependem das CLIs reais nem do Telegram: usam fakes e o
`Dante.ProcessProbe`. Toda alteração preserva os testes existentes, e regra nova
relevante ganha cobertura. Nunca ajuste um teste apenas para fazer passar uma
implementação incorreta.

Não há CI no repositório hoje: a validação local (build + testes) é obrigatória antes do
PR, e o resultado real vai no corpo do PR.

Em `/mnt/c`, o `git status` pode acusar arquivos modificados só por cache de stat ou
CRLF; confirme com `git diff` antes de concluir qualquer coisa.

## Disciplina de escopo

* implemente **somente** o escopo pedido pela Issue;
* problema fora do escopo: registre no PR ou na Issue e siga a tarefa — exceto quando
  impedir tecnicamente a conclusão; aí trate de forma mínima e explique;
* **nada de refactor oportunista**: não renomeie arquivos não relacionados, não
  reformate áreas grandes, não corrija warnings aleatórios, não atualize pacotes;
* não invente regra de negócio: se não está na Issue, no código, nos testes ou na
  documentação, não existe;
* prefira solução simples, explícita e testável; sem camadas de abstração sem
  necessidade real;
* PRs pequenos, revisáveis e focados.

Isso vale com força redobrada sobre trabalho recebido de outro worker: trabalho
existente é presumido válido e não é descartado nem refatorado por gosto.

## Workflow Git

```text
 1. Issue antes da implementação, quando o trabalho vem do backlog
 2. criar a branch da tarefa antes da primeira alteração
 3. confirmar a branch e verificar git status
 4. registrar o baseline (build + testes)
 5. implementar somente o escopo
 6. adicionar/ajustar testes
 7. validar (build + testes)
 8. revisar o diff completo
 9. commit com staging seletivo
10. push
11. abrir Pull Request para main
12. deixar a working tree limpa
```

### Branch

`main` nunca recebe trabalho direto: toda mudança passa por branch + PR.

```bash
git switch -c feat/issue-<numero>-<slug>
git branch --show-current
```

Prefixos: `feat/`, `fix/`, `refactor/`, `chore/`, `test/`, `docs/`. Padrão de nome
usado no projeto: `<prefixo>/issue-<numero>-<slug>`.

```text
1 Issue → 1 branch → 1 PR
```

Nunca combine Issues independentes numa branch ou num PR. A branch é da Issue, não do
agente: se a Issue trocar de worker, o próximo continua na **mesma branch e no mesmo
PR**.

### Alterações pré-existentes

Depois de criar ou retomar a branch, rode `git status`. Havendo alterações que você não
fez: não descarte, não sobrescreva, não faça reset. Entenda a origem antes de
prosseguir — elas podem ser trabalho legítimo de outro worker.

### Commit

Staging **seletivo**, por caminho explícito, conferindo antes de commitar:

```bash
git add <caminho> <caminho> ...
git diff --staged --stat
git commit -m "<tipo>(<escopo opcional>): <resumo>"
```

`git add .`, `git add -A` e `git commit -a` não são receita: varrem a árvore e arrastam
para o commit o que não pertence à tarefa.

Mensagem curta e semântica (`feat:`, `fix:`, `docs:`, `test:`, `refactor:`, `chore:`).
Commit ao concluir cada unidade de trabalho relevante do turno; commits intermediários
de checkpoint são permitidos.

### Pull Request

PR para `main` com, no corpo:

```text
Closes #<numero>

## Implementação
o que mudou e por quê

## Validação
comandos executados e resultado real (build, testes)
```

Quando uma Issue depende de outra ainda não mesclada, o PR pode ser **empilhado** sobre
a branch da dependência; o corpo diz sobre qual PR ele está empilhado.

**Squash merge** é o padrão de integração. O merge é **sempre decisão humana**: nenhum
agente faz merge do próprio PR sem instrução explícita.

## Relatório final

Toda tarefa concluída termina com um relatório curto, proporcional ao diff:

```text
Branch:
Commit:
PR:
Baseline:          build/testes antes
Validação:         build/testes depois
Alterações principais:
Testes adicionados/alterados:
Pendências fora do escopo:
Working tree:
```

Quando a Issue passou por mais de um worker, o relatório cobre a Issue inteira, não só
o último turno.
