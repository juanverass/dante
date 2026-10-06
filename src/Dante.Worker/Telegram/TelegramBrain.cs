using System.Collections.Concurrent;
using Dante.Application.ConversaDoBrain;
using Dante.Application.EspacosDeConhecimento;
using Dante.Application.Projetos;
using Dante.Application.SegurancaDoBrain;
using Dante.Application.Comum;
using Dante.Infrastructure.SegurancaDoBrain;
using Microsoft.Extensions.Configuration;
namespace Dante.Worker.Telegram;

// Adapter de entrada: resolve identidade/seleção fora do texto enviado aos agentes.
public sealed class TelegramBrain(IServiceScopeFactory scopeFactory,IConfiguration configuration)
{
    private sealed record Selecao(Guid? Espaco=null,Guid? Projeto=null,IReadOnlyList<EspacoDeConhecimentoDto>? Espacos=null,IReadOnlyList<ProjetoDto>? Projetos=null);
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
            if(normal is "ajuda" or "help")return "Brain: diga o que você sabe sobre X?, já resolvemos algo parecido?, documente como resolvemos isso, essa informação está errada ou de onde veio essa informação?. Para selecionar, diga listar espaços / usar espaço Nome / usar projeto Nome / usar sem projeto. Alterações pedem confirmação.";
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
                selecoes[chave]=new(novo.Id);Limpar();return $"Espaço {ProtecaoDeSegredos.Redigir(novo.Nome)} criado e selecionado, sem projeto.";
            }
            if(normal.StartsWith("usar espaco ",StringComparison.Ordinal))
            {
                var nome=texto[(explicito?7:0)..].Trim()[12..].Trim();
                var candidatos=int.TryParse(nome,out var numero)&&estado.Espacos is not null&&numero>=1&&numero<=estado.Espacos.Count?
                    listaEspacos.Where(x=>x.Id==estado.Espacos[numero-1].Id).ToArray():listaEspacos.Where(x=>string.Equals(x.Nome,nome,StringComparison.OrdinalIgnoreCase)).ToArray();
                if(candidatos.Length!=1)return "Espaço não encontrado ou nome ambíguo. Diga listar espaços e escolha um número da lista.";
                selecoes[chave]=new(candidatos[0].Id);Limpar();return $"Espaço {ProtecaoDeSegredos.Redigir(candidatos[0].Nome)} selecionado, sem projeto.";
            }
            if(estado.Espaco is null&&listaEspacos.Count==1){estado=new(listaEspacos[0].Id);selecoes[chave]=estado;}
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
            {selecoes[chave]=estado with{Projeto=null};Limpar();return "Escopo selecionado sem projeto.";}
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
                selecoes[chave]=estado with{Projeto=candidatos[0].Id};Limpar();return $"Projeto {ProtecaoDeSegredos.Redigir(candidatos[0].Nome)} {(criar?"criado e ":"")}selecionado.";
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
    private static string IdDaConversa(TelegramMessage mensagem)=>$"{mensagem.Chat.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{(mensagem.MessageThreadId??0).ToString(System.Globalization.CultureInfo.InvariantCulture)}";

}
