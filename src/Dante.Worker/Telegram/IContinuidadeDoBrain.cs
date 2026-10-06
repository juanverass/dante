using Dante.Application.ConstrucaoDeContexto;
using Dante.Application.MetricasDoBrain;
using Dante.Worker.Sessions;
namespace Dante.Worker.Telegram;

// Continuidade do Brain na conversa natural (#145): o pacote do Context Builder acompanha o texto do turno como dado
// citado. Bootstrap no primeiro envio de cada conversa upstream; depois, refresh só com itens novos ou revisados.
// Só o que a sessão aceitou conta como injetado, e é isso que as métricas locais registram (#148).
public interface IContinuidadeDoBrain
{
    bool Configurado { get; }
    // Com escopo resolvido, devolve o texto do turno mesmo sem itens: a mensagem também mede o histórico bruto (#148).
    Task<ContextoParaTurno?> PrepararAsync(TelegramMessage mensagem,string idSessao,string texto,CancellationToken cancellationToken=default);
    Task RegistrarInjecaoAsync(ContextoParaTurno contexto,CancellationToken cancellationToken=default);
    // Resposta do agente e uso informado pela CLI ao fim do turno de uma sessão com escopo Brain.
    Task RegistrarTurnoAsync(string idSessao,string agente,int tokensDaResposta,AgentTurnOutcome resultado,AgentTokenUsage? uso,
        CancellationToken cancellationToken=default);
    // A conversa upstream foi limpa ou compactada: o próximo turno volta ao bootstrap. O Brain não muda.
    void ReiniciarSessao(string idSessao);
    string? EscopoSelecionado(TelegramMessage mensagem);
    string? DescreverStatus(TelegramMessage mensagem,string? idSessao);
}
public sealed record ContextoParaTurno(string IdSessao,string Escopo,string Texto,PacoteDeContextoDto Pacote,bool Bootstrap,MetricaDoBrainDto Envio);
