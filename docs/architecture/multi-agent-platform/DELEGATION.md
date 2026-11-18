# Delegação e comunicação entre agentes

## Contrato da delegação

```csharp
public sealed class Delegacao : EntidadeBase
{
    public TipoDeDelegacao Tipo { get; private set; }          // Consulta (só leitura, resposta curta) | Tarefa (pode escrever, se permitido)
    public Guid IdExecucaoDeOrigem { get; private set; }
    public Guid IdEtapaDeOrigem { get; private set; }
    public Guid IdExecucaoSubordinada { get; private set; }    // criada junto, tipo Delegada
    public Guid IdExecucaoRaiz { get; private set; }
    public Guid IdVersaoSolicitante { get; private set; }
    public Guid IdVersaoDestinataria { get; private set; }
    public int Profundidade { get; private set; }              // origem.Profundidade + 1
    public string Objetivo { get; private set; } = "";
    public EntradaDaDelegacao Entrada { get; private set; } = null!;   // texto + referências a artefatos/etapas do ledger
    public string CriteriosDeConclusao { get; private set; } = "";
    public LimitesDaDelegacao Limites { get; private set; } = null!;   // prazo, turnos, tokens, escrita permitida
    public EstadoDaDelegacao Estado { get; private set; }      // Pendente, EmAndamento, Respondida, Falhou, Cancelada, Expirada
    public ResultadoDaDelegacao? Resultado { get; private set; }       // texto redigido, artefatos, veredito, autor (versão)
    public string ChaveDeDeduplicacao { get; private set; } = "";      // hash(raiz, destinatário, objetivo normalizado)
}
```

> **Implementação (#309, AD-70 — PR 1, domínio e persistência).** O contrato acima foi implementado com estas
> diferenças: estados `Solicitada → Aceita → EmAndamento → Respondida | Falhou | Expirada | Cancelada | Recusada`; a
> delegação guarda agente, versão e hash do solicitante e do destinatário, a cadeia de agentes e o contrato do catálogo
> fechado (`consulta.v1`, `revisao.v1`) em vez de `Tipo`; o objetivo é guardado redigido, com hash do normalizado; só
> leitura (escrita recusada no domínio e no banco até #307 + #311); teto global de profundidade 1, 4 delegações por
> execução, 30 min para iniciar, 60 min de duração e paralelismo 1; a chave de deduplicação é por unidade de trabalho
> (raiz, etapa de origem, contrato, objetivo normalizado e entrada canônica), **sem o destinatário**, e vale em
> qualquer estado: a repetição devolve a original (mesmo terminal, sem reabri-la) e outro agente é recusado. Executor, fila, retorno, prazo, cascata e ferramentas: PRs 2 e 3 da #309.

A delegação **é uma linha**, não uma mensagem. A execução subordinada é uma `Execucao` comum do tipo `Delegada`:
tem etapas, eventos, aprovações e aparece no acompanhamento.

## Fluxo

```mermaid
sequenceDiagram
  participant C as Sessão do solicitante (CLI)
  participant M as dante_coordenacao (MCP stdio)
  participant W as Worker (IPC da sessão, AD-56)
  participant A as Application: DelegarTarefa
  participant DB as PostgreSQL
  participant F as Fila
  participant D as Sessão do destinatário
  C->>M: delegar_tarefa(destinatario, objetivo, entrada, criterios)
  M->>W: named pipe exclusivo da sessão
  W->>A: identidade = sessão → etapa → execução → versão do agente (nunca do prompt)
  A->>A: validações (abaixo)
  A->>DB: 1 transação: delegacao + execucao Delegada + etapas + trabalho + evento
  A-->>C: "Delegado como E124 a revisor; o resultado chegará em novo turno"
  Note over C: o turno do solicitante termina normalmente; nada fica bloqueado
  F->>D: executa E124 (sessão nova do destinatário, perfil ≤ limites)
  D->>M: entregar_resultado(schema)
  A->>DB: resultado + delegacao Respondida + trabalho AvancarExecucao(origem)
  F->>C: novo turno na sessão do solicitante (ou sessão nova com prólogo do ledger, se a antiga acabou)
  Note over C: o resultado entra como DADO NÃO CONFIÁVEL com autor e E124
```

## Validações determinísticas (no caso de uso, antes de qualquer processo)

| Risco | Regra |
| --- | --- |
| Autoconsulta | `destinatário ≠ solicitante` (versão e agente) |
| Ciclos | destinatário não pode estar na cadeia de agentes ativos da raiz até a origem |
| Recursão ilimitada | `Profundidade ≤ min(política do grupo, limite do agente, global = 3)` |
| Duplicação | `ChaveDeDeduplicacao` única entre delegações não terminais da mesma raiz; também recusa repassar o que o humano já pediu diretamente ao destinatário na mesma conversa |
| Explosão de agentes | `DelegacoesMaximas` por raiz e `ParalelismoMaximo` de filhas simultâneas; destinatário precisa ser participante do grupo (execução de grupo) ou estar em `PodeConsultar` da versão do agente solicitante (execução individual) |
| Consumo ilimitado | Orçamento da raiz: turnos, duração e tokens (somatório de `AgentTokenUsage` quando a CLI informa); a filha recebe uma fatia; esgotado → `Falhou` com motivo, e a origem é informada |
| Escalada de permissão | Perfil da filha = mínimo entre o pedido, a versão destinatária e a política. Delegação `Tarefa` com escrita exige worktree (etapa 5) **e** aprovação do D.A.N.T.E. pelo gateway (etapa 9); antes disso, delegação é só de leitura. Escrita em grupo antes da etapa 9 acontece só por etapas de roteiro ou de plano aprovado, despachadas pelo orquestrador, nunca por delegação livre |
| Sem rastreabilidade | Todo resultado tem autor (versão), execução, etapa e evidências; a consolidação cita `E…` e o agente |
| Prompt injection | Objetivo, entrada e resultado são entregues entre delimitadores de dado não confiável, com autor; instruções do destinatário vêm só da versão dele |
| Segredos | A filha recebe ambiente só do repositório do contexto (AD-10); resultado de execução com segredos vinculados passa pela redaction e, se omitido para o humano (AD-21), também é omitido para o solicitante |
| Pergunta ao humano em cadeia | Filha pode pedir input ao humano (ele é a autoridade da raiz); a pergunta mostra a cadeia ("revisor, a pedido de coordenador, em E123") |

Prazo: a delegação expira se a filha não começar em `Limites.Prazo` (padrão 30 min); enquanto a filha está
`EmExecucao`, o prazo é prorrogado (lição da ADR 0095 do concorrente). Expirada → origem recebe o fato e decide.

## Ferramentas do `dante_coordenacao`

Servidor MCP stdio no mesmo padrão do `dante_brain` (AD-56): executável do Worker em modo próprio, named pipe
exclusivo da sessão, sem credenciais na linha de comando. Concedido só a sessões que executam etapas de uma
execução (nunca à conversa comum) e só com as ferramentas que o papel permite.

| Ferramenta | Quem recebe | Efeito |
| --- | --- | --- |
| `consultar_agente` | participantes com `PodeConsultar` | Delegação `Consulta`, só leitura |
| `delegar_tarefa` | coordenador | Delegação `Tarefa` |
| `propor_plano` | coordenador | Plano estruturado para validação/aprovação |
| `estado_da_execucao` | todos | Lê etapas, delegações e resultados da própria raiz (fatos, sem conteúdo de outras raízes) |
| `entregar_resultado` | todos | Resultado estruturado da etapa (schema por tipo: texto, veredito de revisão, lista de artefatos) |
| `pedir_aprovacao` | conforme política | Pedido de aprovação de negócio, via gateway |

Somente leitura (`estado_da_execucao`) roda sem aprovação; as demais seguem o gateway e a política
([SECURITY_AND_APPROVALS](SECURITY_AND_APPROVALS.md#gateway-de-ferramentas-do-dante)). O servidor nasce com o
gateway mínimo (identidade, catálogo, concessão, schema, registro) na primeira etapa do roadmap que o expõe;
`pedir_aprovacao` e a aprovação com hash só entram com a etapa 9.

## Comunicação: alternativas

| Abordagem | Avaliação |
| --- | --- |
| Mensagens diretas entre agentes | Rejeitada no início: difícil limitar, ciclos, contexto opaco; foi a fonte da maioria das ADRs corretivas do concorrente |
| **Mediada pelo orquestrador/coordenador** | **Recomendada**: toda troca é uma delegação com estado, limites e autor |
| **Artefatos compartilhados** | **Recomendada junto**: resultados grandes (diff, relatório) ficam como artefatos da etapa (arquivo no diretório da execução ou referência a commit/PR) e a delegação carrega a referência, não o conteúdo inteiro |
| Eventos | Para observabilidade e chat, não para agentes conversarem |
| **Contratos de entrada e saída** | **Recomendada**: `entregar_resultado` com schema por tipo de etapa; o roteiro e o coordenador consomem campos, não prosa |

Evolução: consultas diretas entre participantes (`consultar_agente`) só depois de o coordenador e os limites
estarem medidos; mensagens livres, nunca sem nova AD.

## Consolidação

- o coordenador (ou o roteiro) produz a resposta final com `entregar_resultado`;
- o orquestrador anexa a trilha: participantes, etapas, delegações, resultado de cada uma, aprovações;
- divergências não são resolvidas em silêncio: a resposta tem seção de divergências com autor e evidência;
- o histórico completo fica no ledger; o Telegram mostra o resumo e `/execucao E123` mostra a trilha.
