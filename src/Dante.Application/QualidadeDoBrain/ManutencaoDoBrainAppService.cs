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
        // As duas faixas reportam "limite", como antes da extração (#205).
        ValidacaoDeEntrada.ExigirFaixa(limite, 1, 100, nameof(limite));
        ValidacaoDeEntrada.ExigirFaixa(deslocamento, 0, 10000, nameof(limite));
        var lote = await consulta.ListarAsync(acesso, deslocamento, limite + 1, cancellationToken);
        var itens = lote.Take(limite).Where(x => politica.PermiteConteudo(x, acesso, FinalidadeDeLeitura.Leitura)).ToArray();
        var arestas = await consulta.ListarRelacoesAsync(acesso, itens.Select(x => x.Id).ToArray(), 1001, cancellationToken);
        return AnaliseDeQualidade.Analisar(itens, arestas, DateTimeOffset.UtcNow, confirmarAntesDe, lote.Count > limite);
    }
    public async Task ConsolidarAsync(AcessoAoBrain acesso, RevisaoEsperadaDto destino, IReadOnlyList<RevisaoEsperadaDto> duplicatas,
        ProvenienciaDto decisao, CancellationToken cancellationToken = default)
    {
        await leitura.ValidarAcessoAsync(acesso,cancellationToken); var p=Evidencia(acesso,decisao);
        ManutencaoDoBrainValidator.ValidarConsolidacao(destino, duplicatas);
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
        await leitura.ValidarAcessoAsync(acesso,cancellationToken); ValidacaoDeEntrada.ExigirFaixa(limite,1,100,nameof(limite));
        var itens=await consulta.ElegiveisParaContextoAsync(acesso,DateTimeOffset.UtcNow,limite,cancellationToken);
        // Ainda não é o Context Pack (#140): somente seleção segura, sem interpretar conflitos como verdade.
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
}
