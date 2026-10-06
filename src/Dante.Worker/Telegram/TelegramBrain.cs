using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Dante.Application.ConstrucaoDeContexto;
using Dante.Application.ConversaDoBrain;
using Dante.Application.EspacosDeConhecimento;
using Dante.Application.Projetos;
using Dante.Application.SegurancaDoBrain;
using Dante.Application.Comum;
using Dante.Infrastructure.SegurancaDoBrain;
using Microsoft.Extensions.Configuration;
namespace Dante.Worker.Telegram;

// Adapter de entrada: resolve identidade/seleção fora do texto enviado aos agentes.
public sealed class TelegramBrain(IServiceScopeFactory scopeFactory,IConfiguration configuration) : IContinuidadeDoBrain
{
    private sealed record Selecao(Guid? Espaco=null,Guid? Projeto=null,IReadOnlyList<EspacoDeConhecimentoDto>? Espacos=null,IReadOnlyList<ProjetoDto>? Projetos=null,
        string? NomeEspaco=null,string? NomeProjeto=null);
    private readonly ConcurrentDictionary<(Guid Tenant,Guid Usuario,long Chat,long Topico),Selecao> selecoes=[];
    public async Task<string?> AtenderAsync(TelegramMessage mensagem,string texto,CancellationToken cancellationToken=default)
    {
        var explicito=string.Equals(texto,"/brain",StringComparison.OrdinalIgnoreCase)||texto.StartsWith("/brain ",StringComparison.OrdinalIgnoreCase);
        if(string.IsNullOrWhiteSpace(configuration.GetConnectionString("Dante")))return explicito?"Brain não configurado neste host.":null;
        if(mensagem.From is null)return null;
        if(texto.StartsWith('/')&&!explicito)
        {
            if(texto is "/clear" or "/compact")
            {
                using var limpeza=scopeFactory.CreateScope();var id=limpeza.ServiceProvider.GetRequiredService<IdentidadeTelegramDoBrain>().Resolver(mensagem.From.Id);
                limpeza.ServiceProvider.GetRequiredService<IEstadoDeConversaDoBrain>().LimparConversa(id,IdDaConversa(mensagem));
            }
            return null;
        }
        try
        {
            using var bootstrap=scopeFactory.CreateScope();var identidade=bootstrap.ServiceProvider.GetRequiredService<IdentidadeTelegramDoBrain>().Resolver(mensagem.From.Id);
            var chave=(identidade.IdTenant,identidade.IdUsuario,mensagem.Chat.Id,mensagem.MessageThreadId??0);
            var conversa=IdDaConversa(mensagem);
            var estado=selecoes.GetOrAdd(chave,_=>new());
            var contexto=bootstrap.ServiceProvider.GetRequiredService<AutorizacaoDoBrain>();contexto.Estabelecer(identidade);
            var tipo=ResolvedorDeIntencaoDoBrain.Resolver(texto);
            var normal=ResolvedorDeIntencaoDoBrain.Normalizar(explicito?texto.Length>7?texto[7..].Trim():"":texto);
            if(explicito && string.Equals(texto,"/brain",StringComparison.OrdinalIgnoreCase))normal="ajuda";
            var comandoEscopo=normal is "ajuda" or "help" or "listar espacos" or "liste espacos" or "meus espacos" or "listar projetos" or "liste projetos" or "usar sem projeto" or "sem projeto" ||
                normal.StartsWith("criar espaco ",StringComparison.Ordinal)||normal.StartsWith("crie um espaco ",StringComparison.Ordinal)||
                normal.StartsWith("usar espaco ",StringComparison.Ordinal)||normal.StartsWith("usar projeto ",StringComparison.Ordinal)||normal.StartsWith("criar projeto ",StringComparison.Ordinal);
            var estadoConversa=estado.Espaco is { } idEspaco?bootstrap.ServiceProvider.GetRequiredService<IEstadoDeConversaDoBrain>().Obter(new(identidade.IdTenant,identidade.IdUsuario,idEspaco,estado.Projeto,conversa)):null;
            if(!explicito && !comandoEscopo && (tipo.Intencao==IntencaoDoBrain.Nenhuma && estadoConversa?.Pendente?.Acao is not ("texto_captura" or "texto_correcao" or "texto_consulta") ||
                tipo.Intencao is IntencaoDoBrain.Confirmar or IntencaoDoBrain.Cancelar or IntencaoDoBrain.Escolher && estadoConversa?.Pendente is null && estadoConversa?.ContextoDeAlteracao!=true && !(estadoConversa?.Resultados.Count>0) &&
                !(tipo.Intencao==IntencaoDoBrain.Confirmar && tipo.Numero is not null && estadoConversa?.Resultados.Any(x=>x.Origem=="candidato")==true)))return null;
            var escopos=bootstrap.ServiceProvider.GetRequiredService<IEspacoDeConhecimentoAppService>();
            var listaEspacos=await escopos.PesquisarAsync(new(identidade.IdUsuario,Limite:100),cancellationToken);
            if(normal is "ajuda" or "help")return "Brain: diga o que você sabe sobre X?, já resolvemos algo parecido?, documente como resolvemos isso, essa informação está errada ou de onde veio essa informação?. Para selecionar, diga listar espaços / usar espaço Nome / usar projeto Nome / usar sem projeto. Alterações pedem confirmação. "+
                "Estado do trabalho: atualize o contexto de trabalho: objetivo: ...; tarefa: ...; próximo passo: ... / mostre o contexto de trabalho. "+
                "Conversas com os agentes recebem automaticamente o contexto relevante do escopo selecionado.";
            if(normal is "listar espacos" or "liste espacos" or "meus espacos")
            {
                selecoes[chave]=estado with{Espacos=listaEspacos};
                return listaEspacos.Count==0?"Você ainda não tem espaços. Diga criar espaço Pessoal.":"Espaços:\n"+string.Join('\n',listaEspacos.Select((x,i)=>$"{i+1}. {ProtecaoDeSegredos.Redigir(x.Nome)}"))+"\nDiga usar espaço Nome ou usar espaço 1.";
            }
            if(normal.StartsWith("criar espaco ",StringComparison.Ordinal)||normal.StartsWith("crie um espaco ",StringComparison.Ordinal))
            {
                var prefixo=normal.StartsWith("criar espaco ",StringComparison.Ordinal)?13:15;
                var nome=texto[(explicito?7:0)..].Trim()[prefixo..].Trim();ProtecaoDeSegredos.GarantirSeguro(nome);
                var novo=await escopos.AdicionarAsync(new(){IdUsuario=identidade.IdUsuario,IdTenant=identidade.IdTenant,Nome=nome},cancellationToken);
                selecoes[chave]=new(novo.Id,NomeEspaco:novo.Nome);Limpar();return $"Espaço {ProtecaoDeSegredos.Redigir(novo.Nome)} criado e selecionado, sem projeto.";
            }
            if(normal.StartsWith("usar espaco ",StringComparison.Ordinal))
            {
                var nome=texto[(explicito?7:0)..].Trim()[12..].Trim();
                var candidatos=int.TryParse(nome,out var numero)&&estado.Espacos is not null&&numero>=1&&numero<=estado.Espacos.Count?
                    listaEspacos.Where(x=>x.Id==estado.Espacos[numero-1].Id).ToArray():listaEspacos.Where(x=>string.Equals(x.Nome,nome,StringComparison.OrdinalIgnoreCase)).ToArray();
                if(candidatos.Length!=1)return "Espaço não encontrado ou nome ambíguo. Diga listar espaços e escolha um número da lista.";
                selecoes[chave]=new(candidatos[0].Id,NomeEspaco:candidatos[0].Nome);Limpar();return $"Espaço {ProtecaoDeSegredos.Redigir(candidatos[0].Nome)} selecionado, sem projeto.";
            }
            if(estado.Espaco is null&&listaEspacos.Count==1){estado=new(listaEspacos[0].Id,NomeEspaco:listaEspacos[0].Nome);selecoes[chave]=estado;}
            var e=listaEspacos.SingleOrDefault(x=>x.Id==estado.Espaco);
            if(e is null)
            {
                if(tipo.Intencao==IntencaoDoBrain.Nenhuma&&!explicito)return null;
                return listaEspacos.Count==0?"Você ainda não tem espaços. Diga criar espaço Pessoal.":"Selecione um espaço pelo nome: diga listar espaços e usar espaço Nome.";
            }
            using var operacao=scopeFactory.CreateScope();
            var acesso=new AcessoAoBrain(identidade.IdUsuario,e.Id,estado.Projeto);
            // Permissões padrão não incluem Confidential/Secret; mensagem nunca altera grants.
            operacao.ServiceProvider.GetRequiredService<AutorizacaoDoBrain>().Estabelecer(identidade,acesso);
            var projetos=operacao.ServiceProvider.GetRequiredService<IProjetoAppService>();
            if(normal is "usar sem projeto" or "sem projeto")
            {selecoes[chave]=estado with{Projeto=null,NomeProjeto=null};Limpar();return "Escopo selecionado sem projeto.";}
            if(normal is "listar projetos" or "liste projetos")
            {
                using var raiz=scopeFactory.CreateScope();raiz.ServiceProvider.GetRequiredService<AutorizacaoDoBrain>().Estabelecer(identidade,acesso with{IdProjeto=null});
                var itens=await raiz.ServiceProvider.GetRequiredService<IProjetoAppService>().PesquisarAsync(new(e.Id,Limite:100),cancellationToken);
                selecoes[chave]=estado with{Projetos=itens};return itens.Count==0?"Nenhum projeto. Você pode continuar sem projeto ou dizer criar projeto Nome.":"Projetos:\n"+string.Join('\n',itens.Select((x,i)=>$"{i+1}. {ProtecaoDeSegredos.Redigir(x.Nome)}"))+"\nDiga usar projeto Nome ou usar projeto 1.";
            }
            if(normal.StartsWith("usar projeto ",StringComparison.Ordinal)||normal.StartsWith("criar projeto ",StringComparison.Ordinal))
            {
                using var raiz=scopeFactory.CreateScope();raiz.ServiceProvider.GetRequiredService<AutorizacaoDoBrain>().Estabelecer(identidade,acesso with{IdProjeto=null});
                var servico=raiz.ServiceProvider.GetRequiredService<IProjetoAppService>();
                var criar=normal.StartsWith("criar projeto ",StringComparison.Ordinal);var nome=texto[(explicito?7:0)..].Trim()[(criar?14:13)..].Trim();ProtecaoDeSegredos.GarantirSeguro(nome);
                var itens=await servico.PesquisarAsync(new(e.Id,Limite:100),cancellationToken);
                var candidatos=criar?new[]{await servico.AdicionarAsync(new(){IdEspacoDeConhecimento=e.Id,Nome=nome},cancellationToken)}:
                    int.TryParse(nome,out var numero)&&estado.Projetos is not null&&numero>=1&&numero<=estado.Projetos.Count?
                    itens.Where(x=>x.Id==estado.Projetos[numero-1].Id).ToArray():itens.Where(x=>string.Equals(x.Nome,nome,StringComparison.OrdinalIgnoreCase)).ToArray();
                if(candidatos.Length!=1)return "Projeto não encontrado ou nome ambíguo. Diga listar projetos e escolha um número da lista.";
                selecoes[chave]=estado with{Projeto=candidatos[0].Id,NomeProjeto=candidatos[0].Nome};Limpar();return $"Projeto {ProtecaoDeSegredos.Redigir(candidatos[0].Nome)} {(criar?"criado e ":"")}selecionado.";
            }
            var selecionado=mensagem.ReplyToMessage is {Text:not null} reply&&reply.Chat.Id==mensagem.Chat.Id && reply.MessageThreadId==mensagem.MessageThreadId?reply:null;
            var pedido=new PedidoDeConversaDto(conversa,texto,$"conversa:{conversa}:{mensagem.MessageId}",selecionado?.Text,
                selecionado is null?null:$"conversa:{conversa}:{selecionado.MessageId}");
            var resposta=await operacao.ServiceProvider.GetRequiredService<ConversaDoBrainAppService>().AtenderAsync(acesso,pedido,cancellationToken);
            return resposta??(explicito?"Não entendi a intenção Brain. Diga /brain ajuda para ver exemplos.":null);
            void Limpar()=>bootstrap.ServiceProvider.GetRequiredService<IEstadoDeConversaDoBrain>().LimparConversa(identidade,conversa);
        }
        catch(UnauthorizedAccessException){return "Brain: identidade ou escopo não autorizado. Selecione um espaço permitido.";}
        catch(ConflitoDeConcorrenciaException){return "A informação mudou enquanto você confirmava. Refaça a consulta antes de alterar.";}
        catch(ArgumentException){return "Brain: entrada inválida ou protegida. Revise o texto e escolha um alvo inequívoco.";}
        catch(InvalidOperationException){return "Brain: item/estado mudou ou está indisponível. Refaça a consulta e confirme novamente.";}
        catch(Exception ex) when(ex is not OperationCanceledException){return "Brain indisponível. Confira a configuração do host.";}
    }
    // Estado por sessão em memória: só chaves/revisões e custo do que foi aceito, nunca o conteúdo injetado.
    private sealed record Injecao(string Escopo,IReadOnlyDictionary<string,int> Injetados,DateTimeOffset AtualizadoEm,int Itens,int Tokens,bool Bootstrap,bool Snapshot);
    private readonly ConcurrentDictionary<string,Injecao> injecoes=new(StringComparer.Ordinal);
    public bool Configurado=>!string.IsNullOrWhiteSpace(configuration.GetConnectionString("Dante"));
    public async Task<ContextoParaTurno?> PrepararAsync(TelegramMessage mensagem,string idSessao,string texto,CancellationToken cancellationToken=default)
    {
        if(!Configurado||mensagem.From is null||string.IsNullOrWhiteSpace(texto))return null;
        try
        {
            IdentidadeDoBrain identidade;Selecao estado;(Guid,Guid,long,long) chave;
            using(var bootstrap=scopeFactory.CreateScope())
            {
                identidade=bootstrap.ServiceProvider.GetRequiredService<IdentidadeTelegramDoBrain>().Resolver(mensagem.From.Id);
                chave=(identidade.IdTenant,identidade.IdUsuario,mensagem.Chat.Id,mensagem.MessageThreadId??0);
                estado=selecoes.GetOrAdd(chave,_=>new());
                if(estado.Espaco is null)
                {
                    // Mesma regra da conversa Brain: um único espaço é selecionado sem pedir nada; vários exigem escolha.
                    bootstrap.ServiceProvider.GetRequiredService<AutorizacaoDoBrain>().Estabelecer(identidade);
                    var espacos=await bootstrap.ServiceProvider.GetRequiredService<IEspacoDeConhecimentoAppService>().PesquisarAsync(new(identidade.IdUsuario,Limite:100),cancellationToken);
                    if(espacos.Count!=1)return null;
                    estado=new(espacos[0].Id,NomeEspaco:espacos[0].Nome);selecoes[chave]=estado;
                }
            }
            var escopo=Escopo(estado)!;var anterior=injecoes.GetValueOrDefault(idSessao);
            var mudou=anterior is not null&&anterior.Escopo!=escopo;var inicial=anterior is null||mudou;
            using var operacao=scopeFactory.CreateScope();
            var acesso=new AcessoAoBrain(identidade.IdUsuario,estado.Espaco!.Value,estado.Projeto);
            operacao.ServiceProvider.GetRequiredService<AutorizacaoDoBrain>().Estabelecer(identidade,acesso);
            var pacote=await operacao.ServiceProvider.GetRequiredService<ConstrutorDeContextoAppService>().ConstruirAsync(acesso,new()
            {
                Mensagem=Limitar(texto,2000),Filtros=new(){Texto=Termos(texto)},OrcamentoDeTokens=inicial?2048:1024,LimiteDeItens=inicial?12:6,
                JaInjetados=inicial?new Dictionary<string,int>():anterior!.Injetados
            },cancellationToken);
            if(pacote.Itens.Count==0)return null;
            var aviso=mudou?"O escopo do Brain mudou nesta conversa: contexto do Brain enviado antes pertence ao escopo anterior.\n":"";
            return new(idSessao,escopo,$"{pacote.TextoParaInjecao}{aviso}[fim do contexto do Brain]\n\nMensagem do usuário:\n{texto}",pacote,inicial);
        }
        // Sem identidade, escopo ou banco, a conversa segue sem contexto do Brain (falha fechada, nada é injetado).
        catch(Exception ex) when(ex is not OperationCanceledException){return null;}
    }
    public void RegistrarInjecao(ContextoParaTurno contexto)
    {
        var pacote=contexto.Pacote.RegistrarInjecao(contexto.Pacote.Itens.Select(x=>x.Chave).ToArray());
        injecoes.AddOrUpdate(contexto.IdSessao,_=>Nova(null),(_,atual)=>Nova(atual));
        if(injecoes.Count>200)foreach(var antiga in injecoes.OrderBy(x=>x.Value.AtualizadoEm).Take(injecoes.Count-200).ToArray())injecoes.TryRemove(antiga.Key,out _);
        Injecao Nova(Injecao? atual)
        {
            var injetados=new Dictionary<string,int>(atual is not null&&atual.Escopo==contexto.Escopo&&!contexto.Bootstrap?atual.Injetados:new Dictionary<string,int>(),StringComparer.Ordinal);
            if(injetados.Count+pacote.Itens.Count>500)injetados.Clear();
            foreach(var item in pacote.Itens)injetados[item.Chave]=item.Revisao;
            return new(contexto.Escopo,injetados,DateTimeOffset.UtcNow,pacote.Custo.Itens,pacote.Custo.TokensEstimados,contexto.Bootstrap,pacote.Itens.Any(x=>x.Origem=="snapshot"));
        }
    }
    public void ReiniciarSessao(string idSessao)=>injecoes.TryRemove(idSessao,out _);
    public string? EscopoSelecionado(TelegramMessage mensagem)=>Escopo(SelecaoDe(mensagem));
    public string? DescreverStatus(TelegramMessage mensagem,string? idSessao)
    {
        if(!Configurado||mensagem.From is null)return null;
        var selecao=SelecaoDe(mensagem);var escopo=Escopo(selecao);
        var linha=selecao?.Espaco is null?"Brain: nenhum espaço selecionado; conversas seguem sem contexto do Brain.":
            $"Brain: espaço {ProtecaoDeSegredos.Redigir(selecao.NomeEspaco??"selecionado")}, {(selecao.Projeto is null?"sem projeto":$"projeto {ProtecaoDeSegredos.Redigir(selecao.NomeProjeto??"selecionado")}")}.";
        if(idSessao is null)return linha;
        if(!injecoes.TryGetValue(idSessao,out var injecao))return linha+$"\nSessão {idSessao}: nenhum contexto do Brain enviado nesta conversa.";
        return linha+$"\nSessão {idSessao}: {injecao.Injetados.Count} item(ns) do Brain já na conversa; último envio {(injecao.Bootstrap?"inicial":"de atualização")} com {injecao.Itens} item(ns), "+
            $"~{injecao.Tokens} tokens estimados{(injecao.Snapshot?", com o contexto de trabalho":"")}"+(injecao.Escopo==escopo?".":"; o escopo mudou e a próxima mensagem leva o contexto do novo escopo.");
    }
    private Selecao? SelecaoDe(TelegramMessage mensagem)
    {
        if(!Configurado||mensagem.From is null)return null;
        try
        {
            using var scope=scopeFactory.CreateScope();var identidade=scope.ServiceProvider.GetRequiredService<IdentidadeTelegramDoBrain>().Resolver(mensagem.From.Id);
            return selecoes.GetValueOrDefault((identidade.IdTenant,identidade.IdUsuario,mensagem.Chat.Id,mensagem.MessageThreadId??0));
        }
        catch(Exception ex) when(ex is not OperationCanceledException){return null;}
    }
    private static string? Escopo(Selecao? selecao)=>selecao?.Espaco is { } espaco?$"{espaco:D}/{selecao.Projeto?.ToString("D")??"-"}":null;
    private static string Limitar(string texto,int limite)=>texto.Length<=limite?texto:texto[..(char.IsHighSurrogate(texto[limite-1])?limite-1:limite)];
    // Termos significativos unidos por "or": a mensagem inteira exigiria todas as palavras na busca lexical.
    private static readonly HashSet<string> Vazias=new(StringComparer.Ordinal){"para","como","com","sem","sobre","isso","isto","esse","essa","este","esta","aquele","aquela",
        "uma","umas","uns","que","qual","quais","quando","onde","porque","por","pelo","pela","mais","menos","muito","pode","podemos","preciso","precisamos","vamos",
        "fazer","faca","agora","ainda","tambem","entao","mas","nos","nas","dos","das","voce","the","and","for","with"};
    private static string Termos(string texto)=>string.Join(" or ",Regex.Matches(texto,@"[\p{L}\p{N}][\p{L}\p{N}_.\-]{2,63}",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100))
        .Select(x=>x.Value.TrimEnd('.','-').ToLowerInvariant()).Where(x=>x.Length>=3&&!Vazias.Contains(ResolvedorDeIntencaoDoBrain.Normalizar(x))).Distinct(StringComparer.Ordinal).Take(16));
    private static string IdDaConversa(TelegramMessage mensagem)=>$"{mensagem.Chat.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{(mensagem.MessageThreadId??0).ToString(System.Globalization.CultureInfo.InvariantCulture)}";

}
