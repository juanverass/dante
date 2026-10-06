using Dante.Application.ContextosDeTrabalho;
using Dante.Application.SegurancaDoBrain;

namespace Dante.Application.ConversaDoBrain;

internal static class ApresentacaoDaConversa
{
    internal const string FormatoDoContexto="Diga: atualize o contexto de trabalho: objetivo: ...; tarefa: ...; progresso: ...; resultado: ...; pendência: ...; próximo passo: ...; referência: ... Campos omitidos são mantidos; listas informadas substituem as anteriores.";
    internal static string DescreverContexto(ContextoDeTrabalhoDto contexto)
    {
        var d=contexto.Dados;string Lista(IReadOnlyList<string> itens)=>itens.Count==0?"nenhum":string.Join("; ",itens.Select(x=>Resumir(x,500)));
        return $"Contexto de trabalho (revisão {contexto.Revisao}, {contexto.Sensibilidade}):\nObjetivo: {Resumir(d.Objetivo,2000)}\nTarefa: {Resumir(d.Tarefa,2000)}\n"+
            $"Progresso: {Resumir(d.Progresso,2000)}\nÚltimo resultado: {Resumir(d.UltimoResultado,2000)}\nPendências: {Lista(d.Pendencias)}\n"+
            $"Próximos passos: {Lista(d.ProximosPassos)}\nReferências: {Lista(d.Referencias)}\nDecisões confirmadas referenciadas: {d.IdsDecisoesConfirmadas.Count}.\n"+
            "É estado operacional, não fato confirmado; entra no contexto das próximas sessões deste escopo, e /clear e /compact não o alteram.";
    }
    internal static string Resumir(string texto,int limite)
    {
        texto=ProtecaoDeSegredos.Redigir(texto)!;if(texto.Length<=limite)return texto;
        if(char.IsHighSurrogate(texto[limite-1]))limite--;return texto[..limite]+"…";
    }
}
