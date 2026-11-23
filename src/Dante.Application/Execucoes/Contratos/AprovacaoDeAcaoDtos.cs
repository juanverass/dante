using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Pedido de ação protegida (AD-72, #311). A identidade do solicitante nunca é argumento: com Dono, é a pessoa que deu o
// comando (canal e id dela, conferidos contra o dono da raiz); sem Dono, é o agente da etapa, pela versão congelada
// gravada na etapa ou, sem ela, na execução (o canal da sessão informa a etapa). Hash e payload são calculados pelo
// servidor.
public sealed record SolicitacaoDeAcaoDto(
    string Acao,
    Guid IdExecucao,
    Guid? IdEtapa,
    string? Alvo,
    IReadOnlyCollection<string> Capacidades,
    string ParametrosJson,
    ProprietarioDaExecucao? Dono,
    TimeSpan? Validade = null);

// Parâmetro como a pessoa o vê: redigido (credencial vira marcador) e cortado. O hash cobre o valor inteiro.
public sealed record ParametroExibidoDto(string Nome, string Valor);

// Aprovação como a interface a mostra: resumo redigido, hash e prazos; nunca o payload bruto.
public sealed record AprovacaoDeAcaoDto(
    Guid Id,
    string Acao,
    string Descricao,
    ClasseDeRisco Risco,
    string? Alvo,
    string? CodigoDaExecucao,
    string Solicitante,
    string CanalDoDono,
    long IdDoDono,
    string Hash,
    EstadoDaAprovacaoDeAcao Estado,
    DateTimeOffset CriadaEm,
    DateTimeOffset ExpiraEm,
    DateTimeOffset? DecididaEm,
    string? Motivo,
    string? Resultado,
    IReadOnlyList<ParametroExibidoDto> Parametros)
{
    public const int TamanhoDoHashCurto = 12;

    public string HashCurto => Hash[..TamanhoDoHashCurto];
}

public sealed record PedidoDeAcaoDto(AprovacaoDeAcaoDto Aprovacao, bool Novo);

public enum DesfechoDaDecisaoDeAcao
{
    Aprovada,
    Recusada,
    // O prazo venceu antes da decisão: o pedido expira e nada é autorizado.
    Expirada,
    // Revogada antes da decisão (agente desativado ou com outra versão): nada é autorizado.
    Cancelada,
    // Já decidida, expirada ou cancelada antes: a decisão não muda.
    JaDecidida,
    // Inexistente ou de outro dono (indistinguíveis).
    NaoEncontrada
}

public sealed record DecisaoDeAcaoDto(DesfechoDaDecisaoDeAcao Desfecho, AprovacaoDeAcaoDto? Aprovacao);

// O que a ação de fato fez, dito por quem a executa (o consumidor, PR 3). SemEfeito: comprovadamente nada mudou.
// Incerta: pode ter mudado; o estado observado é relatado e a ação nunca é repetida automaticamente.
public abstract record DesfechoDaAcaoProtegida
{
    private DesfechoDaAcaoProtegida()
    {
    }

    public sealed record Executada(string? Resultado) : DesfechoDaAcaoProtegida;

    public sealed record SemEfeito(string Motivo) : DesfechoDaAcaoProtegida;

    public sealed record Incerta(string Motivo, string? EstadoObservado) : DesfechoDaAcaoProtegida;
}

// Execução de uma ação aprovada, fornecida pelo consumidor da ação: remontar o payload com os fatos atuais (versão,
// capacidades, conteúdo) e executar exatamente os parâmetros gravados. O gateway decide se e quando executa.
public sealed record ExecucaoDaAcaoProtegida(
    DefinicaoDeAcao Acao,
    Func<AprovacaoDeAcao, CancellationToken, Task<PayloadDaAcao>> RemontarPayload,
    Func<AprovacaoDeAcao, CancellationToken, Task<DesfechoDaAcaoProtegida>> Executar);

public enum ResultadoDaExecucaoDeAcao
{
    Executada,
    Falhou,
    Incerta,
    // Não está aprovada (pendente, recusada, já executada, incerta…): nada roda.
    NaoAutorizada,
    Expirada,
    PayloadAlterado,
    // Agente desativado ou com outra versão, ou payload atual impossível de remontar: cancelada sem executar.
    Revogada,
    // Outra tentativa gravou o início primeiro (CAS): esta não executa.
    JaIniciada,
    NaoEncontrada
}

public sealed record ExecucaoDeAcaoDto(ResultadoDaExecucaoDeAcao Resultado, AprovacaoDeAcaoDto? Aprovacao);

// O que a manutenção encerrou: pedidos vencidos (expirados) e execuções interrompidas por reinício (incertas).
public sealed record ManutencaoDeAprovacoesDto(IReadOnlyList<AprovacaoDeAcaoDto> Expiradas,
    IReadOnlyList<AprovacaoDeAcaoDto> Incertas);
