using System.Text;
using Dante.Domain.Conhecimentos;
using Dante.Domain.RelacoesDeConhecimento;

namespace Dante.Application.ConstrucaoDeContexto;

internal static class SelecaoDeContexto
{
    internal static int EstimarTokens(string texto)=>(Encoding.UTF8.GetByteCount(texto)+2)/3;
    // Papéis que a continuidade exige: se a frase é igual a outro item, o papel fica como ponteiro para ele.
    private static bool Essencial(string campo)=>campo is "Objetivo" or "Pendencias" or "ProximosPassos";
    internal static PacoteDeContextoDto Selecionar(PedidoDeContextoDto pedido, IReadOnlyList<ItemDeContextoDto> candidatos,
        IReadOnlyList<CampoDoSnapshot> camposDoSnapshot, IReadOnlyList<RegistroDeContextoDto> registrosAnteriores, bool limitou,
        IReadOnlyList<RelacaoEntreItens>? relacoes=null, IReadOnlyDictionary<string,IReadOnlyList<string>>? consolidacoes=null)
    {
        var registros=registrosAnteriores.ToList();var inicioDosNovos=registros.Count;
        var presentes=pedido.FragmentosJaPresentes.Append(pedido.Mensagem).Select(Normalizar).ToArray();
        var selecionados=new List<ItemDeContextoDto>();var tokens=EstimarTokens(FormatoDeContexto.Cabecalho(null));
        var normalizados=new HashSet<string>(StringComparer.Ordinal);
        foreach(var original in candidatos.OrderBy(Prioridade).ThenByDescending(x=>x.Relevancia).ThenBy(x=>x.Chave,StringComparer.Ordinal))
        {
            var candidato=original;
            if(candidato.Origem=="snapshot")
            {
                var campos=CamposRestantes(camposDoSnapshot,selecionados);
                candidato=candidato with{Conteudo=string.Join("\n",campos.Select(c=>c.Texto)),CamposDoSnapshot=campos};
            }
            // Estimativa conservadora: sensibilidade por item, sem omissão de fonte; o pós-processamento só reduz ou verifica.
            var item=candidato with{TokensEstimados=EstimarTokens(FormatoDeContexto.Formatar(candidato,true))};
            var normal=Normalizar(item.Conteudo);string? descarte=null;
            // O snapshot já foi reduzido campo a campo por igualdade; substring aqui descartaria pendências e próximos
            // passos contidos em frases de sentido oposto. Ele só sai inteiro se for igual ao que já está presente.
            var snapshot=item.Origem=="snapshot";
            if(pedido.JaInjetados.TryGetValue(item.Chave,out var revisaoInjetada) && revisaoInjetada==item.Revisao)descarte="Já injetado nesta conversa upstream, na mesma revisão.";
            else if(string.IsNullOrWhiteSpace(normal)||normalizados.Contains(normal)||(snapshot?presentes.Contains(normal):Coberto(item.Conteudo,presentes)) ||
                !snapshot && selecionados.Any(x=>Normalizar(x.Conteudo).Contains(normal,StringComparison.Ordinal)))descarte="Conteúdo já presente no prompt/resumo/snapshot ou em outro item.";
            else if(item.InicioDaFonte is { } inicio && selecionados.Any(x=>x.Origem=="fonte_bruta" && x.Id==item.Id && x.Revisao==item.Revisao &&
                x.InicioDaFonte<inicio+item.Conteudo.EnumerateRunes().Count() && inicio<x.InicioDaFonte+x.Conteudo.EnumerateRunes().Count()))
                descarte="Trecho sobreposto a fonte já selecionada.";
            else if(selecionados.Count>=pedido.LimiteDeItens)descarte="Limite de itens.";
            else if(tokens+item.TokensEstimados>pedido.OrcamentoDeTokens)descarte="Descartado pelo orçamento de tokens estimados.";
            if(descarte is not null){registros.Add(new(item.Chave,item.Id,item.Origem,"descartado",descarte,item.TokensEstimados));continue;}
            selecionados.Add(item);normalizados.Add(normal);tokens+=item.TokensEstimados;
            registros.Add(new(item.Chave,item.Id,item.Origem,"selecionado",item.Motivo,item.TokensEstimados));
        }
        if(selecionados.Count==0)return new(Guid.NewGuid(),"",[],registros.ToArray(),new(0,0,0,0,pedido.OrcamentoDeTokens),limitou);
        var chaves=selecionados.Select(x=>x.Chave).ToHashSet(StringComparer.Ordinal);
        // Fonte bruta só deixa de ser repetida quando cada trecho dela está num conhecimento selecionado no mesmo pacote.
        var identidades=selecionados.ToDictionary(x=>x.Chave,FormatoDeContexto.Identidade,StringComparer.Ordinal);
        for(var i=0;i<selecionados.Count;i++)
            if(selecionados[i].Origem=="fonte_bruta" && consolidacoes?.GetValueOrDefault(selecionados[i].Chave) is { Count: > 0 } cobertura && cobertura.All(chaves.Contains))
                selecionados[i]=selecionados[i] with{ConsolidadaEm=cobertura.Select(c=>identidades[c]).Distinct(StringComparer.Ordinal).ToArray()};
        var uniforme=selecionados.Count>1 && selecionados.All(x=>x.Sensibilidade==selecionados[0].Sensibilidade)?selecionados[0].Sensibilidade:(Sensibilidade?)null;
        if(uniforme is not null && Texto(selecionados,uniforme).Length>=Texto(selecionados,null).Length)uniforme=null;
        // Relações entre itens do próprio pacote, com IDs e revisões verificáveis; entram só se couberem no orçamento.
        var porId=selecionados.Select((x,i)=>(x,i)).Where(p=>p.x.Origem=="conhecimento").GroupBy(p=>p.x.Id).ToDictionary(g=>g.Key,g=>g.First().i);
        foreach(var r in (relacoes??[]).Where(r=>r.IdOrigem!=r.IdDestino).DistinctBy(r=>(r.IdOrigem,r.IdDestino,r.Tipo)).OrderBy(r=>r.IdOrigem).ThenBy(r=>r.IdDestino).ThenBy(r=>r.Tipo))
        {
            if(!porId.TryGetValue(r.IdOrigem,out var o)||!porId.TryGetValue(r.IdDestino,out var d))continue;
            var origem=selecionados[o];var anterior=origem;
            selecionados[o]=origem with{Relacoes=[..origem.Relacoes??[],$"{r.Tipo} {FormatoDeContexto.Identidade(selecionados[d])}"]};
            if(EstimarTokens(Texto(selecionados,uniforme))>pedido.OrcamentoDeTokens)selecionados[o]=anterior;
        }
        var finais=selecionados.Select(x=>x with{TokensEstimados=EstimarTokens(FormatoDeContexto.Formatar(x,uniforme is null))}).ToArray();
        var porChave=finais.ToDictionary(x=>x.Chave,StringComparer.Ordinal);
        for(var i=inicioDosNovos;i<registros.Count;i++)
            if(registros[i].Estado=="selecionado" && porChave.TryGetValue(registros[i].Chave,out var f))registros[i]=registros[i] with{TokensEstimados=f.TokensEstimados};
        var textoParaInjecao=Texto(finais,uniforme);
        return new(Guid.NewGuid(),textoParaInjecao,finais,registros.ToArray(),new(EstimarTokens(textoParaInjecao),finais.Length,textoParaInjecao.Length,Encoding.UTF8.GetByteCount(textoParaInjecao),pedido.OrcamentoDeTokens),limitou);
    }
    private static string Texto(IReadOnlyList<ItemDeContextoDto> itens,Sensibilidade? uniforme)=>
        FormatoDeContexto.Cabecalho(uniforme)+string.Concat(itens.Select(x=>FormatoDeContexto.Formatar(x,uniforme is null)));
    // Só igualdade após Normalizar prova equivalência: substring não prova ("habilitar X" está contido em
    // "Não habilitar X"). Campo igual a um item selecionado não se repete; papel essencial vira ponteiro verificável
    // para esse item, para o agente saber que aquele texto é o objetivo/pendência/próximo passo. Entrada repetida
    // igual na mesma lista sai; entradas distintas ficam todas, mesmo que uma esteja contida na outra.
    private static CampoDoSnapshot[] CamposRestantes(IReadOnlyList<CampoDoSnapshot> campos,IReadOnlyList<ItemDeContextoDto> selecionados)
    {
        var textos=selecionados.Select(x=>(Item:x,Texto:Normalizar(x.Conteudo))).ToArray();
        var restantes=new List<CampoDoSnapshot>();var vistos=new HashSet<(string,string)>();
        foreach(var c in campos)
        {
            var n=Normalizar(c.Texto);
            if(!vistos.Add((c.Campo,n)))continue;
            var igual=textos.FirstOrDefault(t=>t.Texto==n).Item;
            if(igual is null)restantes.Add(c);
            else if(Essencial(c.Campo))restantes.Add(c with{Texto=$"[contido em {FormatoDeContexto.Identidade(igual)}]"});
        }
        return restantes.ToArray();
    }
    private static int Prioridade(ItemDeContextoDto x)=>x.Status==StatusDoConhecimento.Confirmado && x.Tipo==TipoDeConhecimento.Instrucao?0:
        x.Status==StatusDoConhecimento.Confirmado && x.Tipo==TipoDeConhecimento.Decisao?1:x.Status==StatusDoConhecimento.Confirmado?2:
        x.Referenciado?3:x.Origem=="snapshot"?4:x.Origem=="conhecimento"?5:6;
    internal static string Normalizar(string texto)=>string.Join(' ',texto.Normalize(NormalizationForm.FormC).Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries));
    internal static bool Coberto(string conteudo,IReadOnlyList<string> presentes)
    {var texto=Normalizar(conteudo);return presentes.Any(x=>x==texto || texto.Length>=24 && x.Contains(texto,StringComparison.Ordinal));}
}

// Relação registrada no Brain entre dois conhecimentos candidatos ao pacote.
internal sealed record RelacaoEntreItens(Guid IdOrigem, Guid IdDestino, TipoDeRelacao Tipo);
