using Dante.Application.SegurancaDoBrain;
using Dante.Application.Comum;
using Dante.Application.Conhecimentos;
using Dante.Application.EspacosDeConhecimento;
using Dante.Application.Projetos;
using Dante.Application.RelacoesDeConhecimento;
using Dante.Application.Mapeamento;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.RelacoesDeConhecimento;
namespace Dante.Application.CapturaDeConhecimento;

public sealed class CapturaDeConhecimentoAppService(ICandidatoDeConhecimentoRepository candidatos,
    IConhecimentoRepository conhecimentos, IEspacoDeConhecimentoRepository espacos, IProjetoRepository projetos,
    IRelacaoDeConhecimentoRepository relacoes, IUnitOfWork unitOfWork, IMapsterTypeAdapter typeAdapter, AutorizacaoDoBrain? autorizacao = null) : ICapturaDeConhecimentoAppService
{
    public async Task<CandidatoDeConhecimentoDto> CapturarAsync(CapturaDeConhecimentoDto captura, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(captura);
        ProtecaoDeSegredos.GarantirSeguro([captura.Conteudo, captura.Justificativa, captura.Titulo, .. captura.Tags]);
        await GarantirEscopoAsync(captura.IdEspacoDeConhecimento, captura.IdProjeto, true, cancellationToken);
        var candidato = Novo(captura);
        await ValidarConsolidacaoAsync(candidato, cancellationToken);
        var existente = await candidatos.ObterEquivalenteAsync(candidato, cancellationToken);
        if (existente is not null)
        {
            if (existente.Estado == EstadoDoCandidato.Pendente)
            {
                existente.RegistrarOrigemEquivalente(candidato.Proveniencia, DateTimeOffset.UtcNow);
                candidatos.Atualizar(existente); await unitOfWork.SalvarAlteracoesAsync(cancellationToken);
            }
            return ParaDto(existente);
        }
        await candidatos.AdicionarAsync(candidato, cancellationToken);
        await unitOfWork.SalvarAlteracoesAsync(cancellationToken);
        return ParaDto(candidato);
    }

    public async Task<CandidatoDeConhecimentoDto> CorrigirAsync(Guid idEspaco, Guid? idProjeto, Guid idCandidato, int revisaoEsperada,
        CapturaDeConhecimentoDto correcao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correcao);
        ProtecaoDeSegredos.GarantirSeguro([correcao.Conteudo, correcao.Justificativa, correcao.Titulo, .. correcao.Tags]);
        var candidato = await ObterAsync(idEspaco, idProjeto, idCandidato, true, cancellationToken);
        if (correcao.IdEspacoDeConhecimento != idEspaco || correcao.IdProjeto != idProjeto || correcao.Natureza != candidato.Natureza ||
            correcao.IdIncidente != candidato.IdIncidente || correcao.IdSolucao != candidato.IdSolucao)
            throw new ArgumentException("Correção não redefine escopo/origem nem vínculos da captura.");
        // Valida no agregado antes da gravação; conflito UNIQUE/concorrência não faz commit parcial.
        candidato.Corrigir(revisaoEsperada, correcao.Tipo, correcao.Conteudo, correcao.Sensibilidade, correcao.Justificativa,
            ConhecimentoAppService.ParaProveniencia(correcao.Proveniencia), DateTimeOffset.UtcNow, correcao.Titulo, correcao.Tags);
        var equivalente = await candidatos.ObterEquivalenteAsync(candidato, cancellationToken);
        if (equivalente is not null && equivalente.Id != candidato.Id) throw new InvalidOperationException("Já existe candidato equivalente; revise-o antes de corrigir.");
        candidatos.Atualizar(candidato); await unitOfWork.SalvarAlteracoesAsync(cancellationToken);
        return ParaDto(candidato);
    }

    public async Task<Guid> ConfirmarAsync(Guid idEspaco, Guid? idProjeto, Guid idCandidato, int revisaoEsperada,
        ProvenienciaDto responsavel, CancellationToken cancellationToken = default)
    {
        var candidato = await ObterAsync(idEspaco, idProjeto, idCandidato, true, cancellationToken);
        await ValidarConsolidacaoAsync(candidato, cancellationToken);
        var p = ConhecimentoAppService.ParaProveniencia(responsavel); var instante = DateTimeOffset.UtcNow;
        ProtecaoDeSegredos.GarantirSeguro(candidato.Conteudo, candidato.Justificativa);
        ConhecimentoAppService.ParaProveniencia(new ProvenienciaDto { IdResponsavel = candidato.Proveniencia.IdResponsavel, Origem = candidato.Proveniencia.Origem, ReferenciaDaFonte = candidato.Proveniencia.ReferenciaDaFonte, TrechoDaFonte = candidato.Proveniencia.TrechoDaFonte });
        var conhecimento = candidato.Promover(revisaoEsperada, p, instante);
        await conhecimentos.AdicionarAsync(conhecimento, cancellationToken);
        if (candidato.IdIncidente is not null)
        {
            var incidente = (await conhecimentos.ObterPorIdAsync(candidato.IdIncidente.Value, cancellationToken))!;
            var solucao = (await conhecimentos.ObterPorIdAsync(candidato.IdSolucao!.Value, cancellationToken))!;
            var resolvido = new RelacaoDeConhecimento(incidente, solucao, TipoDeRelacao.ResolvidoPor, p, instante);
            if (await relacoes.ObterEquivalenteAsync(resolvido, cancellationToken) is null) await relacoes.AdicionarAsync(resolvido, cancellationToken);
            await relacoes.AdicionarAsync(new RelacaoDeConhecimento(solucao, conhecimento, TipoDeRelacao.ProduziuAprendizado, p, instante), cancellationToken);
        }
        candidatos.Atualizar(candidato);
        await unitOfWork.SalvarAlteracoesAsync(cancellationToken);
        return conhecimento.Id;
    }

    public Task RejeitarAsync(Guid idEspaco, Guid? idProjeto, Guid idCandidato, int revisaoEsperada, ProvenienciaDto responsavel, CancellationToken cancellationToken = default) =>
        EncerrarAsync(idEspaco, idProjeto, idCandidato, revisaoEsperada, responsavel, false, cancellationToken);
    public Task DescartarAsync(Guid idEspaco, Guid? idProjeto, Guid idCandidato, int revisaoEsperada, ProvenienciaDto responsavel, CancellationToken cancellationToken = default) =>
        EncerrarAsync(idEspaco, idProjeto, idCandidato, revisaoEsperada, responsavel, true, cancellationToken);
    private async Task EncerrarAsync(Guid espaco, Guid? projeto, Guid id, int revisao, ProvenienciaDto p, bool descartar, CancellationToken ct)
    {
        var candidato = await ObterAsync(espaco, projeto, id, false, ct);
        if (descartar) candidato.Descartar(revisao, ConhecimentoAppService.ParaProveniencia(p), DateTimeOffset.UtcNow);
        else candidato.Rejeitar(revisao, ConhecimentoAppService.ParaProveniencia(p), DateTimeOffset.UtcNow);
        candidatos.Atualizar(candidato); await unitOfWork.SalvarAlteracoesAsync(ct);
    }
    public async Task<IReadOnlyList<CandidatoDeConhecimentoDto>> ListarPendentesAsync(Guid idEspaco, Guid? idProjeto, int limite = 50, CancellationToken cancellationToken = default, int deslocamento = 0)
    {
        ValidacaoDeEntrada.ExigirFaixa(limite, 1, 100, nameof(limite));
        ValidacaoDeEntrada.ExigirFaixa(deslocamento, 0, 10000, nameof(deslocamento));
        await GarantirEscopoAsync(idEspaco, idProjeto, false, cancellationToken);
        return (await candidatos.ListarPendentesAsync(idEspaco, idProjeto, limite, cancellationToken, deslocamento)).Select(ParaDto).ToArray();
    }
    public async Task<CandidatoDeConhecimentoDto> ObterCandidatoAsync(Guid idEspaco, Guid? idProjeto, Guid id, CancellationToken cancellationToken = default) =>
        ParaDto(await ObterAsync(idEspaco, idProjeto, id, false, cancellationToken));
    private static CandidatoDeConhecimento Novo(CapturaDeConhecimentoDto dto) => new(dto.IdEspacoDeConhecimento, dto.IdProjeto,
        dto.Tipo, dto.Conteudo, dto.Sensibilidade, dto.Natureza, dto.Modo, dto.Justificativa,
        ConhecimentoAppService.ParaProveniencia(dto.Proveniencia), DateTimeOffset.UtcNow, dto.IdIncidente, dto.IdSolucao, dto.Titulo, dto.Tags);
    private CandidatoDeConhecimentoDto ParaDto(CandidatoDeConhecimento x) => SaidaAutorizadaDoBrain.Projetar(typeAdapter.Mapear<CandidatoDeConhecimento, CandidatoDeConhecimentoDto>(x), autorizacao);
    private async Task<CandidatoDeConhecimento> ObterAsync(Guid espaco, Guid? projeto, Guid id, bool gravar, CancellationToken ct)
    {
        await GarantirEscopoAsync(espaco, projeto, gravar, ct);
        var candidato = await candidatos.ObterPorIdAsync(id, ct);
        if (candidato is null || candidato.IdEspacoDeConhecimento != espaco || candidato.IdProjeto != projeto)
            throw new ArgumentException("Candidato não encontrado no escopo.");
        return candidato;
    }
    private async Task GarantirEscopoAsync(Guid espaco, Guid? projeto, bool gravar, CancellationToken ct)
    {
        ValidacaoDeEntrada.ExigirEscopo(espaco, projeto);
        var e = await espacos.ObterPorIdAsync(espaco, ct) ?? throw new ArgumentException("Espaço não encontrado.");
        if (gravar && e.Arquivado) throw new InvalidOperationException("Espaço arquivado é somente leitura.");
        if (projeto is not null)
        {
            var p = await projetos.ObterPorIdAsync(projeto.Value, ct) ?? throw new ArgumentException("Projeto não encontrado.");
            if (p.IdEspacoDeConhecimento != espaco || gravar && p.Arquivado) throw new InvalidOperationException("Projeto inválido ou arquivado.");
        }
    }
    private async Task ValidarConsolidacaoAsync(CandidatoDeConhecimento candidato, CancellationToken ct)
    {
        if (candidato.IdIncidente is null) return;
        foreach (var (id, tipo) in new[] { (candidato.IdIncidente.Value, TipoDeConhecimento.Incidente), (candidato.IdSolucao!.Value, TipoDeConhecimento.Solucao) })
        {
            var item = await conhecimentos.ObterPorIdAsync(id, ct);
            if (item is null || item.IdEspacoDeConhecimento != candidato.IdEspacoDeConhecimento || item.IdProjeto != candidato.IdProjeto ||
                item.Tipo != tipo || item.Status is StatusDoConhecimento.Inativo or StatusDoConhecimento.Substituido)
                throw new ArgumentException("Consolidação exige incidente/solução ativos no mesmo escopo.");
        }
    }
}
