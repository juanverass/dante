using Dante.Application.AuditoriaDoBrain;
using static Dante.Application.ConversaDoBrain.ApresentacaoDaConversa;

namespace Dante.Application.ConversaDoBrain;

public sealed class InspecaoDaConversa(InspecaoDoBrainAppService auditoria)
{
    internal async Task<string> InspecionarAsync(ContextoDaConversa conversa, CancellationToken cancellationToken)
    {
        var audit=await auditoria.InspecionarAsync(conversa.Acesso,new(){Limite=10},cancellationToken);
        return $"Brain: {Resumir(audit.Espaco.Nome,100)}. {audit.Conhecimentos.Count} conhecimentos nesta página, {audit.Relacoes.Count} relações e {audit.Fontes.Count} fontes brutas."+
            (audit.TemMais?" Há mais itens; refine a consulta.":"")+"\nConsulte por assunto para ver conteúdo e origem.";
    }
    internal async Task<string> ExportarAsync(ContextoDaConversa conversa, CancellationToken cancellationToken)
    {
        var export=await auditoria.ExportarAsync(conversa.Acesso,cancellationToken);
        if(export.Markdown.Length>12000)return "A exportação é maior que o limite desta conversa. Use a exportação local Markdown/JSON para salvar os arquivos.";
        return export.Markdown;
    }
}
