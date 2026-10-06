using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;

namespace Dante.Application.ConversaDoBrain;

public sealed class ConversaDoBrainAppService(LeituraDoBrainAppService leitura, AutorizacaoDoBrain autorizacao,
    IEstadoDeConversaDoBrain estados, ConsultaDaConversa consulta, CapturaDaConversa capturaDaConversa,
    FontesDaConversa fontesDaConversa, AlteracoesDaConversa alteracoes, InspecaoDaConversa inspecao,
    ContextoDeTrabalhoDaConversa contextoDeTrabalho, AvaliacaoDaConversa avaliacao)
{
    public async Task<string?> AtenderAsync(AcessoAoBrain acesso,PedidoDeConversaDto pedido,CancellationToken cancellationToken=default)
    {
        await leitura.ValidarAcessoAsync(acesso,cancellationToken);
        ConversaDoBrainValidator.ValidarPedido(pedido);
        var identidade=autorizacao.Identidade!;var chave=new ChaveDeConversaDto(identidade.IdTenant,identidade.IdUsuario,acesso.IdEspacoDeConhecimento,acesso.IdProjeto,pedido.IdConversa);
        var conversa=new ContextoDaConversa(acesso,pedido,chave,estados);var intencao=ResolvedorDeIntencaoDoBrain.Resolver(pedido.Texto);
        // Uma nova intenção cancela a proposta anterior, evitando confirmar uma ação diferente da última pergunta.
        if(intencao.Intencao is not (IntencaoDoBrain.Nenhuma or IntencaoDoBrain.Confirmar or IntencaoDoBrain.Cancelar or IntencaoDoBrain.Escolher))
            conversa.Guardar(conversa.Estado with{Pendente=null,ConfirmacaoExpirada=false});
        if(intencao.Intencao==IntencaoDoBrain.Nenhuma)
        {
            if(conversa.Estado.Pendente?.Acao=="texto_captura")return await capturaDaConversa.CapturarAsync(conversa, conversa.Pedido.Texto,conversa.Estado.Pendente.Conteudo=="Solucao"?TipoDeConhecimento.Solucao:TipoDeConhecimento.Nota, cancellationToken);
            if(conversa.Estado.Pendente?.Acao=="texto_correcao")return alteracoes.PrepararAlteracao(conversa, "correcao",conversa.Estado.Pendente.Alvo,conversa.Pedido.Texto);
            if(conversa.Estado.Pendente?.Acao=="texto_consulta")return await consulta.ConsultarAsync(conversa, conversa.Pedido.Texto,true, cancellationToken);
            return null;
        }
        switch(intencao.Intencao)
        {
            case IntencaoDoBrain.Consultar:return await consulta.ConsultarAsync(conversa, intencao.Texto,false, cancellationToken);
            case IntencaoDoBrain.Experiencia:
                var termo=string.IsNullOrWhiteSpace(intencao.Texto)?conversa.Estado.UltimoTermo:intencao.Texto;
                if(string.IsNullOrWhiteSpace(termo)){conversa.Guardar(conversa.Estado with{Pendente=conversa.Pendente("texto_consulta")});return "Com qual problema? Descreva o assunto que devo buscar no Brain.";}
                return await consulta.ConsultarAsync(conversa, termo,true, cancellationToken);
            case IntencaoDoBrain.Capturar:
                var corpo=string.IsNullOrWhiteSpace(intencao.Texto)?conversa.Pedido.TrechoSelecionado:intencao.Texto;
                var tipo=ResolvedorDeIntencaoDoBrain.Normalizar(conversa.Pedido.Texto).Contains("como resolvemos",StringComparison.Ordinal)?TipoDeConhecimento.Solucao:TipoDeConhecimento.Nota;
                if(string.IsNullOrWhiteSpace(corpo)){conversa.Guardar(conversa.Estado with{Pendente=conversa.Pendente("texto_captura",texto:tipo.ToString())});return "Descreva como resolvemos ou responda citando o trecho a documentar. Vou preparar um candidato, sem registrar um fato automaticamente.";}
                return await capturaDaConversa.CapturarAsync(conversa, corpo,tipo, cancellationToken);
            case IntencaoDoBrain.ImportarFonte:return await fontesDaConversa.ImportarFonteAsync(conversa, intencao, cancellationToken);
            case IntencaoDoBrain.CapturarFonte:return await fontesDaConversa.CapturarFonteAsync(conversa, intencao, cancellationToken);
            case IntencaoDoBrain.Corrigir:return alteracoes.PrepararAlteracao(conversa, "correcao",conversa.Escolher(intencao.Numero),intencao.Texto);
            case IntencaoDoBrain.Invalidar:return alteracoes.PrepararAlteracao(conversa, "invalidacao",conversa.Escolher(intencao.Numero));
            case IntencaoDoBrain.Escolher:
                if(conversa.Estado.Pendente?.Acao=="alvo_correcao")return alteracoes.PrepararAlteracao(conversa, "correcao",conversa.Escolher(intencao.Numero),conversa.Estado.Pendente.Conteudo);
                if(conversa.Estado.Pendente?.Acao=="alvo_invalidacao")return alteracoes.PrepararAlteracao(conversa, "invalidacao",conversa.Escolher(intencao.Numero));
                if(conversa.Estado.Pendente?.Acao=="alvo_origem")return await consulta.OrigemAsync(conversa, conversa.Escolher(intencao.Numero), cancellationToken);
                return "Escolha um resultado ao pedir origem, correção ou invalidação.";
            case IntencaoDoBrain.Origem:
                var origem=conversa.Escolher(intencao.Numero);
                if(origem is null){conversa.Guardar(conversa.Estado with{Pendente=conversa.Pendente("alvo_origem")});return "De qual informação? Escolha primeira/segunda ou o número na última lista.";}
                return await consulta.OrigemAsync(conversa, origem, cancellationToken);
            case IntencaoDoBrain.Relacionar:
                var incidente=intencao.Numero is not null?conversa.Escolher(intencao.Numero):Unico(TipoDeConhecimento.Incidente);
                var solucao=intencao.SegundoNumero is not null?conversa.Escolher(intencao.SegundoNumero):Unico(TipoDeConhecimento.Solucao);
                if(incidente is null||solucao is null||incidente.Tipo!=TipoDeConhecimento.Incidente||solucao.Tipo!=TipoDeConhecimento.Solucao||incidente.Origem!="conhecimento"||solucao.Origem!="conhecimento")
                    return "Preciso de um incidente e uma solução inequívocos na última consulta. Diga, por exemplo: relacione a solução 2 ao incidente 1.";
                conversa.Guardar(conversa.Estado with{Pendente=conversa.Pendente("relacao",incidente,segundo:solucao)});
                return $"Vou registrar que “{solucao.Descricao}” resolveu “{incidente.Descricao}”. Diga confirmar ou cancelar.";
            case IntencaoDoBrain.ListarCandidatos:return await capturaDaConversa.ListarCandidatosAsync(conversa, cancellationToken);
            case IntencaoDoBrain.Confirmar:
                if(conversa.Estado.Pendente is null&&intencao.Numero is not null&&conversa.Escolher(intencao.Numero) is {Origem:"candidato"} candidato)
                    conversa.Guardar(conversa.Estado with{Pendente=conversa.Pendente("captura",candidato)});
                if(conversa.Estado.ConfirmacaoExpirada)return "Essa confirmação expirou. Refaça a consulta e prepare novamente a alteração.";
                if(conversa.Estado.Pendente is null||conversa.Estado.Pendente.Acao is not ("captura" or "correcao" or "invalidacao" or "relacao" or "fonte" or "fonte_remocao"))return "Não há alteração completa aguardando confirmação. Consulte o item ou prepare um candidato primeiro.";
                var alteracao=conversa.Estado.Pendente;
                if(!conversa.ConsumirPendente())return "Essa confirmação expirou ou já foi utilizada. Refaça a consulta.";
                return await alteracoes.ExecutarAsync(conversa, alteracao, cancellationToken);
            case IntencaoDoBrain.Cancelar:
                if(conversa.Estado.Pendente?.Acao=="captura"&&conversa.ConsumirPendente())
                    await capturaDaConversa.DescartarAsync(conversa, cancellationToken);
                else conversa.Guardar(conversa.Estado with{Pendente=null});
                return "Alteração cancelada; nenhum conhecimento foi modificado.";
            case IntencaoDoBrain.Inspecionar:return await inspecao.InspecionarAsync(conversa, cancellationToken);
            case IntencaoDoBrain.Exportar:return await inspecao.ExportarAsync(conversa, cancellationToken);
            case IntencaoDoBrain.AtualizarContexto:return await contextoDeTrabalho.AtualizarContextoAsync(conversa, intencao.Texto, cancellationToken);
            case IntencaoDoBrain.MostrarContexto:return await contextoDeTrabalho.MostrarContextoAsync(conversa, cancellationToken);
            case IntencaoDoBrain.Metricas:return await avaliacao.MetricasAsync(conversa, cancellationToken);
            case IntencaoDoBrain.AvaliarRetomada:return await avaliacao.AvaliarAsync(conversa, intencao.Texto, cancellationToken);
            default:return null;
        }
        AlvoDeConversaDto? Unico(TipoDeConhecimento tipo)=>conversa.Estado.Resultados.Count(x=>x.Tipo==tipo&&x.Origem=="conhecimento")==1?conversa.Estado.Resultados.Single(x=>x.Tipo==tipo&&x.Origem=="conhecimento"):null;

    }
}
