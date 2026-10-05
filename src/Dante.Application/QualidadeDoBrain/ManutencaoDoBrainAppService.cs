using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dante.Application.Comum;
using Dante.Application.Conhecimentos;
using Dante.Application.RelacoesDeConhecimento;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.RelacoesDeConhecimento;
namespace Dante.Application.QualidadeDoBrain;

public sealed class ManutencaoDoBrainAppService(IConsultaDeQualidade consulta, IConhecimentoRepository conhecimentos,
    IRelacaoDeConhecimentoRepository relacoes, LeituraDoBrainAppService leitura, PoliticaDeSensibilidade politica, IUnitOfWork unitOfWork)
{
    public async Task<RelatorioDeQualidadeDto> RevisarAsync(AcessoAoBrain acesso, int deslocamento = 0, int limite = 100,
        DateTimeOffset? confirmarAntesDe = null, CancellationToken cancellationToken = default)
    {
        await leitura.ValidarAcessoAsync(acesso, cancellationToken);
        if (limite is < 1 or > 100 || deslocamento is < 0 or > 10000) throw new ArgumentOutOfRangeException(nameof(limite));
        var lote = await consulta.ListarAsync(acesso, deslocamento, limite + 1, cancellationToken);
        var itens = lote.Take(limite).Where(x => politica.PermiteConteudo(x, acesso, FinalidadeDeLeitura.Leitura)).ToArray();
        var arestas = await consulta.ListarRelacoesAsync(acesso, itens.Select(x => x.Id).ToArray(), 1001, cancellationToken);
        var achados = new List<AchadoDeQualidadeDto>(); var agora = DateTimeOffset.UtcNow;
        foreach (var item in itens)
        {
            if (item.Status is StatusDoConhecimento.Inativo or StatusDoConhecimento.Substituido)
                achados.Add(new(ProblemaDeQualidade.InativoOuSubstituido,item.Id,item.IdConhecimentoSubstituto,"Estado histórico; fora do contexto operacional."));
            else if (!item.EstaValidoEm(agora)) achados.Add(new(ProblemaDeQualidade.ForaDaValidade,item.Id,null,"Fora do intervalo de validade atual."));
            if (!item.Historico.Any(x => x.Proveniencia.ReferenciaDaFonte is not null))
                achados.Add(new(ProblemaDeQualidade.SemFonteReferenciada,item.Id,null,"Sem referência lógica à fonte; requer revisão manual."));
            if (arestas.Count <= 1000 && !arestas.Any(x => x.IdOrigem == item.Id || x.IdDestino == item.Id))
                achados.Add(new(ProblemaDeQualidade.Orfao,item.Id,null,"Sem relações no grafo; indicação de revisão, sem invalidação automática."));
            var confirmacoes = item.Historico.Where((r,i) => r.Status == StatusDoConhecimento.Confirmado && (i == 0 || item.Historico[i-1].Status != StatusDoConhecimento.Confirmado));
            if (confirmarAntesDe is { } corte && confirmacoes.LastOrDefault() is { } ultima && ultima.RegistradaEm < corte)
                achados.Add(new(ProblemaDeQualidade.ConfirmacaoAnteriorAoLimite,item.Id,null,"Última confirmação anterior ao limite solicitado."));
        }
        for (var i=0;i<itens.Length;i++) for (var j=i+1;j<itens.Length;j++)
        {
            var a=itens[i]; var b=itens[j];
            if (!a.EstaValidoEm(agora) || !b.EstaValidoEm(agora) || a.Tipo != b.Tipo) continue;
            if (PossivelContradicao(a,b)) achados.Add(new(ProblemaDeQualidade.PossivelContradicao,a.Id,b.Id,"Valores diferentes para a mesma chave estruturada ou negação textual próxima; exige evidência/decisão."));
            else if (Normalizar(a.Conteudo) == Normalizar(b.Conteudo) && a.Conteudo is not null && a.DadosEstruturados == b.DadosEstruturados || Semelhanca(a.Conteudo,b.Conteudo) >= 0.85)
                achados.Add(new(ProblemaDeQualidade.PossivelDuplicata,a.Id,b.Id,"Conteúdo idêntico/próximo no mesmo tipo/escopo; consolidação somente explícita."));
        }
        var conflitos = arestas.Take(1000).Where(x => x.Tipo == TipoDeRelacao.Contradiz).Select(x =>
            new ConflitoDeConhecimentoDto(x.Id,x.IdOrigem,x.IdDestino,x.IdConhecimentoEscolhido,x.ResolvidaEm)).ToArray();
        foreach(var conflito in conflitos.Where(x => x.ResolvidoEm is null))
            achados.Add(new(ProblemaDeQualidade.ContradicaoExplicita,conflito.IdOrigem,conflito.IdDestino,"Conflito explícito não resolvido; nenhum lado entra automaticamente no contexto."));
        return new(achados.Take(500).ToArray(),conflitos,lote.Count > limite || arestas.Count > 1000 || achados.Count > 500,itens.Length);
    }
    public async Task ConsolidarAsync(AcessoAoBrain acesso, RevisaoEsperadaDto destino, IReadOnlyList<RevisaoEsperadaDto> duplicatas,
        ProvenienciaDto decisao, CancellationToken cancellationToken = default)
    {
        await leitura.ValidarAcessoAsync(acesso,cancellationToken); var p=Evidencia(acesso,decisao);
        if (duplicatas.Count is < 1 or > 20 || duplicatas.Select(x=>x.IdConhecimento).Distinct().Count()!=duplicatas.Count || duplicatas.Any(x=>x.IdConhecimento==destino.IdConhecimento))
            throw new ArgumentException("Consolidação exige duplicatas distintas e limite de 20.");
        var alvo=await ObterAsync(acesso,destino,cancellationToken); var fontes=new List<Conhecimento>();
        foreach(var dto in duplicatas) fontes.Add(await ObterAsync(acesso,dto,cancellationToken));
        if (fontes.Any(x=>x.Sensibilidade>alvo.Sensibilidade || x.Historico.Any(r=>r.Sensibilidade>alvo.Sensibilidade) || x.Status==StatusDoConhecimento.Confirmado && alvo.Status!=StatusDoConhecimento.Confirmado))
            throw new InvalidOperationException("Consolidação não reduz sensibilidade nem substitui confirmação por inferência.");
        foreach(var fonte in fontes) foreach(var revisao in fonte.Historico)
        {
            var origem=revisao.Proveniencia;
            ProtecaoDeSegredos.GarantirSeguro(origem.Origem,origem.ReferenciaDaFonte,origem.RevisaoDaFonte,origem.TrechoDaFonte);
        }
        var atuais=await consulta.ListarRelacoesAsync(acesso,[alvo.Id,.. fontes.Select(x=>x.Id)],1001,cancellationToken);
        if (atuais.Count>1000 || atuais.Any(x=>x.Tipo==TipoDeRelacao.Contradiz && x.ResolvidaEm is null))
            throw new InvalidOperationException("Resolva conflitos explícitos antes de consolidar.");
        var agora=DateTimeOffset.UtcNow; alvo.IncorporarFontesDe(fontes,destino.Revisao,p,agora); conhecimentos.Atualizar(alvo);
        foreach(var fonte in fontes)
        {
            fonte.SubstituirPor(alvo,fonte.Revisao,p,agora); conhecimentos.Atualizar(fonte);
            await relacoes.AdicionarAsync(new(alvo,fonte,TipoDeRelacao.Substitui,p,agora),cancellationToken);
        }
        await unitOfWork.SalvarAlteracoesAsync(cancellationToken);
    }
    public async Task<Guid> MarcarContradicaoAsync(AcessoAoBrain acesso, RevisaoEsperadaDto a, RevisaoEsperadaDto b,
        ProvenienciaDto decisao,CancellationToken cancellationToken=default)
    {
        await leitura.ValidarAcessoAsync(acesso,cancellationToken); var p=Evidencia(acesso,decisao);
        var origem=await ObterAsync(acesso,a,cancellationToken); var destino=await ObterAsync(acesso,b,cancellationToken);
        var relacao=new RelacaoDeConhecimento(origem,destino,TipoDeRelacao.Contradiz,p,DateTimeOffset.UtcNow);
        var existente=await relacoes.ObterEquivalenteAsync(relacao,cancellationToken);
        if(existente is not null) return existente.Id;
        await relacoes.AdicionarAsync(relacao,cancellationToken); await unitOfWork.SalvarAlteracoesAsync(cancellationToken); return relacao.Id;
    }
    public async Task ResolverConflitoAsync(AcessoAoBrain acesso, Guid idRelacao, RevisaoEsperadaDto escolhido,
        RevisaoEsperadaDto descartado,ProvenienciaDto decisao,CancellationToken cancellationToken=default)
    {
        await leitura.ValidarAcessoAsync(acesso,cancellationToken); var p=Evidencia(acesso,decisao);
        var r=await relacoes.ObterPorIdAsync(idRelacao,cancellationToken);
        if(r is null || r.IdEspacoDeConhecimento!=acesso.IdEspacoDeConhecimento || r.IdProjeto!=acesso.IdProjeto || r.Tipo!=TipoDeRelacao.Contradiz || r.ResolvidaEm is not null ||
            escolhido.IdConhecimento==descartado.IdConhecimento || !(new[]{r.IdOrigem,r.IdDestino}).Contains(escolhido.IdConhecimento) || !(new[]{r.IdOrigem,r.IdDestino}).Contains(descartado.IdConhecimento))
            throw new ArgumentException("Conflito não encontrado ou decisão incompatível.");
        var vencedor=await ObterAsync(acesso,escolhido,cancellationToken); var anterior=await ObterAsync(acesso,descartado,cancellationToken,false,true);
        if(vencedor.Status!=StatusDoConhecimento.Confirmado || vencedor.Sensibilidade<anterior.Sensibilidade || anterior.Status==StatusDoConhecimento.Substituido && anterior.IdConhecimentoSubstituto!=vencedor.Id)
            throw new InvalidOperationException("Resolução exige versão confirmada sem reduzir sensibilidade.");
        var agora=DateTimeOffset.UtcNow;
        r.ResolverContradicao(vencedor,p,agora); relacoes.Atualizar(r);
        if(anterior.Status is not (StatusDoConhecimento.Inativo or StatusDoConhecimento.Substituido))
        {
            anterior.SubstituirPor(vencedor,descartado.Revisao,p,agora); conhecimentos.Atualizar(anterior);
            await relacoes.AdicionarAsync(new(vencedor,anterior,TipoDeRelacao.Substitui,p,agora),cancellationToken);
        }
        await unitOfWork.SalvarAlteracoesAsync(cancellationToken);
    }
    public async Task InvalidarAsync(AcessoAoBrain acesso,RevisaoEsperadaDto item,ProvenienciaDto decisao,CancellationToken cancellationToken=default)
    {
        await leitura.ValidarAcessoAsync(acesso,cancellationToken); var p=Evidencia(acesso,decisao);
        var entidade=await ObterAsync(acesso,item,cancellationToken, false); entidade.Invalidar(item.Revisao,p,DateTimeOffset.UtcNow);
        conhecimentos.Atualizar(entidade); await unitOfWork.SalvarAlteracoesAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<LeituraProtegidaDto>> SelecionarParaContextoAsync(AcessoAoBrain acesso,int limite=20,CancellationToken cancellationToken=default)
    {
        await leitura.ValidarAcessoAsync(acesso,cancellationToken); if(limite is <1 or >100)throw new ArgumentOutOfRangeException(nameof(limite));
        var itens=await consulta.ElegiveisParaContextoAsync(acesso,DateTimeOffset.UtcNow,limite,cancellationToken);
        // Ainda não é o Context Pack (#141): somente seleção segura, sem interpretar conflitos como verdade.
        return itens.Where(x=>politica.PermiteConteudo(x,acesso,FinalidadeDeLeitura.ContextoAutomatico))
            .Select(x=>politica.Projetar(x,acesso,FinalidadeDeLeitura.ContextoAutomatico)).ToArray();
    }
    private async Task<Conhecimento> ObterAsync(AcessoAoBrain acesso,RevisaoEsperadaDto dto,CancellationToken ct,bool exigirValidade=true,bool permitirHistorico=false)
    {
        var item=await conhecimentos.ObterPorIdAsync(dto.IdConhecimento,ct);
        if(item is null || item.IdEspacoDeConhecimento!=acesso.IdEspacoDeConhecimento || item.IdProjeto!=acesso.IdProjeto ||
            !politica.PermiteConteudo(item,acesso,FinalidadeDeLeitura.Leitura))throw new UnauthorizedAccessException("Conhecimento não autorizado no escopo.");
        if(item.Revisao!=dto.Revisao || (!permitirHistorico && item.Status is StatusDoConhecimento.Inativo or StatusDoConhecimento.Substituido) || exigirValidade && !item.EstaValidoEm(DateTimeOffset.UtcNow))throw new InvalidOperationException("Conhecimento inválido ou revisão desatualizada.");
        return item;
    }
    private static ProvenienciaDoConhecimento Evidencia(AcessoAoBrain acesso,ProvenienciaDto dto)
    {
        var p=ConhecimentoAppService.ParaProveniencia(dto);
        if(p.IdResponsavel!=acesso.IdUsuario || p.ReferenciaDaFonte is null || p.TrechoDaFonte is null)
            throw new ArgumentException("Decisão exige responsável autenticado, referência e evidência.");
        return p;
    }
    private static string Normalizar(string? texto)=>Regex.Replace((texto??"").Normalize(NormalizationForm.FormC).Trim(),@"\s+"," ",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    private static double Semelhanca(string? a,string? b)
    {
        if(string.IsNullOrWhiteSpace(a)||string.IsNullOrWhiteSpace(b))return 0;
        var x=Palavras(a);var y=Palavras(b); if(x.Count<4||y.Count<4)return 0;
        return (double)x.Intersect(y).Count()/x.Union(y).Count();
    }
    private static HashSet<string> Palavras(string x)=>Regex.Matches(Normalizar(x).ToLowerInvariant(),@"[\p{L}\p{N}_]+",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100))
        .Select(m=>m.Value).ToHashSet();
    private static bool PossivelContradicao(Conhecimento a,Conhecimento b)
    {
        if(a.DadosEstruturados is not null && b.DadosEstruturados is not null)
        {
            using var x=JsonDocument.Parse(a.DadosEstruturados);using var y=JsonDocument.Parse(b.DadosEstruturados);
            if(x.RootElement.TryGetProperty("chave",out var chave) && y.RootElement.TryGetProperty("chave",out var outra) && chave.GetRawText()==outra.GetRawText() &&
                x.RootElement.TryGetProperty("valor",out var valor) && y.RootElement.TryGetProperty("valor",out var outroValor) && valor.GetRawText()!=outroValor.GetRawText())return true;
        }
        var px=Palavras(a.Conteudo??"");var py=Palavras(b.Conteudo??"");
        return px.Contains("não")!=py.Contains("não") && Semelhanca(a.Conteudo,b.Conteudo)>=0.65;
    }
}
