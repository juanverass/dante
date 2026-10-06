using Dante.Application.Conhecimentos;
using Dante.Application.CapturaDeConhecimento;
using Dante.Application.RelacoesDeConhecimento;
using Dante.Application.DocumentosFonte;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.RelacoesDeConhecimento;
using static Dante.Application.ConversaDoBrain.ApresentacaoDaConversa;

namespace Dante.Application.ConversaDoBrain;

public sealed class AlteracoesDaConversa(IConhecimentoAppService conhecimentos, ICapturaDeConhecimentoAppService captura, IRelacaoDeConhecimentoAppService relacoes, DocumentoFonteAppService documentos, IDocumentoFonteRepository fontes, ConsultaDaConversa consulta)
{
    internal string PrepararAlteracao(ContextoDaConversa conversa, string acao,AlvoDeConversaDto? alvo,string? texto=null)
    {
        if(alvo is null && conversa.Estado.Resultados.Count==0)return "Primeiro consulte o Brain pelo assunto: o que você sabe sobre X? Depois escolha a informação a alterar.";
        if(alvo is null){conversa.Guardar(conversa.Estado with{Pendente=conversa.Pendente("alvo_"+acao,texto:texto)});return "Qual informação? Escolha pelo número ou diga primeira/segunda na última lista. Nenhuma alteração foi feita.";}
        if(alvo.Origem=="fonte_bruta" && acao=="invalidacao")
        {
            conversa.Guardar(conversa.Estado with{Pendente=conversa.Pendente("fonte_remocao",alvo)});
            return $"Vou remover a fonte inteira deste trecho: “{alvo.Descricao}”. Texto original e índices serão apagados; conhecimentos consolidados manterão suas provas. Diga confirmar ou cancelar.";
        }
        if(alvo.Origem!="conhecimento")return "Esse resultado é uma fonte bruta. Consulte um conhecimento consolidado para corrigir ou invalidar.";
        if(acao=="correcao"&&string.IsNullOrWhiteSpace(texto)){conversa.Guardar(conversa.Estado with{Pendente=conversa.Pendente("texto_correcao",alvo)});return "Qual é a informação correta? Envie o texto; depois pedirei confirmação.";}
        ProtecaoDeSegredos.GarantirSeguro(texto);
        conversa.Guardar(conversa.Estado with{Pendente=conversa.Pendente(acao,alvo,texto)});
        return acao=="invalidacao"?$"Vou invalidar “{alvo.Descricao}”. O histórico será preservado e o item deixará de ser recuperado. Diga confirmar ou cancelar.":
            $"Vou corrigir “{alvo.Descricao}” para “{Resumir(texto!,600)}”. A correção ficará inferida até nova confirmação. Diga confirmar ou cancelar.";
    }
    internal async Task<string> ExecutarAsync(ContextoDaConversa conversa, AlteracaoPendenteDto alteracao, CancellationToken cancellationToken)
    {
        var prova=conversa.Prova(conversa.Pedido.Texto);
        if(alteracao.Acao=="captura")
        {
            if(!conversa.Permitida(alteracao.Alvo!.Sensibilidade))throw new UnauthorizedAccessException("Candidato protegido.");
            await captura.ConfirmarAsync(conversa.Acesso.IdEspacoDeConhecimento,conversa.Acesso.IdProjeto,alteracao.Alvo!.Id,alteracao.Alvo.Revisao,prova,cancellationToken);
            return alteracao.Alvo.Tipo==TipoDeConhecimento.Inferencia?"Candidato consolidado como inferido; uma inferência não vira fato confirmado automaticamente.":"Candidato consolidado com sua confirmação e proveniência preservada.";
        }
        if(alteracao.Acao is "fonte" or "fonte_remocao")
        {
            var documento=await fontes.ObterPorIdAsync(alteracao.Alvo!.Id,cancellationToken)??throw new InvalidOperationException("Fonte não encontrada.");
            if(!conversa.Permitida(documento.Sensibilidade)||documento.Revisao!=alteracao.Alvo.Revisao||documento.IdEspacoDeConhecimento!=conversa.Acesso.IdEspacoDeConhecimento||documento.IdProjeto!=conversa.Acesso.IdProjeto)
                throw new InvalidOperationException("Fonte mudou; refaça a consulta.");
            if(alteracao.Acao=="fonte_remocao")
            {await documentos.RemoverAsync(conversa.Acesso,documento.Id,documento.Revisao,cancellationToken);return "Fonte removida; texto e índices apagados, conhecimentos consolidados preservados.";}
            var revisaoAnterior=documento.Revisao;
            var fonteAtualizada=await documentos.ImportarAsync(conversa.Acesso,documento.Origem,documento.Formato,alteracao.Conteudo!,documento.Sensibilidade,documento.Revisao,cancellationToken);
            return fonteAtualizada.Revisao==revisaoAnterior?"Fonte já corresponde ao conteúdo; revisão mantida.":"Fonte atualizada com nova revisão; conhecimentos consolidados permanecem com a prova histórica.";
        }
        var atual=await consulta.CarregarAsync(conversa, alteracao.Alvo!, cancellationToken);
        if(alteracao.Acao=="invalidacao")
        {await conhecimentos.InvalidarAsync(atual.Id,atual.Revisao,prova,cancellationToken);return "Informação invalidada. Histórico preservado; não será reinjetada na busca/contexto.";}
        if(alteracao.Acao=="correcao")
        {
            await conhecimentos.CorrigirAsync(atual.Id,atual with{Conteudo=alteracao.Conteudo,Proveniencia=prova},cancellationToken);
            return "Correção registrada com histórico e proveniência; status inferido até nova confirmação.";
        }
        var segundo=await consulta.CarregarAsync(conversa, alteracao.SegundoAlvo!, cancellationToken);
        await relacoes.RelacionarAsync(conversa.Acesso.IdEspacoDeConhecimento,conversa.Acesso.IdProjeto,atual.Id,segundo.Id,TipoDeRelacao.ResolvidoPor,prova,cancellationToken);
        return "Relação registrada: a solução resolveu o incidente.";
    }
}
