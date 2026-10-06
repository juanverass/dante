using Dante.Application.ContextosDeTrabalho;
using Dante.Domain.Conhecimentos;
using Dante.Domain.ContextosDeTrabalho;
using static Dante.Application.ConversaDoBrain.ApresentacaoDaConversa;

namespace Dante.Application.ConversaDoBrain;

public sealed class ContextoDeTrabalhoDaConversa(ContextoDeTrabalhoAppService snapshots)
{
    // Snapshot operacional selecionado pelo usuário (#145): campos omitidos são mantidos e listas informadas substituem
    // as anteriores. Não cria Conhecimento nem guarda a conversa.
    internal async Task<string> AtualizarContextoAsync(ContextoDaConversa conversa, string corpo, CancellationToken cancellationToken)
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
        var anterior=await snapshots.RetomarAsync(conversa.Acesso,cancellationToken);var d=anterior?.Dados;
        string Campo(string nome,string? atual)=>campos.TryGetValue(nome,out var valor)?valor:atual??"não informado";
        IReadOnlyList<string> Lista(string nome,IReadOnlyList<string>? atual)=>listas.TryGetValue(nome,out var valor)?valor:atual??[];
        var dados=new DadosDoContexto(Campo("objetivo",d?.Objetivo),Campo("tarefa",d?.Tarefa),Campo("progresso",d?.Progresso),d?.IdsDecisoesConfirmadas??[],
            Lista("referencias",d?.Referencias),Lista("pendencias",d?.Pendencias),Lista("passos",d?.ProximosPassos),Campo("resultado",d?.UltimoResultado));
        var salvo=await snapshots.SubstituirAsync(conversa.Acesso,anterior?.Revisao??0,dados,anterior?.Sensibilidade??Sensibilidade.Pessoal,"conversa explícita",anterior?.ExpiraEm,cancellationToken);
        return "Contexto de trabalho atualizado.\n"+DescreverContexto(salvo);
    }

    internal async Task<string> MostrarContextoAsync(ContextoDaConversa conversa, CancellationToken cancellationToken)
    {
        var contextoAtual=await snapshots.RetomarAsync(conversa.Acesso,cancellationToken);
        return contextoAtual is null?"Nenhum contexto de trabalho ativo neste escopo. "+FormatoDoContexto:DescreverContexto(contextoAtual);
    }
}
