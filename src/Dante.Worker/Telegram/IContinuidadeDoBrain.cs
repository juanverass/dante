using Dante.Application.ConstrucaoDeContexto;
namespace Dante.Worker.Telegram;

// Continuidade do Brain na conversa natural (#145): o pacote do Context Builder acompanha o texto do turno como dado
// citado. Bootstrap no primeiro envio de cada conversa upstream; depois, refresh só com itens novos ou revisados.
// Só o que a sessão aceitou conta como injetado.
public interface IContinuidadeDoBrain
{
    bool Configurado { get; }
    Task<ContextoParaTurno?> PrepararAsync(TelegramMessage mensagem,string idSessao,string texto,CancellationToken cancellationToken=default);
    void RegistrarInjecao(ContextoParaTurno contexto);
    // A conversa upstream foi limpa ou compactada: o próximo turno volta ao bootstrap. O Brain não muda.
    void ReiniciarSessao(string idSessao);
    string? EscopoSelecionado(TelegramMessage mensagem);
    string? DescreverStatus(TelegramMessage mensagem,string? idSessao);
}
public sealed record ContextoParaTurno(string IdSessao,string Escopo,string Texto,PacoteDeContextoDto Pacote,bool Bootstrap);
