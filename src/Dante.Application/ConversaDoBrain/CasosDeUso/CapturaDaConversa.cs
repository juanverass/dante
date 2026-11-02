using Dante.Application.CapturaDeConhecimento;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using static Dante.Application.ConversaDoBrain.ApresentacaoDaConversa;

namespace Dante.Application.ConversaDoBrain;

public sealed class CapturaDaConversa(ICapturaDeConhecimentoAppService captura)
{
    internal async Task<string> CapturarAsync(ContextoDaConversa conversa, string texto,TipoDeConhecimento tipo, CancellationToken cancellationToken)
    {
        var selecionado=conversa.Pedido.TrechoSelecionado is not null&&texto==conversa.Pedido.TrechoSelecionado;
        var colon=texto.IndexOf(':');
        if(colon>0&&Enum.TryParse<TipoDeConhecimento>(ResolvedorDeIntencaoDoBrain.Normalizar(texto[..colon]),true,out var explicito))
        {tipo=explicito;texto=texto[(colon+1)..].Trim();}
        ProtecaoDeSegredos.GarantirSeguro(texto);
        if(texto.Length>10000)throw new ArgumentException("Selecione um trecho de até 10000 caracteres.");
        // AD-59 P2/I9: o trecho citado pertence a quem o escreveu, não a quem pediu o registro. Mensagem do assistente vira
        // conclusão do agente (registrada, nunca confirmada como fato); autor não identificado não é registrado (D1).
        var autoria=selecionado?conversa.Pedido.AutoriaDoTrecho:AutoriaDaFonte.Usuario;
        if(autoria==AutoriaDaFonte.Desconhecida)
            return "Só registro como fonte uma mensagem sua ou do assistente. Escreva o conteúdo com suas palavras ou responda a uma dessas mensagens.";
        var natureza=!selecionado?NaturezaDoConteudo.DitoPeloUsuario:autoria==AutoriaDaFonte.Usuario?NaturezaDoConteudo.FonteSelecionada:NaturezaDoConteudo.ConclusaoDoAgente;
        var p=conversa.Prova(texto) with{ReferenciaDaFonte=selecionado?conversa.Pedido.ReferenciaDoTrecho??conversa.Pedido.ReferenciaDaMensagem:conversa.Pedido.ReferenciaDaMensagem};
        if(selecionado)p=p with{Origem=$"{p.Origem};autoria-da-fonte:{autoria}"};
        var candidato=await captura.CapturarAsync(new(){IdEspacoDeConhecimento=conversa.Acesso.IdEspacoDeConhecimento,IdProjeto=conversa.Acesso.IdProjeto,Tipo=tipo,Conteudo=texto,
            Sensibilidade=Sensibilidade.Pessoal,Natureza=natureza,Modo=ModoDeCaptura.Explicita,
            Justificativa="Captura explicitamente solicitada na conversa; aguardando confirmação.",Proveniencia=p},cancellationToken);
        if(candidato.Estado!=EstadoDoCandidato.Pendente)return "Esse conteúdo já foi avaliado; não criei um candidato duplicado.";
        var alvo=new AlvoDeConversaDto(candidato.Id,candidato.Revisao,"candidato",candidato.Tipo,null,candidato.Sensibilidade,Resumir(candidato.Conteudo,600));
        conversa.Guardar(conversa.Estado with{Pendente=conversa.Pendente("captura",alvo)});
        var aviso=natureza==NaturezaDoConteudo.ConclusaoDoAgente?"\nO trecho é do assistente: fica registrado como conclusão do agente e, mesmo após confirmar, não vira fato confirmado.":"";
        return $"Preparei um candidato ({candidato.Tipo}, {candidato.Sensibilidade}): {alvo.Descricao}\nNão é um fato confirmado.{aviso} Diga confirmar para consolidar ou cancelar.";
    }

    internal async Task<string> ListarCandidatosAsync(ContextoDaConversa conversa, CancellationToken cancellationToken)
    {
        var pendentes=await captura.ListarPendentesAsync(conversa.Acesso.IdEspacoDeConhecimento,conversa.Acesso.IdProjeto,10,cancellationToken);
        var alvos=pendentes.Where(x=>conversa.Permitida(x.Sensibilidade)).Select(x=>new AlvoDeConversaDto(x.Id,x.Revisao,"candidato",x.Tipo,null,x.Sensibilidade,Resumir(x.Conteudo,500))).ToArray();
        conversa.Guardar(conversa.Estado with{Resultados=alvos,Pendente=null});
        return alvos.Length==0?"Nenhum candidato pendente neste escopo.":"Candidatos pendentes:\n"+string.Join('\n',alvos.Select((x,i)=>$"{i+1}. {x.Descricao} ({x.Tipo}, {x.Sensibilidade})"))+"\nDiga confirmar primeira/segunda para consolidar o candidato escolhido.";
    }
    internal async Task DescartarAsync(ContextoDaConversa conversa, CancellationToken cancellationToken)
    {
        await captura.DescartarAsync(conversa.Acesso.IdEspacoDeConhecimento, conversa.Acesso.IdProjeto,
            conversa.Estado.Pendente!.Alvo!.Id, conversa.Estado.Pendente.Alvo.Revisao,
            conversa.Prova(conversa.Pedido.Texto), cancellationToken);
    }
}
