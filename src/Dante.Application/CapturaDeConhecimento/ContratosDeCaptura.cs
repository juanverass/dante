using Dante.Application.Comum;
using Dante.Application.Conhecimentos;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
namespace Dante.Application.CapturaDeConhecimento;

public sealed record CapturaDeConhecimentoDto
{
    public Guid IdEspacoDeConhecimento { get; init; }
    public Guid? IdProjeto { get; init; }
    public TipoDeConhecimento Tipo { get; init; }
    public string Conteudo { get; init; } = "";
    public Sensibilidade Sensibilidade { get; init; } = Sensibilidade.Pessoal;
    public NaturezaDoConteudo Natureza { get; init; }
    public ModoDeCaptura Modo { get; init; }
    public string Justificativa { get; init; } = "";
    public ProvenienciaDto Proveniencia { get; init; } = new();
    public Guid? IdIncidente { get; init; }
    public Guid? IdSolucao { get; init; }
}
public sealed record AtoDoCandidatoDto(int Revisao, string Acao, TipoDeConhecimento Tipo, string Conteudo,
    Sensibilidade Sensibilidade, string Justificativa, ProvenienciaDto Proveniencia,
    DateTimeOffset Instante, EstadoDoCandidato Estado, Guid? IdConhecimento);
public sealed record CandidatoDeConhecimentoDto(Guid Id, Guid IdEspacoDeConhecimento, Guid? IdProjeto,
    TipoDeConhecimento Tipo, string Conteudo, Sensibilidade Sensibilidade, NaturezaDoConteudo Natureza,
    ModoDeCaptura Modo, EstadoDoCandidato Estado, int Revisao, Guid? IdConhecimento, IReadOnlyList<AtoDoCandidatoDto> Historico);

public interface ICandidatoDeConhecimentoRepository : IRepository<CandidatoDeConhecimento>
{
    Task<CandidatoDeConhecimento?> ObterEquivalenteAsync(CandidatoDeConhecimento candidato, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CandidatoDeConhecimento>> ListarPendentesAsync(Guid idEspaco, Guid? idProjeto, int limite, CancellationToken cancellationToken = default);
}
public interface ICapturaDeConhecimentoAppService
{
    Task<CandidatoDeConhecimentoDto> CapturarAsync(CapturaDeConhecimentoDto captura, CancellationToken cancellationToken = default);
    Task<CandidatoDeConhecimentoDto> CorrigirAsync(Guid idEspaco, Guid? idProjeto, Guid idCandidato, int revisaoEsperada,
        CapturaDeConhecimentoDto correcao, CancellationToken cancellationToken = default);
    Task<Guid> ConfirmarAsync(Guid idEspaco, Guid? idProjeto, Guid idCandidato, int revisaoEsperada,
        ProvenienciaDto responsavel, CancellationToken cancellationToken = default);
    Task RejeitarAsync(Guid idEspaco, Guid? idProjeto, Guid idCandidato, int revisaoEsperada, ProvenienciaDto responsavel, CancellationToken cancellationToken = default);
    Task DescartarAsync(Guid idEspaco, Guid? idProjeto, Guid idCandidato, int revisaoEsperada, ProvenienciaDto responsavel, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CandidatoDeConhecimentoDto>> ListarPendentesAsync(Guid idEspaco, Guid? idProjeto, int limite = 50, CancellationToken cancellationToken = default);
}
