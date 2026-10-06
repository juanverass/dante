using Dante.Application.AuditoriaDoBrain;
using Dante.Application.DocumentosFonte;
using Dante.Application.MetricasDoBrain;
using Dante.Application.BuscaDoBrain;
using Dante.Application.CapturaDeConhecimento;
using Dante.Application.Conhecimentos;
using Dante.Application.ContextosDeTrabalho;
using Dante.Application.RelacoesDeConhecimento;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.ContextosDeTrabalho;
using Dante.Domain.RelacoesDeConhecimento;
namespace Dante.Application.ConversaDoBrain;

public sealed class ConversaDoBrainAppService(BuscaDoBrainAppService busca,LeituraDoBrainAppService leitura,
    IConhecimentoAppService conhecimentos,ICapturaDeConhecimentoAppService captura,IRelacaoDeConhecimentoAppService relacoes,
    InspecaoDoBrainAppService auditoria,DocumentoFonteAppService documentos,IDocumentoFonteRepository fontes,AutorizacaoDoBrain autorizacao,IEstadoDeConversaDoBrain estados,
    ContextoDeTrabalhoAppService snapshots,MetricasDoBrainAppService metricas)
{
    public async Task<string?> AtenderAsync(AcessoAoBrain acesso,PedidoDeConversaDto pedido,CancellationToken cancellationToken=default)
    {
        await leitura.ValidarAcessoAsync(acesso,cancellationToken);
        if(string.IsNullOrWhiteSpace(pedido.IdConversa)||pedido.IdConversa.Length>200||pedido.Texto.Length>10000||pedido.ReferenciaDaMensagem.Length>2000)
            throw new ArgumentException("Conversa inválida.");
        var identidade=autorizacao.Identidade!;var chave=new ChaveDeConversaDto(identidade.IdTenant,identidade.IdUsuario,acesso.IdEspacoDeConhecimento,acesso.IdProjeto,pedido.IdConversa);
        var estado=estados.Obter(chave);var intencao=ResolvedorDeIntencaoDoBrain.Resolver(pedido.Texto);
        EstadoDeConversaDto Guardar(EstadoDeConversaDto novo)=>estados.Salvar(chave,novo);
        AlteracaoPendenteDto Pendente(string acao,AlvoDeConversaDto? alvo=null,string? texto=null,AlvoDeConversaDto? segundo=null)=>new(acao,alvo,segundo,texto,DateTimeOffset.UtcNow.AddMinutes(5));
        string PrepararAlteracao(string acao,AlvoDeConversaDto? alvo,string? texto=null)
        {
            if(alvo is null && estado.Resultados.Count==0)return "Primeiro consulte o Brain pelo assunto: o que você sabe sobre X? Depois escolha a informação a alterar.";
            if(alvo is null){Guardar(estado with{Pendente=Pendente("alvo_"+acao,texto:texto)});return "Qual informação? Escolha pelo número ou diga primeira/segunda na última lista. Nenhuma alteração foi feita.";}
            if(alvo.Origem=="fonte_bruta" && acao=="invalidacao")
            {
                Guardar(estado with{Pendente=Pendente("fonte_remocao",alvo)});
                return $"Vou remover a fonte inteira deste trecho: “{alvo.Descricao}”. Texto original e índices serão apagados; conhecimentos consolidados manterão suas provas. Diga confirmar ou cancelar.";
            }
            if(alvo.Origem!="conhecimento")return "Esse resultado é uma fonte bruta. Consulte um conhecimento consolidado para corrigir ou invalidar.";
            if(acao=="correcao"&&string.IsNullOrWhiteSpace(texto)){Guardar(estado with{Pendente=Pendente("texto_correcao",alvo)});return "Qual é a informação correta? Envie o texto; depois pedirei confirmação.";}
            ProtecaoDeSegredos.GarantirSeguro(texto);
            Guardar(estado with{Pendente=Pendente(acao,alvo,texto)});
            return acao=="invalidacao"?$"Vou invalidar “{alvo.Descricao}”. O histórico será preservado e o item deixará de ser recuperado. Diga confirmar ou cancelar.":
                $"Vou corrigir “{alvo.Descricao}” para “{Resumir(texto!,600)}”. A correção ficará inferida até nova confirmação. Diga confirmar ou cancelar.";
        }
        AlvoDeConversaDto? Escolher(int? numero)=>numero is { } n ? n>=1&&n<=estado.Resultados.Count?estado.Resultados[n-1]:null:
            estado.Resultados.Count==1?estado.Resultados[0]:null;
        // Uma nova intenção cancela a proposta anterior, evitando confirmar uma ação diferente da última pergunta.
        if(intencao.Intencao is not (IntencaoDoBrain.Nenhuma or IntencaoDoBrain.Confirmar or IntencaoDoBrain.Cancelar or IntencaoDoBrain.Escolher))
            estado=Guardar(estado with{Pendente=null,ConfirmacaoExpirada=false});
        if(intencao.Intencao==IntencaoDoBrain.Nenhuma)
        {
            if(estado.Pendente?.Acao=="texto_captura")return await CapturarAsync(pedido.Texto,estado.Pendente.Conteudo=="Solucao"?TipoDeConhecimento.Solucao:TipoDeConhecimento.Nota);
            if(estado.Pendente?.Acao=="texto_correcao")return PrepararAlteracao("correcao",estado.Pendente.Alvo,pedido.Texto);
            if(estado.Pendente?.Acao=="texto_consulta")return await ConsultarAsync(pedido.Texto,true);
            return null;
        }
        switch(intencao.Intencao)
        {
            case IntencaoDoBrain.Consultar:return await ConsultarAsync(intencao.Texto,false);
            case IntencaoDoBrain.Experiencia:
                var termo=string.IsNullOrWhiteSpace(intencao.Texto)?estado.UltimoTermo:intencao.Texto;
                if(string.IsNullOrWhiteSpace(termo)){Guardar(estado with{Pendente=Pendente("texto_consulta")});return "Com qual problema? Descreva o assunto que devo buscar no Brain.";}
                return await ConsultarAsync(termo,true);
            case IntencaoDoBrain.Capturar:
                var corpo=string.IsNullOrWhiteSpace(intencao.Texto)?pedido.TrechoSelecionado:intencao.Texto;
                var tipo=ResolvedorDeIntencaoDoBrain.Normalizar(pedido.Texto).Contains("como resolvemos",StringComparison.Ordinal)?TipoDeConhecimento.Solucao:TipoDeConhecimento.Nota;
                if(string.IsNullOrWhiteSpace(corpo)){Guardar(estado with{Pendente=Pendente("texto_captura",texto:tipo.ToString())});return "Descreva como resolvemos ou responda citando o trecho a documentar. Vou preparar um candidato, sem registrar um fato automaticamente.";}
                return await CapturarAsync(corpo,tipo);
            case IntencaoDoBrain.ImportarFonte:
                var origemFonte="nota:"+intencao.Nome;ProtecaoDeSegredos.GarantirSeguro(origemFonte,intencao.Texto);
                var existente=await fontes.ObterPelaOrigemAsync(acesso,origemFonte,cancellationToken);
                if(existente is null)
                {
                    await documentos.ImportarAsync(acesso,origemFonte,intencao.Formato!,intencao.Texto,cancellationToken:cancellationToken);
                    return "Fonte bruta adicionada. Importar não confirma conteúdo; consulte pelo assunto e selecione um trecho para preparar candidato.";
                }
                if(existente.Formato!=intencao.Formato)throw new ArgumentException("Formato da fonte mudou.");
                var alvoFonte=new AlvoDeConversaDto(existente.Id,existente.Revisao,"fonte_bruta",null,null,existente.Sensibilidade,Resumir(existente.Origem,500));
                Guardar(estado with{Pendente=Pendente("fonte",alvoFonte,intencao.Texto)});
                return $"A fonte {alvoFonte.Descricao} já existe. Vou atualizar a revisão e reconstruir os trechos, preservando conhecimento consolidado. Diga confirmar ou cancelar.";
            case IntencaoDoBrain.CapturarFonte:
                var selecionada=Escolher(intencao.Numero);
                if(selecionada is not {Origem:"fonte_bruta",NumeroDaParte:not null})return "Escolha o número de uma fonte bruta na última consulta.";
                var candidatoFonte=await documentos.GerarCandidatoAsync(acesso,selecionada.Id,selecionada.Revisao,selecionada.NumeroDaParte.Value,cancellationToken:cancellationToken);
                if(candidatoFonte.Estado!=EstadoDoCandidato.Pendente)return "Esse trecho já foi avaliado; não criei candidato duplicado.";
                var candidatoAlvo=new AlvoDeConversaDto(candidatoFonte.Id,candidatoFonte.Revisao,"candidato",candidatoFonte.Tipo,null,candidatoFonte.Sensibilidade,Resumir(candidatoFonte.Conteudo,600));
                Guardar(estado with{Pendente=Pendente("captura",candidatoAlvo)});
                return $"Preparei candidato do trecho exato: {candidatoAlvo.Descricao}\nProveniência inclui documento/revisão/hash/parte. Diga confirmar ou cancelar.";
            case IntencaoDoBrain.Corrigir:return PrepararAlteracao("correcao",Escolher(intencao.Numero),intencao.Texto);
            case IntencaoDoBrain.Invalidar:return PrepararAlteracao("invalidacao",Escolher(intencao.Numero));
            case IntencaoDoBrain.Escolher:
                if(estado.Pendente?.Acao=="alvo_correcao")return PrepararAlteracao("correcao",Escolher(intencao.Numero),estado.Pendente.Conteudo);
                if(estado.Pendente?.Acao=="alvo_invalidacao")return PrepararAlteracao("invalidacao",Escolher(intencao.Numero));
                if(estado.Pendente?.Acao=="alvo_origem")return await OrigemAsync(Escolher(intencao.Numero));
                return "Escolha um resultado ao pedir origem, correção ou invalidação.";
            case IntencaoDoBrain.Origem:
                var origem=Escolher(intencao.Numero);
                if(origem is null){Guardar(estado with{Pendente=Pendente("alvo_origem")});return "De qual informação? Escolha primeira/segunda ou o número na última lista.";}
                return await OrigemAsync(origem);
            case IntencaoDoBrain.Relacionar:
                var incidente=intencao.Numero is not null?Escolher(intencao.Numero):Unico(TipoDeConhecimento.Incidente);
                var solucao=intencao.SegundoNumero is not null?Escolher(intencao.SegundoNumero):Unico(TipoDeConhecimento.Solucao);
                if(incidente is null||solucao is null||incidente.Tipo!=TipoDeConhecimento.Incidente||solucao.Tipo!=TipoDeConhecimento.Solucao||incidente.Origem!="conhecimento"||solucao.Origem!="conhecimento")
                    return "Preciso de um incidente e uma solução inequívocos na última consulta. Diga, por exemplo: relacione a solução 2 ao incidente 1.";
                Guardar(estado with{Pendente=Pendente("relacao",incidente,segundo:solucao)});
                return $"Vou registrar que “{solucao.Descricao}” resolveu “{incidente.Descricao}”. Diga confirmar ou cancelar.";
            case IntencaoDoBrain.ListarCandidatos:
                var pendentes=await captura.ListarPendentesAsync(acesso.IdEspacoDeConhecimento,acesso.IdProjeto,10,cancellationToken);
                var alvos=pendentes.Where(x=>Permitida(x.Sensibilidade)).Select(x=>new AlvoDeConversaDto(x.Id,x.Revisao,"candidato",x.Tipo,null,x.Sensibilidade,Resumir(x.Conteudo,500))).ToArray();
                Guardar(estado with{Resultados=alvos,Pendente=null});
                return alvos.Length==0?"Nenhum candidato pendente neste escopo.":"Candidatos pendentes:\n"+string.Join('\n',alvos.Select((x,i)=>$"{i+1}. {x.Descricao} ({x.Tipo}, {x.Sensibilidade})"))+"\nDiga confirmar primeira/segunda para consolidar o candidato escolhido.";
            case IntencaoDoBrain.Confirmar:
                if(estado.Pendente is null&&intencao.Numero is not null&&Escolher(intencao.Numero) is {Origem:"candidato"} candidato)
                    estado=Guardar(estado with{Pendente=Pendente("captura",candidato)});
                if(estado.ConfirmacaoExpirada)return "Essa confirmação expirou. Refaça a consulta e prepare novamente a alteração.";
                if(estado.Pendente is null||estado.Pendente.Acao is not ("captura" or "correcao" or "invalidacao" or "relacao" or "fonte" or "fonte_remocao"))return "Não há alteração completa aguardando confirmação. Consulte o item ou prepare um candidato primeiro.";
                var alteracao=estado.Pendente;
                if(!estados.ConsumirPendente(chave,estado.Versao))return "Essa confirmação expirou ou já foi utilizada. Refaça a consulta.";
                var prova=Prova(pedido.Texto);
                if(alteracao.Acao=="captura")
                {
                    if(!Permitida(alteracao.Alvo!.Sensibilidade))throw new UnauthorizedAccessException("Candidato protegido.");
                    await captura.ConfirmarAsync(acesso.IdEspacoDeConhecimento,acesso.IdProjeto,alteracao.Alvo!.Id,alteracao.Alvo.Revisao,prova,cancellationToken);
                    return alteracao.Alvo.Tipo==TipoDeConhecimento.Inferencia?"Candidato consolidado como inferido; uma inferência não vira fato confirmado automaticamente.":"Candidato consolidado com sua confirmação e proveniência preservada.";
                }
                if(alteracao.Acao is "fonte" or "fonte_remocao")
                {
                    var documento=await fontes.ObterPorIdAsync(alteracao.Alvo!.Id,cancellationToken)??throw new InvalidOperationException("Fonte não encontrada.");
                    if(!Permitida(documento.Sensibilidade)||documento.Revisao!=alteracao.Alvo.Revisao||documento.IdEspacoDeConhecimento!=acesso.IdEspacoDeConhecimento||documento.IdProjeto!=acesso.IdProjeto)
                        throw new InvalidOperationException("Fonte mudou; refaça a consulta.");
                    if(alteracao.Acao=="fonte_remocao")
                    {await documentos.RemoverAsync(acesso,documento.Id,documento.Revisao,cancellationToken);return "Fonte removida; texto e índices apagados, conhecimentos consolidados preservados.";}
                    var revisaoAnterior=documento.Revisao;
                    var fonteAtualizada=await documentos.ImportarAsync(acesso,documento.Origem,documento.Formato,alteracao.Conteudo!,documento.Sensibilidade,documento.Revisao,cancellationToken);
                    return fonteAtualizada.Revisao==revisaoAnterior?"Fonte já corresponde ao conteúdo; revisão mantida.":"Fonte atualizada com nova revisão; conhecimentos consolidados permanecem com a prova histórica.";
                }
                var atual=await CarregarAsync(alteracao.Alvo!);
                if(alteracao.Acao=="invalidacao")
                {await conhecimentos.InvalidarAsync(atual.Id,atual.Revisao,prova,cancellationToken);return "Informação invalidada. Histórico preservado; não será reinjetada na busca/contexto.";}
                if(alteracao.Acao=="correcao")
                {
                    await conhecimentos.CorrigirAsync(atual.Id,atual with{Conteudo=alteracao.Conteudo,Proveniencia=prova},cancellationToken);
                    return "Correção registrada com histórico e proveniência; status inferido até nova confirmação.";
                }
                var segundo=await CarregarAsync(alteracao.SegundoAlvo!);
                await relacoes.RelacionarAsync(acesso.IdEspacoDeConhecimento,acesso.IdProjeto,atual.Id,segundo.Id,TipoDeRelacao.ResolvidoPor,prova,cancellationToken);
                return "Relação registrada: a solução resolveu o incidente.";
            case IntencaoDoBrain.Cancelar:
                if(estado.Pendente?.Acao=="captura"&&estados.ConsumirPendente(chave,estado.Versao))
                    await captura.DescartarAsync(acesso.IdEspacoDeConhecimento,acesso.IdProjeto,estado.Pendente.Alvo!.Id,estado.Pendente.Alvo.Revisao,Prova(pedido.Texto),cancellationToken);
                else Guardar(estado with{Pendente=null});
                return "Alteração cancelada; nenhum conhecimento foi modificado.";
            case IntencaoDoBrain.Inspecionar:
                var audit=await auditoria.InspecionarAsync(acesso,new(){Limite=10},cancellationToken);
                return $"Brain: {Resumir(audit.Espaco.Nome,100)}. {audit.Conhecimentos.Count} conhecimentos nesta página, {audit.Relacoes.Count} relações e {audit.Fontes.Count} fontes brutas."+
                    (audit.TemMais?" Há mais itens; refine a consulta.":"")+"\nConsulte por assunto para ver conteúdo e origem.";
            case IntencaoDoBrain.Exportar:
                var export=await auditoria.ExportarAsync(acesso,cancellationToken);
                if(export.Markdown.Length>12000)return "A exportação é maior que o limite desta conversa. Use a exportação local Markdown/JSON para salvar os arquivos.";
                return export.Markdown;
            case IntencaoDoBrain.AtualizarContexto:return await AtualizarContextoAsync(intencao.Texto);
            case IntencaoDoBrain.MostrarContexto:
                var contextoAtual=await snapshots.RetomarAsync(acesso,cancellationToken);
                return contextoAtual is null?"Nenhum contexto de trabalho ativo neste escopo. "+FormatoDoContexto:DescreverContexto(contextoAtual);
            case IntencaoDoBrain.Metricas:return MetricasDoBrainAppService.Formatar(await metricas.ResumirAsync(acesso,cancellationToken));
            case IntencaoDoBrain.AvaliarRetomada:return await AvaliarAsync(intencao.Texto);
            default:return null;
        }
        AlvoDeConversaDto? Unico(TipoDeConhecimento tipo)=>estado.Resultados.Count(x=>x.Tipo==tipo&&x.Origem=="conhecimento")==1?estado.Resultados.Single(x=>x.Tipo==tipo&&x.Origem=="conhecimento"):null;
        ProvenienciaDto Prova(string trecho)=>new(){IdResponsavel=acesso.IdUsuario,Origem="conversa explícita",ReferenciaDaFonte=pedido.ReferenciaDaMensagem,TrechoDaFonte=trecho};
        bool Permitida(Sensibilidade classe)=>classe<Sensibilidade.Confidencial || classe==Sensibilidade.Confidencial && acesso.PermitirConfidencial || classe==Sensibilidade.Secreto && acesso.PermitirSecreto;
        async Task<ConhecimentoDto> CarregarAsync(AlvoDeConversaDto alvo)
        {
            var item=await conhecimentos.ObterPorIdAsync(alvo.Id,cancellationToken)??throw new InvalidOperationException("Item não encontrado; refaça a consulta.");
            if(!Permitida(item.Sensibilidade))throw new UnauthorizedAccessException("Informação protegida.");
            if(item.IdEspacoDeConhecimento!=acesso.IdEspacoDeConhecimento||item.IdProjeto!=acesso.IdProjeto||item.Revisao!=alvo.Revisao||item.Status is StatusDoConhecimento.Inativo or StatusDoConhecimento.Substituido)
                throw new InvalidOperationException("Item mudou; refaça a consulta.");
            return item;
        }
        async Task<string> ConsultarAsync(string termo,bool experiencia)
        {
            if(string.IsNullOrWhiteSpace(termo)||termo.Length>2000)throw new ArgumentException("Consulta inválida.");
            var resultado=await busca.BuscarAsync(acesso,new(){Texto=termo,Limite=10,Tipos=experiencia?[TipoDeConhecimento.Incidente,TipoDeConhecimento.Solucao,TipoDeConhecimento.Aprendizado,TipoDeConhecimento.Procedimento]:[]},cancellationToken);
            var relevantes=experiencia?resultado.Resultados.Where(x=>x.Origem==OrigemDoResultado.FonteBruta||x.Item.Tipo is TipoDeConhecimento.Incidente or TipoDeConhecimento.Solucao or TipoDeConhecimento.Aprendizado or TipoDeConhecimento.Procedimento).ToArray():resultado.Resultados.ToArray();
            var alvos=relevantes.Select(x=>new AlvoDeConversaDto(x.Item.Id,x.Item.Revisao,x.Origem==OrigemDoResultado.Conhecimento?"conhecimento":"fonte_bruta",x.Origem==OrigemDoResultado.Conhecimento?x.Item.Tipo:null,
                x.Origem==OrigemDoResultado.Conhecimento?x.Item.Status:null,x.Item.Sensibilidade,Resumir(x.Item.Conteudo??x.Item.DadosEstruturados??"conteúdo protegido",500),x.Fonte?.Numero)).ToArray();
            Guardar(estado with{Resultados=alvos,Pendente=null,UltimoTermo=termo});
            return alvos.Length==0?"Não encontrei informação autorizada neste escopo. Não consultei outros espaços.":
                string.Join('\n',alvos.Select((x,i)=>$"{i+1}. {x.Descricao}\n   {(x.Origem=="fonte_bruta"?"Fonte bruta, não consolidada":$"{x.Tipo}, {x.Status}")}; {x.Sensibilidade}. Origem: {Resumir(relevantes[i].Item.Origem??"não informada",200)}."))+"\nPeça a origem ou escolha um item para corrigir/invalidar.";
        }
        async Task<string> CapturarAsync(string texto,TipoDeConhecimento tipo)
        {
            var colon=texto.IndexOf(':');
            if(colon>0&&Enum.TryParse<TipoDeConhecimento>(ResolvedorDeIntencaoDoBrain.Normalizar(texto[..colon]),true,out var explicito))
            {tipo=explicito;texto=texto[(colon+1)..].Trim();}
            ProtecaoDeSegredos.GarantirSeguro(texto);
            if(texto.Length>10000)throw new ArgumentException("Selecione um trecho de até 10000 caracteres.");
            var selecionado=pedido.TrechoSelecionado is not null&&texto==pedido.TrechoSelecionado;
            var p=Prova(texto) with{ReferenciaDaFonte=selecionado?pedido.ReferenciaDoTrecho??pedido.ReferenciaDaMensagem:pedido.ReferenciaDaMensagem};
            var candidato=await captura.CapturarAsync(new(){IdEspacoDeConhecimento=acesso.IdEspacoDeConhecimento,IdProjeto=acesso.IdProjeto,Tipo=tipo,Conteudo=texto,
                Sensibilidade=Sensibilidade.Pessoal,Natureza=selecionado?NaturezaDoConteudo.FonteSelecionada:NaturezaDoConteudo.DitoPeloUsuario,Modo=ModoDeCaptura.Explicita,
                Justificativa="Captura explicitamente solicitada na conversa; aguardando confirmação.",Proveniencia=p},cancellationToken);
            if(candidato.Estado!=EstadoDoCandidato.Pendente)return "Esse conteúdo já foi avaliado; não criei um candidato duplicado.";
            var alvo=new AlvoDeConversaDto(candidato.Id,candidato.Revisao,"candidato",candidato.Tipo,null,candidato.Sensibilidade,Resumir(candidato.Conteudo,600));
            Guardar(estado with{Pendente=Pendente("captura",alvo)});
            return $"Preparei um candidato ({candidato.Tipo}, {candidato.Sensibilidade}): {alvo.Descricao}\nNão é um fato confirmado. Diga confirmar para consolidar ou cancelar.";
        }
        // Snapshot operacional selecionado pelo usuário (#145): campos omitidos são mantidos e listas informadas substituem
        // as anteriores. Não cria Conhecimento nem guarda a conversa.
        async Task<string> AtualizarContextoAsync(string corpo)
        {
            if(string.IsNullOrWhiteSpace(corpo))return FormatoDoContexto;
            var campos=new Dictionary<string,string>(StringComparer.Ordinal);var listas=new Dictionary<string,List<string>>(StringComparer.Ordinal);
            foreach(var parte in corpo.Split([';','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
            {
                var colon=parte.IndexOf(':');
                var campo=colon<=0?null:ResolvedorDeIntencaoDoBrain.Normalizar(parte[..colon].Trim()) switch
                {
                    "objetivo"=>"objetivo","tarefa"=>"tarefa","progresso"=>"progresso","resultado" or "ultimo resultado"=>"resultado",
                    "pendencia" or "pendencias"=>"pendencias","proximo passo" or "proximos passos"=>"passos","referencia" or "referencias"=>"referencias",_=>null
                };
                var valor=colon<=0?"":parte[(colon+1)..].Trim();
                if(campo is null||valor.Length==0)return "Campo do contexto de trabalho não reconhecido ou vazio; nada foi alterado. "+FormatoDoContexto;
                if(campo is "pendencias" or "passos" or "referencias"){if(!listas.TryGetValue(campo,out var lista))listas[campo]=lista=[];lista.Add(valor);}
                else campos[campo]=valor;
            }
            var anterior=await snapshots.RetomarAsync(acesso,cancellationToken);var d=anterior?.Dados;
            string Campo(string nome,string? atual)=>campos.TryGetValue(nome,out var valor)?valor:atual??"não informado";
            IReadOnlyList<string> Lista(string nome,IReadOnlyList<string>? atual)=>listas.TryGetValue(nome,out var valor)?valor:atual??[];
            var dados=new DadosDoContexto(Campo("objetivo",d?.Objetivo),Campo("tarefa",d?.Tarefa),Campo("progresso",d?.Progresso),d?.IdsDecisoesConfirmadas??[],
                Lista("referencias",d?.Referencias),Lista("pendencias",d?.Pendencias),Lista("passos",d?.ProximosPassos),Campo("resultado",d?.UltimoResultado));
            var salvo=await snapshots.SubstituirAsync(acesso,anterior?.Revisao??0,dados,anterior?.Sensibilidade??Sensibilidade.Pessoal,"conversa explícita",anterior?.ExpiraEm,cancellationToken);
            return "Contexto de trabalho atualizado.\n"+DescreverContexto(salvo);
        }
        // Avaliação humana da retomada (#148): campos omitidos ficam "não avaliados", nunca zero presumido.
        async Task<string> AvaliarAsync(string corpo)
        {
            const string formato="Diga: avalie a retomada: repetições: 0; esclarecimentos: 0; concluída: sim; contexto adicional: não; incorretos: 0; irrelevantes: 0; relevantes: 3. Campos omitidos ficam como não avaliados.";
            if(string.IsNullOrWhiteSpace(corpo))return formato;
            var avaliacao=new MetricaDoBrainDto();
            foreach(var parte in corpo.Split([';','\n',','],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
            {
                var colon=parte.IndexOf(':');var campo=colon<=0?"":ResolvedorDeIntencaoDoBrain.Normalizar(parte[..colon].Trim());
                var valor=colon<=0?"":ResolvedorDeIntencaoDoBrain.Normalizar(parte[(colon+1)..].Trim().TrimEnd('.'));
                int? numero=int.TryParse(valor,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var n)?n:null;
                bool? sim=valor is "sim" or "s"?true:valor is "nao" or "n"?false:null;
                MetricaDoBrainDto? lida=campo switch
                {
                    "repeticoes" or "repeticao" or "repeti" when numero is not null=>avaliacao with{Repeticoes=numero},
                    "esclarecimentos" or "esclarecimento" when numero is not null=>avaliacao with{Esclarecimentos=numero},
                    "concluida" or "tarefa concluida" when sim is not null=>avaliacao with{Concluida=sim},
                    "contexto adicional" or "buscou contexto" or "busquei contexto" when sim is not null=>avaliacao with{BuscouContextoAdicional=sim},
                    "incorretos" or "obsoletos" or "incorretos ou obsoletos" when numero is not null=>avaliacao with{Incorretos=numero},
                    "irrelevantes" when numero is not null=>avaliacao with{Irrelevantes=numero},
                    "relevantes" when numero is not null=>avaliacao with{Relevantes=numero},
                    _=>null
                };
                if(lida is null)return "Campo ou valor de avaliação inválido; nada foi registrado. "+formato;
                avaliacao=lida;
            }
            try
            {
                var sessao=await metricas.AvaliarAsync(acesso,pedido.IdSessao,avaliacao,cancellationToken);
                return $"Avaliação registrada para a sessão {sessao[(sessao.LastIndexOf(':')+1)..]}. Diga métricas do Brain para ver o resumo.";
            }
            catch(InvalidOperationException){return "Nenhuma conversa com contexto do Brain medida neste escopo; converse com o agente antes de avaliar.";}
        }
        async Task<string> OrigemAsync(AlvoDeConversaDto? alvo)
        {
            if(alvo is null)return "Escolha um resultado inequívoco na última consulta.";
            if(alvo.Origem=="fonte_bruta")
            {
                var fonte=await fontes.ObterPorIdAsync(alvo.Id,cancellationToken);
                if(fonte is null || fonte.Removido || fonte.Revisao!=alvo.Revisao || fonte.IdEspacoDeConhecimento!=acesso.IdEspacoDeConhecimento || fonte.IdProjeto!=acesso.IdProjeto || !Permitida(fonte.Sensibilidade))
                    return "A fonte mudou, foi removida ou está protegida; refaça a consulta.";
                return $"Fonte bruta: {Resumir(fonte.Origem,500)}. Formato: {fonte.Formato}; revisão {fonte.Revisao}; hash {fonte.Hash[..12]}. Importar não confirmou o conteúdo.";
            }
            var item=await CarregarAsync(alvo);
            var provas=item.Historico.Where(x=>Permitida(x.Sensibilidade)).Select(x=>x.Proveniencia).Where(x=>x.ReferenciaDaFonte is not null).Distinct().Take(5).ToArray();
            var linhas=new List<string>();
            foreach(var p in provas)
            {
                var referencia=p.ReferenciaDaFonte??"não informada";
                if(referencia.StartsWith("conversa:",StringComparison.Ordinal))referencia="mensagem selecionada da conversa";
                else if(Uri.TryCreate(referencia,UriKind.Absolute,out var uri)&&uri.Scheme=="brain"&&uri.Host=="fonte"&&uri.Segments.Length>1&&Guid.TryParse(uri.Segments[1].Trim('/'),out var idFonte))
                {
                    var fonte=await fontes.ObterPorIdAsync(idFonte,cancellationToken);
                    referencia=fonte is not null && Permitida(fonte.Sensibilidade)?fonte.Origem+(fonte.Removido?" (original removido)":""):"fonte protegida ou indisponível";
                }
                linhas.Add($"Origem: {Resumir(p.Origem,200)}. Fonte: {Resumir(referencia,500)}. Revisão da fonte: {Resumir(p.RevisaoDaFonte??"não informada",100)}. Evidência: {Resumir(p.TrechoDaFonte??"não registrada",700)}.");
            }
            return $"{item.Tipo}; {item.Status}; {item.Sensibilidade}; revisão {item.Revisao}.\n"+(linhas.Count==0?$"Origem: {Resumir(item.Proveniencia.Origem,200)}.":string.Join('\n',linhas));
        }
    }
    private const string FormatoDoContexto="Diga: atualize o contexto de trabalho: objetivo: ...; tarefa: ...; progresso: ...; resultado: ...; pendência: ...; próximo passo: ...; referência: ... Campos omitidos são mantidos; listas informadas substituem as anteriores.";
    private static string DescreverContexto(ContextoDeTrabalhoDto contexto)
    {
        var d=contexto.Dados;string Lista(IReadOnlyList<string> itens)=>itens.Count==0?"nenhum":string.Join("; ",itens.Select(x=>Resumir(x,500)));
        return $"Contexto de trabalho (revisão {contexto.Revisao}, {contexto.Sensibilidade}):\nObjetivo: {Resumir(d.Objetivo,2000)}\nTarefa: {Resumir(d.Tarefa,2000)}\n"+
            $"Progresso: {Resumir(d.Progresso,2000)}\nÚltimo resultado: {Resumir(d.UltimoResultado,2000)}\nPendências: {Lista(d.Pendencias)}\n"+
            $"Próximos passos: {Lista(d.ProximosPassos)}\nReferências: {Lista(d.Referencias)}\nDecisões confirmadas referenciadas: {d.IdsDecisoesConfirmadas.Count}.\n"+
            "É estado operacional, não fato confirmado; entra no contexto das próximas sessões deste escopo, e /clear e /compact não o alteram.";
    }
    private static string Resumir(string texto,int limite)
    {
        texto=ProtecaoDeSegredos.Redigir(texto)!;if(texto.Length<=limite)return texto;
        if(char.IsHighSurrogate(texto[limite-1]))limite--;return texto[..limite]+"…";
    }
}
