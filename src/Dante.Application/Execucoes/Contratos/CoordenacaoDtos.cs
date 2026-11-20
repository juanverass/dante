using Dante.Application.Agentes;
using Dante.Application.Contextos;
using Dante.Application.Grupos;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Pedido de uma tarefa composta a um grupo no modo Coordenador (#310, AD-75): a resolução do grupo (versão ativa e
// versão atual de cada participante, já conferidas), o objetivo, o contexto congelado e a origem. Número já reservado;
// a chave deduplica o pedido da interface.
public sealed record NovaCoordenacaoDto(ResolucaoDeGrupoDto Grupo, string Objetivo, JobExecutionContext Contexto,
    long Numero, string Canal, long IdDoUsuarioNoCanal, string ChaveDeDeduplicacao);

// Um papel do snapshot como o coordenador o vê no planejamento: as capacidades e as responsabilidades da versão
// congelada (nunca as atuais do cadastro).
public sealed record PapelParaPlanejarDto(string Papel, string Handle, int NumeroDaVersao, string? Responsabilidades,
    IReadOnlyList<CapacidadeDoAgente> Capacidades);

// Resultado de uma unidade de uma revisão anterior, entregue ao coordenador que replaneja (o estado da unidade, o motivo
// e o resultado estruturado persistido; nunca texto livre da sessão).
public sealed record UnidadeAnteriorDto(string Unidade, string Papel, EstadoDaEtapa Estado, string? Motivo,
    string? Resultado);

// O que o executor precisa para a etapa de planejamento: a versão congelada do coordenador, o contexto e o objetivo
// congelados, os papéis que podem receber unidade, quantas unidades ainda cabem no orçamento e, numa revisão, o motivo,
// o plano anterior, os desfechos dele e a instrução humana.
public sealed record PlanejamentoDaCoordenacaoDto(
    Guid IdExecucao,
    string Codigo,
    string Canal,
    long IdDoUsuarioNoCanal,
    string Contexto,
    string Objetivo,
    string NomeDoGrupo,
    int NumeroDaVersaoDoGrupo,
    string PapelDoCoordenador,
    string HandleDoCoordenador,
    VersaoDoAgenteDto VersaoDoCoordenador,
    IReadOnlyList<PapelParaPlanejarDto> Papeis,
    int UnidadesDisponiveis,
    int Revisao,
    MotivoDoPlano Motivo,
    string? PlanoAnterior,
    IReadOnlyList<UnidadeAnteriorDto> UnidadesAnteriores,
    string? InstrucaoHumana,
    // Propostas do coordenador já recusadas nesta etapa (a correção única, AD-75), contadas pelo ledger.
    int PropostasRecusadas);

public enum DesfechoDaProposta
{
    // Plano gravado, gate aberto e a execução aguardando a decisão humana.
    GateAberto,
    // Recusado pela validação; o coordenador ainda pode corrigir uma vez.
    CorrecaoPedida,
    // Recusado de novo (ou sem proposta): a etapa e a execução falharam sem plano, filhos nem trabalhos.
    Encerrada,
    // Pedido de cancelamento do dono registrado: a conclusão comum gravou Cancelada.
    Cancelada
}

// Recusas: as da validação (motivo, unidade e mensagem), para o coordenador corrigir.
public sealed record RegistroDaPropostaDto(DesfechoDaProposta Desfecho, EstadoDaExecucao Estado,
    IReadOnlyList<RecusaDoPlano> Recusas, int? Revisao = null, string? HashDoPlano = null);

// O que a etapa mecânica de avanço fez: quantas unidades encerrou, qual despachou (código da subordinada), se abriu um
// replanejamento, se abriu a síntese do coordenador (PR 3) e o estado da execução.
public sealed record AvancoDaCoordenacaoDto(EstadoDaExecucao Estado, int UnidadesEncerradas, string? Despachada,
    bool Replanejamento, string? Motivo = null, bool Sintese = false);

// Uma revisão do plano para acompanhamento: o estado, o hash e a decisão do gate.
public sealed record PlanoDaCoordenacaoDto(Guid Id, int Revisao, MotivoDoPlano Motivo, EstadoDoPlano Estado, string Hash,
    int QuantidadeDeUnidades, string Conteudo, DateTimeOffset CriadoEm, DateTimeOffset? DecididoEm,
    PedidoDeAprovacaoDto? Pedido);

// O papel de uma etapa numa Coordenada (AD-75): o turno do coordenador que propõe o plano, o gate humano do plano, uma
// unidade do plano aprovado (executada por delegação), o avanço mecânico, sem sessão, que conclui e despacha unidades,
// ou a síntese do coordenador depois das unidades (PR 3).
public enum PassoDaCoordenacao
{
    Planejamento,
    Gate,
    Unidade,
    Avanco,
    Sintese
}

// Uma etapa da Coordenada, com a revisão do plano (gate e unidade), a unidade e o papel, e a sessão vinculada.
public sealed record EtapaDaCoordenacaoDto(Guid IdEtapa, int Numero, PassoDaCoordenacao Passo, EstadoDaEtapa Estado,
    int Tentativa, int? Revisao, string? Unidade, string? Papel, string? Motivo, string? Erro, string? CodigoDaSessao);

// Estado durável de uma Coordenada, para acompanhamento e para o executor.
public sealed record CoordenacaoDaExecucaoDto
{
    public Guid IdExecucao { get; init; }
    public string Codigo { get; init; } = "";
    public EstadoDaExecucao Estado { get; init; }
    public string? Motivo { get; init; }
    public string? Erro { get; init; }
    public string Canal { get; init; } = "";
    public long IdDoUsuarioNoCanal { get; init; }
    public string NomeDoGrupo { get; init; } = "";
    public int NumeroDaVersaoDoGrupo { get; init; }
    public string HashDaVersaoDoGrupo { get; init; } = "";
    public string HashDoSnapshot { get; init; } = "";
    public string PapelDoCoordenador { get; init; } = "";
    public string Objetivo { get; init; } = "";
    public IReadOnlyList<ParticipanteDaExecucaoDto> Participantes { get; init; } = [];
    public IReadOnlyList<PlanoDaCoordenacaoDto> Planos { get; init; } = [];
    public IReadOnlyList<EtapaDaCoordenacaoDto> Etapas { get; init; } = [];

    public PlanoDaCoordenacaoDto? PlanoAtual => Planos.Count == 0 ? null : Planos[^1];
    public PedidoDeAprovacaoDto? PedidoPendente => PlanoAtual?.Pedido is { Estado: EstadoDoPedidoDeAprovacao.Pendente } p ? p : null;
}

// Situação de uma unidade na trilha mecânica (D7): Respondida tem resultado persistido; SemResposta terminou sem ele
// (delegação falhou, expirou ou foi cancelada, ou o despacho foi recusado); Ignorada não foi executada (substituída por
// uma revisão, dependente de unidade sem resposta ou encerrada com a execução antes do despacho); EmCurso e Pendente só
// numa execução ainda aberta.
public enum SituacaoDaUnidade
{
    Respondida,
    SemResposta,
    Ignorada,
    EmCurso,
    Pendente
}

// Uma unidade na trilha: de qual revisão, quem executou (papel, agente e versão congelados), a etapa no ledger, a
// delegação com o desfecho e o resultado estruturado persistido (com o hash), e a sessão que o entregou. Motivo é o
// gravado (de um limite de provider, o texto dele, literal; o Retry-After não chega ao D.A.N.T.E. hoje, D9).
public sealed record UnidadeNaTrilhaDto(
    int Revisao,
    string Unidade,
    string Papel,
    string Handle,
    int NumeroDaVersao,
    string HashDaVersao,
    int NumeroDaEtapa,
    EstadoDaEtapa EstadoDaEtapa,
    SituacaoDaUnidade Situacao,
    string? Motivo,
    string? CodigoDaSubordinada,
    EstadoDaDelegacao? EstadoDaDelegacao,
    string? Resultado,
    string? HashDoResultado,
    string? CodigoDaSessao,
    DateTimeOffset? EncerradaEm);

// Uma revisão do plano na trilha, com a decisão do gate.
public sealed record RevisaoNaTrilhaDto(int Revisao, MotivoDoPlano Motivo, EstadoDoPlano Estado, string Hash,
    EstadoDoPedidoDeAprovacao? Decisao, string? Decisor, DateTimeOffset? DecididoEm, string? CodigoDaSessaoDoPlanejamento);

// Consolidação mecânica (D7, AD-75): gerada pelo servidor a partir do ledger, separada da síntese do coordenador (PR 3).
// Cobre todas as unidades materializadas de todas as revisões, inclusive falhas, expirações, cancelamentos e ignoradas.
// É validação estrutural (cada resultado atribuído ao agente e à sessão de origem, com o hash persistido); não avalia
// o mérito das respostas e não infere consenso entre elas.
public sealed record ConsolidacaoMecanicaDto(
    Guid IdExecucao,
    string Codigo,
    EstadoDaExecucao Estado,
    string? Motivo,
    string NomeDoGrupo,
    int NumeroDaVersaoDoGrupo,
    string HashDoSnapshot,
    IReadOnlyList<RevisaoNaTrilhaDto> Revisoes,
    IReadOnlyList<UnidadeNaTrilhaDto> Unidades)
{
    public const string Alcance =
        "Trilha mecânica gerada pelo servidor a partir do ledger: valida estrutura e proveniência, não o mérito das " +
        "respostas, e não infere consenso entre elas.";

    public int Respondidas => Unidades.Count(u => u.Situacao == SituacaoDaUnidade.Respondida);
    public int SemResposta => Unidades.Count(u => u.Situacao == SituacaoDaUnidade.SemResposta);
    public int Ignoradas => Unidades.Count(u => u.Situacao == SituacaoDaUnidade.Ignorada);
    public int Abertas => Unidades.Count(u => u.Situacao is SituacaoDaUnidade.EmCurso or SituacaoDaUnidade.Pendente);
}

// O que o executor precisa para a etapa de síntese (D7, PR 3): a versão congelada do coordenador, o contexto e o objetivo
// congelados e a trilha mecânica gerada pelo servidor, contra a qual a síntese é validada.
public sealed record SinteseParaRedigirDto(
    Guid IdExecucao,
    string Codigo,
    string Canal,
    long IdDoUsuarioNoCanal,
    string Contexto,
    string Objetivo,
    string NomeDoGrupo,
    int NumeroDaVersaoDoGrupo,
    string PapelDoCoordenador,
    string HandleDoCoordenador,
    VersaoDoAgenteDto VersaoDoCoordenador,
    ConsolidacaoMecanicaDto Trilha,
    // Sínteses já recusadas nesta etapa (a correção única), contadas pelo ledger.
    int SintesesRecusadas);

public enum DesfechoDaSintese
{
    // Síntese gravada; a execução terminou pelo desfecho das unidades.
    Registrada,
    // Recusada pela validação; o coordenador ainda pode corrigir uma vez.
    CorrecaoPedida,
    // Recusada de novo (ou não entregue): a etapa de síntese falhou e a execução terminou pelo desfecho das unidades,
    // só com a trilha mecânica.
    Encerrada,
    // Pedido de cancelamento do dono registrado: a conclusão comum gravou Cancelada.
    Cancelada
}

public sealed record RegistroDaSinteseDto(DesfechoDaSintese Desfecho, EstadoDaExecucao Estado,
    IReadOnlyList<RecusaDaSintese> Recusas, string? Hash = null);

// Uma citação da síntese: a unidade da trilha e o hash completo do resultado persistido (nulo num inconclusivo de
// unidade sem resposta).
public sealed record CitacaoDaSinteseDto(int Revisao, string Unidade, string? Hash);

public sealed record ItemDaSinteseDto(string Texto, IReadOnlyList<CitacaoDaSinteseDto> Citacoes);

// Síntese validada do coordenador (D7), lida da forma canônica gravada: separada da trilha mecânica e sempre exibida com
// o alcance da validação.
public sealed record SinteseDaCoordenacaoDto(
    string Hash,
    string Resumo,
    IReadOnlyList<ItemDaSinteseDto> Conclusoes,
    IReadOnlyList<ItemDaSinteseDto> Divergencias,
    IReadOnlyList<ItemDaSinteseDto> Inconclusivos,
    string? CodigoDaSessao,
    DateTimeOffset RegistradaEm)
{
    public const string Alcance =
        "Síntese do coordenador validada só na estrutura: cobre todas as unidades e cita resultados persistidos pelo hash. " +
        "O servidor não avaliou o mérito das afirmações; divergências são mostradas, não resolvidas, e nada aqui prova " +
        "consenso entre os agentes.";
}

// Resultado de uma Coordenada para a interface: a trilha mecânica (sempre) e a síntese do coordenador, se houver; sem
// síntese, o estado e o motivo da etapa dela (nulos quando a execução nunca chegou à síntese).
public sealed record ResultadoDaCoordenacaoDto(ConsolidacaoMecanicaDto Trilha, SinteseDaCoordenacaoDto? Sintese,
    EstadoDaEtapa? EstadoDaSintese, string? MotivoDaSintese);
