using Dante.Application.BuscaDoBrain;
using Dante.Application.Conhecimentos;
using Dante.Application.DocumentosFonte;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using static Dante.Application.ConversaDoBrain.ApresentacaoDaConversa;

namespace Dante.Application.ConversaDoBrain;

public sealed class ConsultaDaConversa(BuscaDoBrainAppService busca, IConhecimentoAppService conhecimentos, IDocumentoFonteRepository fontes)
{
    internal async Task<ConhecimentoDto> CarregarAsync(ContextoDaConversa conversa, AlvoDeConversaDto alvo, CancellationToken cancellationToken)
    {
        var item=await conhecimentos.ObterPorIdAsync(alvo.Id,cancellationToken)??throw new InvalidOperationException("Item não encontrado; refaça a consulta.");
        if(!conversa.Permitida(item.Sensibilidade))throw new UnauthorizedAccessException("Informação protegida.");
        if(item.IdEspacoDeConhecimento!=conversa.Acesso.IdEspacoDeConhecimento||item.IdProjeto!=conversa.Acesso.IdProjeto||item.Revisao!=alvo.Revisao||item.Status is StatusDoConhecimento.Inativo or StatusDoConhecimento.Substituido)
            throw new InvalidOperationException("Item mudou; refaça a consulta.");
        return item;
    }
    internal async Task<string> ConsultarAsync(ContextoDaConversa conversa, string termo,bool experiencia, CancellationToken cancellationToken)
    {
        if(string.IsNullOrWhiteSpace(termo)||termo.Length>2000)throw new ArgumentException("Consulta inválida.");
        var resultado=await busca.BuscarAsync(conversa.Acesso,new(){Texto=termo,Limite=10,Tipos=experiencia?[TipoDeConhecimento.Incidente,TipoDeConhecimento.Solucao,TipoDeConhecimento.Aprendizado,TipoDeConhecimento.Procedimento]:[]},cancellationToken);
        var relevantes=experiencia?resultado.Resultados.Where(x=>x.Origem==OrigemDoResultado.FonteBruta||x.Item.Tipo is TipoDeConhecimento.Incidente or TipoDeConhecimento.Solucao or TipoDeConhecimento.Aprendizado or TipoDeConhecimento.Procedimento).ToArray():resultado.Resultados.ToArray();
        var alvos=relevantes.Select(x=>new AlvoDeConversaDto(x.Item.Id,x.Item.Revisao,x.Origem==OrigemDoResultado.Conhecimento?"conhecimento":"fonte_bruta",x.Origem==OrigemDoResultado.Conhecimento?x.Item.Tipo:null,
            x.Origem==OrigemDoResultado.Conhecimento?x.Item.Status:null,x.Item.Sensibilidade,Resumir(x.Item.Conteudo??x.Item.DadosEstruturados??"conteúdo protegido",500),x.Fonte?.Numero)).ToArray();
        conversa.Guardar(conversa.Estado with{Resultados=alvos,Pendente=null,UltimoTermo=termo});
        return alvos.Length==0?"Não encontrei informação autorizada neste escopo. Não consultei outros espaços.":
            string.Join('\n',alvos.Select((x,i)=>$"{i+1}. {x.Descricao}\n   {(x.Origem=="fonte_bruta"?"Fonte bruta, não consolidada":$"{x.Tipo}, {x.Status}")}; {x.Sensibilidade}. Origem: {Resumir(relevantes[i].Item.Origem??"não informada",200)}."))+"\nPeça a origem ou escolha um item para corrigir/invalidar.";
    }
    internal async Task<string> OrigemAsync(ContextoDaConversa conversa, AlvoDeConversaDto? alvo, CancellationToken cancellationToken)
    {
        if(alvo is null)return "Escolha um resultado inequívoco na última consulta.";
        if(alvo.Origem=="fonte_bruta")
        {
            var fonte=await fontes.ObterPorIdAsync(alvo.Id,cancellationToken);
            if(fonte is null || fonte.Removido || fonte.Revisao!=alvo.Revisao || fonte.IdEspacoDeConhecimento!=conversa.Acesso.IdEspacoDeConhecimento || fonte.IdProjeto!=conversa.Acesso.IdProjeto || !conversa.Permitida(fonte.Sensibilidade))
                return "A fonte mudou, foi removida ou está protegida; refaça a consulta.";
            return $"Fonte bruta: {Resumir(fonte.Origem,500)}. Formato: {fonte.Formato}; revisão {fonte.Revisao}; hash {fonte.Hash[..12]}. Importar não confirmou o conteúdo.";
        }
        var item=await CarregarAsync(conversa, alvo, cancellationToken);
        var provas=item.Historico.Where(x=>conversa.Permitida(x.Sensibilidade)).Select(x=>x.Proveniencia).Where(x=>x.ReferenciaDaFonte is not null).Distinct().Take(5).ToArray();
        var linhas=new List<string>();
        foreach(var p in provas)
        {
            var referencia=p.ReferenciaDaFonte??"não informada";
            if(referencia.StartsWith("conversa:",StringComparison.Ordinal))referencia="mensagem selecionada da conversa";
            else if(Uri.TryCreate(referencia,UriKind.Absolute,out var uri)&&uri.Scheme=="brain"&&uri.Host=="fonte"&&uri.Segments.Length>1&&Guid.TryParse(uri.Segments[1].Trim('/'),out var idFonte))
            {
                var fonte=await fontes.ObterPorIdAsync(idFonte,cancellationToken);
                referencia=fonte is not null && conversa.Permitida(fonte.Sensibilidade)?fonte.Origem+(fonte.Removido?" (original removido)":""):"fonte protegida ou indisponível";
            }
            linhas.Add($"Origem: {Resumir(p.Origem,200)}. Fonte: {Resumir(referencia,500)}. Revisão da fonte: {Resumir(p.RevisaoDaFonte??"não informada",100)}. Evidência: {Resumir(p.TrechoDaFonte??"não registrada",700)}.");
        }
        return $"{item.Tipo}; {item.Status}; {item.Sensibilidade}; revisão {item.Revisao}.\n"+(linhas.Count==0?$"Origem: {Resumir(item.Proveniencia.Origem,200)}.":string.Join('\n',linhas));
    }
}
