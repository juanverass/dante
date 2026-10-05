using Dante.Application.Comum;
using Dante.Application.EspacosDeConhecimento;
using Dante.Application.Mapeamento;
using Dante.Application.Projetos;
using Dante.Domain.Conhecimentos;

namespace Dante.Application.Conhecimentos;

public sealed class ConhecimentoAppService : CrudBasicoAppService<ConhecimentoDto, ConhecimentoSearchDto, Conhecimento>,
    IConhecimentoAppService
{
    public const int LimiteMaximoDaPesquisa = 100;
    private readonly IConhecimentoRepository conhecimentos;
    private readonly IEspacoDeConhecimentoRepository espacos;
    private readonly IProjetoRepository projetos;

    public ConhecimentoAppService(IConhecimentoRepository conhecimentos, IEspacoDeConhecimentoRepository espacos,
        IProjetoRepository projetos, IUnitOfWork unitOfWork, IMapsterTypeAdapter typeAdapter)
        : base(conhecimentos, unitOfWork, typeAdapter)
    {
        ArgumentNullException.ThrowIfNull(espacos);
        ArgumentNullException.ThrowIfNull(projetos);
        this.conhecimentos = conhecimentos;
        this.espacos = espacos;
        this.projetos = projetos;
    }

    public override async Task<ConhecimentoDto> AdicionarAsync(ConhecimentoDto dto, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        await GarantirEscopoGravavelAsync(dto.IdEspacoDeConhecimento, dto.IdProjeto, cancellationToken);
        return await base.AdicionarAsync(dto, cancellationToken);
    }

    // Atualização CRUD tem exatamente a semântica auditável de correção; identidade/escopo/status do DTO não são copiados.
    public override async Task<ConhecimentoDto?> AtualizarAsync(Guid id, ConhecimentoDto dto,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var entidade = await conhecimentos.ObterPorIdAsync(id, cancellationToken);
        if (entidade is null) return null;
        await GarantirEscopoGravavelAsync(entidade.IdEspacoDeConhecimento, entidade.IdProjeto, cancellationToken);
        AplicarAlteracoes(entidade, dto);
        conhecimentos.Atualizar(entidade);
        await UnitOfWork.SalvarAlteracoesAsync(cancellationToken);
        return ParaDto(entidade);
    }

    public Task<ConhecimentoDto?> CorrigirAsync(Guid id, ConhecimentoDto dto, CancellationToken cancellationToken = default) =>
        AtualizarAsync(id, dto, cancellationToken);

    public Task<bool> ConfirmarAsync(Guid id, int revisaoEsperada, ProvenienciaDto proveniencia,
        CancellationToken cancellationToken = default) => AlterarAsync(id,
            entidade => entidade.Confirmar(revisaoEsperada, ParaProveniencia(proveniencia), DateTimeOffset.UtcNow), cancellationToken);

    public Task<bool> InvalidarAsync(Guid id, int revisaoEsperada, ProvenienciaDto proveniencia,
        CancellationToken cancellationToken = default) => AlterarAsync(id,
            entidade => entidade.Invalidar(revisaoEsperada, ParaProveniencia(proveniencia), DateTimeOffset.UtcNow), cancellationToken);

    public async Task<bool> SubstituirAsync(Guid id, Guid idSubstituto, int revisaoEsperada, ProvenienciaDto proveniencia,
        CancellationToken cancellationToken = default)
    {
        var entidade = await conhecimentos.ObterPorIdAsync(id, cancellationToken);
        if (entidade is null) return false;
        var substituto = await conhecimentos.ObterPorIdAsync(idSubstituto, cancellationToken) ??
            throw new ArgumentException("Conhecimento substituto não encontrado.", nameof(idSubstituto));
        await GarantirEscopoGravavelAsync(entidade.IdEspacoDeConhecimento, entidade.IdProjeto, cancellationToken);
        entidade.SubstituirPor(substituto, revisaoEsperada, ParaProveniencia(proveniencia), DateTimeOffset.UtcNow);
        conhecimentos.Atualizar(entidade);
        await UnitOfWork.SalvarAlteracoesAsync(cancellationToken);
        return true;
    }

    public override async Task<bool> RemoverAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entidade = await conhecimentos.ObterPorIdAsync(id, cancellationToken);
        if (entidade is null) return false;
        await GarantirEscopoGravavelAsync(entidade.IdEspacoDeConhecimento, entidade.IdProjeto, cancellationToken);
        conhecimentos.Remover(entidade);
        await UnitOfWork.SalvarAlteracoesAsync(cancellationToken);
        return true;
    }

    protected override void AplicarAlteracoes(Conhecimento entidade, ConhecimentoDto dto) => entidade.Corrigir(dto.Revisao,
        dto.Tipo, dto.Conteudo, dto.DadosEstruturados, dto.Confianca, dto.Sensibilidade, dto.ValidoDesde, dto.ValidoAte,
        dto.Tags, ParaProveniencia(dto.Proveniencia), DateTimeOffset.UtcNow);

    protected override Task<IReadOnlyList<Conhecimento>> ConsultarAsync(ConhecimentoSearchDto filtro,
        CancellationToken cancellationToken)
    {
        if (filtro.IdEspacoDeConhecimento == Guid.Empty || filtro.IdProjeto == Guid.Empty ||
            (filtro.SomenteSemProjeto && filtro.IdProjeto is not null)) throw new ArgumentException("Escopo de pesquisa inválido.");
        if ((filtro.Tipo is { } tipo && !Enum.IsDefined(tipo)) || (filtro.Status is { } status && !Enum.IsDefined(status)))
            throw new ArgumentException("Classificação de pesquisa inválida.");
        if (filtro.Limite < 1 || filtro.Limite > LimiteMaximoDaPesquisa) throw new ArgumentOutOfRangeException(nameof(filtro.Limite));
        if (filtro.ValidoEm == default(DateTimeOffset)) throw new ArgumentException("Instante de validade inválido.");
        var tag = string.IsNullOrWhiteSpace(filtro.Tag) ? null : filtro.Tag.Trim();
        if (tag?.Length > 100 || tag?.Any(char.IsControl) == true) throw new ArgumentException("Tag de pesquisa inválida.");
        return conhecimentos.ListarDoEspacoAsync(filtro with { Tag = tag }, cancellationToken);
    }

    internal static ProvenienciaDoConhecimento ParaProveniencia(ProvenienciaDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new(dto.IdResponsavel, dto.Origem, dto.ReferenciaDaFonte, dto.RevisaoDaFonte, dto.TrechoDaFonte);
    }

    private async Task GarantirEscopoGravavelAsync(Guid idEspaco, Guid? idProjeto, CancellationToken cancellationToken)
    {
        var espaco = await espacos.ObterPorIdAsync(idEspaco, cancellationToken) ??
            throw new ArgumentException("Espaço de conhecimento não encontrado.");
        if (espaco.Arquivado) throw new InvalidOperationException("Espaço arquivado é somente leitura.");
        if (idProjeto is not null)
        {
            var projeto = await projetos.ObterPorIdAsync(idProjeto.Value, cancellationToken) ??
                throw new ArgumentException("Projeto não encontrado.");
            if (projeto.IdEspacoDeConhecimento != idEspaco) throw new ArgumentException("Projeto pertence a outro espaço.");
            if (projeto.Arquivado) throw new InvalidOperationException("Projeto arquivado é somente leitura.");
        }
    }

    private async Task<bool> AlterarAsync(Guid id, Action<Conhecimento> alterar, CancellationToken cancellationToken)
    {
        var entidade = await conhecimentos.ObterPorIdAsync(id, cancellationToken);
        if (entidade is null) return false;
        await GarantirEscopoGravavelAsync(entidade.IdEspacoDeConhecimento, entidade.IdProjeto, cancellationToken);
        alterar(entidade);
        conhecimentos.Atualizar(entidade);
        await UnitOfWork.SalvarAlteracoesAsync(cancellationToken);
        return true;
    }
}
