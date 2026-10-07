using System.Collections.Concurrent;
using Dante.Worker.Telegram;

namespace Dante.Worker.Brain;

// Só o adapter Telegram chama ConfirmarAsync. Argumentos MCP nunca autorizam decisões de negócio.
public sealed class RegistroDeOperacoesBrain
{
    private sealed record Proposta(string Sessao, DateTimeOffset ExpiraEm, Func<TelegramMessage, CancellationToken, Task<string>> Executar);
    private readonly ConcurrentDictionary<(long Usuario, long Chat, long Topico), Proposta> propostas = [];
    private readonly ConcurrentDictionary<string, TelegramMessage> sessoes = [];
    private readonly object gate = new();
    private readonly ConcurrentDictionary<(long, long, long), int> geracoes = [];
    public int VersaoConversa(TelegramMessage mensagem) => geracoes.GetValueOrDefault(Chave(mensagem));
    public void InvalidarConversa(TelegramMessage mensagem) => geracoes.AddOrUpdate(Chave(mensagem), 1, (_, anterior) => anterior + 1);
    private static (long, long, long) Chave(TelegramMessage m) => (m.From!.Id, m.Chat.Id, m.MessageThreadId ?? 0);
    public void Observar(TelegramMessage mensagem, string? sessao = null)
    {
        // Receber uma mensagem não significa que seu turno começou.
        if (sessao is not null && sessoes.TryGetValue(sessao, out var atual) && Chave(atual) != Chave(mensagem)) Revogar(sessao);
    }
    public bool ConversaPermitida(string sessao, TelegramMessage inicial) =>
        sessoes.TryGetValue(sessao, out var atual) && Chave(atual) == Chave(inicial);
    public void RegistrarSessao(string sessao, TelegramMessage inicial) => sessoes.TryAdd(sessao, inicial);
    public void IniciarTurno(string sessao, TelegramMessage mensagem)
    {
        if (!sessoes.TryGetValue(sessao, out var atual)) return;
        if (Chave(atual) != Chave(mensagem)) { Revogar(sessao); return; }
        // Compare-and-swap não recria uma sessão removida concorrencialmente.
        sessoes.TryUpdate(sessao, mensagem, atual);
    }
    public TelegramMessage Atual(string sessao) => sessoes.TryGetValue(sessao, out var mensagem)
        ? mensagem : throw new UnauthorizedAccessException("Sessão revogada.");
    public void Propor(TelegramMessage mensagem, string sessao, Func<TelegramMessage, CancellationToken, Task<string>> executar)
    {
        lock (gate)
        {
            if (!ConversaPermitida(sessao, mensagem)) throw new UnauthorizedAccessException("Sessão revogada.");
            propostas[Chave(mensagem)] = new(sessao, DateTimeOffset.UtcNow.AddMinutes(5), executar);
        }
    }
    public void Revogar(string sessao)
    {
        lock (gate)
        {
            sessoes.TryRemove(sessao, out _);
            foreach (var item in propostas.Where(x => x.Value.Sessao == sessao))
                ((ICollection<KeyValuePair<(long, long, long), Proposta>>)propostas).Remove(item);
        }
    }
    public async Task<string?> AtenderAsync(TelegramMessage mensagem, string texto, CancellationToken ct, string? sessao = null)
    {
        Observar(mensagem, sessao);
        var normal = Dante.Application.ConversaDoBrain.ResolvedorDeIntencaoDoBrain.Normalizar(texto);
        if (normal.StartsWith("/brain ", StringComparison.Ordinal)) normal = normal[7..].Trim();
        if (normal is not ("confirmar" or "confirme" or "cancelar" or "cancele"))
        {
            // Uma nova mensagem do usuário invalida a proposta; aprovações de ferramenta não a confirmam.
            propostas.TryRemove(Chave(mensagem), out _);
            return null;
        }
        if (!propostas.TryRemove(Chave(mensagem), out var proposta)) return null;
        if (!ConversaPermitida(proposta.Sessao, mensagem)) return "A sessão MCP foi revogada. Prepare novamente a operação.";
        if (normal is "cancelar" or "cancele") return "Proposta MCP cancelada; nenhuma alteração foi aplicada.";
        if (proposta.ExpiraEm <= DateTimeOffset.UtcNow) return "A confirmação MCP expirou. Prepare novamente a operação.";
        try { return await proposta.Executar(mensagem, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return "A operação MCP não foi confirmada: escopo, revisão ou disponibilidade mudou. Refaça a consulta."; }
    }
}
