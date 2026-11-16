# Grupos de agentes e coordenação

Grupo aqui é **unidade de execução**, não agrupamento visual. O concorrente só tem grupo visual
([ORQUESTRACAO](../../research/agentes-chat/ORQUESTRACAO.md#grupos)); a parte que aproveitamos dele é comunicação e
limites, não o modelo de grupo.

## Estrutura

```csharp
public sealed class GrupoDeAgentes : EntidadeBase
{
    public string Nome { get; private set; } = "";         // único; endereçado por /grupo <nome>
    public string Descricao { get; private set; } = "";
    public EstadoDoGrupo Estado { get; private set; }       // Ativo, Desativado, Arquivado
    public Guid IdVersaoAtual { get; private set; }
}

public sealed class VersaoDoGrupo : EntidadeBase        // imutável
{
    public Guid IdGrupo { get; private set; }
    public int Numero { get; private set; }
    public ModoDeCoordenacao Modo { get; private set; }      // Roteiro | Coordenador
    public Guid? IdAgenteCoordenador { get; private set; }   // obrigatório no modo Coordenador
    public IReadOnlyList<ParticipanteDoGrupo> Participantes { get; private set; } = [];
    public PoliticaDoGrupo Politica { get; private set; } = null!;
    public DefinicaoDeRoteiro? Roteiro { get; private set; } // obrigatório no modo Roteiro
    public string HashDaConfiguracao { get; private set; } = "";
}

public sealed record ParticipanteDoGrupo(
    Guid IdAgente,              // o agente, não a versão: a versão é congelada na criação da execução (abaixo)
    string Papel,               // "implementador", "revisor", "pesquisador"
    string Responsabilidades,   // texto entregue ao coordenador e ao próprio agente
    bool PodeEscrever);         // ≤ PodeEscrever da versão do agente, reconferido ao criar a execução

public sealed record PoliticaDoGrupo(
    int ParalelismoMaximo,          // etapas simultâneas da mesma execução (padrão 2)
    int DelegacoesMaximas,          // por execução raiz (padrão 8)
    int ProfundidadeMaxima,         // padrão 2 (raiz → filha → neta)
    int RodadasDeRevisaoMaximas,    // padrão 2
    bool PlanoExigeAprovacao,       // padrão true no modo Coordenador
    IReadOnlyList<string> RepositoriosPermitidos, // aliases; vazio = os do pedido
    TimeSpan DuracaoMaxima);        // da execução raiz
```

### Versões e reprodutibilidade

- toda mudança de coordenador, participante, papel, política ou roteiro cria `VersaoDoGrupo` imutável;
- execução guarda `IdVersaoDoGrupo`; mudar o grupo **não** altera execuções iniciadas (AD-11/20);
- participantes referenciam o **agente**, não a versão: ajustar um agente não exige nova versão de cada grupo. A
  versão de cada participante é **resolvida e congelada na criação da execução** (gravada na execução e em cada
  etapa), e a reprodução usa as versões gravadas na execução.

### Invariantes (validação no domínio)

- coordenador é participante do grupo e tem capacidade `planejar`;
- nenhum participante com `PodeEscrever` acima do que a versão atual do agente permite (validado ao salvar o grupo e
  de novo ao criar a execução);
- papéis únicos dentro do grupo quando o roteiro os referencia;
- roteiro referencia só papéis existentes, forma um DAG e tem limite de rodadas;
- agente desativado não entra em versão nova; grupo com participante desativado continua listável, mas
  execuções novas recusam com o motivo (sem substituição silenciosa);
- `ParalelismoMaximo` ≤ limite global; profundidade ≤ limite global (padrão 3).

## Coordenação: duas formas, uma de cada vez

### 1. Roteiro determinístico (primeiro)

O grupo define um roteiro fixo — papéis, etapas, dependências, condição de laço e limites. O **orquestrador (código)**
o executa; nenhum modelo decide a ordem. É o pipeline implementar → revisar → corrigir → aprovação humana
([PERSISTENCE_AND_EXECUTIONS](PERSISTENCE_AND_EXECUTIONS.md#pipeline-de-desenvolvimento-como-etapas-persistentes)).

Vantagens: previsível, testável por tabela, sem custo de planejamento, cobre o caso de uso principal (o Agent
Harness já é esse roteiro, feito à mão). Limite: não decompõe tarefas abertas.

### 2. Coordenador por papel (depois)

Um agente registrado recebe o papel de coordenador numa versão de grupo. Ele:

1. recebe a tarefa e o contexto (participantes, papéis, responsabilidades, limites, ferramentas);
2. propõe um plano com `propor_plano` (estruturado: etapas, agente/papel, objetivo, entradas, critérios de
   conclusão, dependências, se escreve);
3. o **orquestrador valida** o plano (participantes do grupo, papéis, `PodeEscrever`, DAG, limites, um escritor por
   worktree) e, se `PlanoExigeAprovacao`, a execução vai a `AguardandoAprovacao` com o plano visível;
4. aprovado, as etapas são materializadas; o orquestrador as despacha respeitando dependências e paralelismo;
5. resultados voltam ao coordenador **em novo turno** da sessão dele (não bloqueia o turno esperando);
6. o coordenador pode delegar mais (dentro dos limites) ou consolidar com `entregar_resultado`;
7. a resposta final cita participantes e divergências; o orquestrador anexa a trilha (quem fez qual etapa).

### Comparação: coordenador dedicado × papel

| Critério | Agente coordenador permanente (especial) | Papel em qualquer agente registrado (**recomendado**) |
| --- | --- | --- |
| Configuração | um agente mágico com regras próprias | o mesmo cadastro de agentes; papel e ferramentas vêm da versão do grupo |
| Permissões | tende a acumular privilégios | ferramentas de coordenação concedidas só durante a execução coordenada |
| Visibilidade | coordenação implícita ("o que sobra vai para ele") — o concorrente substituiu isso em 3 dias (ADR 0093 → 0137) | explícita: recibo "coordenado por X (grupo Y v3)" |
| Teste | difícil isolar | o orquestrador é código; o coordenador é um driver falso em teste |
| Custo | sempre presente | só quando o grupo é do modo Coordenador |

**Brain não é coordenador.** Ver [ARCHITECTURE](ARCHITECTURE.md#fronteira-com-o-brain).

## Endereçamento

| Pedido | Efeito |
| --- | --- |
| `/grupo <nome> [@alias] <tarefa>` | Execução `Roteiro` ou `Coordenada` com a versão atual do grupo |
| `/tarefa <handle> [@alias] <tarefa>` | Execução `Tarefa` com um agente (primeira versão funciona só com isto) |
| `/session start <handle> [@alias]` | Sessão de conversa com um agente registrado (`claude`/`codex` continuam funcionando) |
| Mensagem comum | Inalterada: turno da sessão ativa (AD-23) |

Regras: nenhuma inferência de agente ou grupo pelo texto (AD-08/27); grupo/agente desconhecido ou desativado recusa
sem cair para outro; `@alias` continua sendo só repositório. Sintaxe final é decisão de produto (ver README).

## Matriz de falhas e intervenção humana (grupo)

| Situação | Comportamento |
| --- | --- |
| Participante falha numa etapa | Etapa `Falhou`; roteiro aplica nova tentativa se `Retentavel`, senão a execução vai a `Bloqueada` com opções: tentar de novo, pular (se a etapa for opcional), cancelar |
| Revisor e implementador não convergem | Após `RodadasDeRevisaoMaximas`, `Bloqueada` com o último veredito e o diff; humano decide |
| Coordenador não delega / delega demais | Limites de delegação e profundidade; plano recusado pela validação volta ao coordenador uma vez, depois `Falhou` |
| Limite de plano do provider | Etapa espera (`disponivel_em`) e o usuário é avisado; outras etapas independentes seguem |
| Pausa | `/execucao pausar E…`: nenhuma etapa nova é despachada; as em andamento terminam (ou são interrompidas com `--agora`) |
| Cancelamento | Cancela filhas e etapas em andamento (interrupt/close da sessão, AD-16/17), libera reservas sem apagar worktree, registra motivo; o motivo entra no prólogo se a execução for refeita |
| Resultados conflitantes | Consolidação nunca escolhe em silêncio: lista as posições com autor e evidência; se a política exigir, vira aprovação humana |
