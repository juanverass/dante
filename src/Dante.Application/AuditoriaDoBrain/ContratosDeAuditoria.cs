using Dante.Application.Conhecimentos;
using Dante.Application.EspacosDeConhecimento;
using Dante.Application.Projetos;
using Dante.Application.RelacoesDeConhecimento;
using Dante.Application.DocumentosFonte;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;
using Dante.Domain.RelacoesDeConhecimento;
using Dante.Domain.DocumentosFonte;
namespace Dante.Application.AuditoriaDoBrain;

public sealed record AuditoriaDoBrainSearchDto
{
    public TipoDeConhecimento? Tipo { get; init; }
    public StatusDoConhecimento? Status { get; init; }
    public string? Origem { get; init; }
    public string? Tag { get; init; }
    public bool IncluirInativos { get; init; }
    public int Limite { get; init; } = 100;
    public int Deslocamento { get; init; }
}
public sealed record DadosDeAuditoria(EspacoDeConhecimento Espaco, IReadOnlyList<Projeto> Projetos,
    IReadOnlyList<Conhecimento> Conhecimentos, IReadOnlyList<RelacaoDeConhecimento> Relacoes,
    IReadOnlyList<DocumentoFonte> Fontes, bool TemMais);
public interface IConsultaDeAuditoria
{
    Task<DadosDeAuditoria> ConsultarAsync(AcessoAoBrain acesso, AuditoriaDoBrainSearchDto filtro,
        bool exportacao, CancellationToken cancellationToken = default);
}
public sealed record FonteAuditadaDto(DocumentoFonteDto Documento, string Conteudo);
public sealed record AuditoriaDoBrainDto(int VersaoFormato, EspacoDeConhecimentoDto Espaco, IReadOnlyList<ProjetoDto> Projetos,
    IReadOnlyList<ConhecimentoDto> Conhecimentos, IReadOnlyList<RelacaoDeConhecimentoDto> Relacoes,
    IReadOnlyList<FonteAuditadaDto> Fontes, bool TemMais);
public sealed record ExportacaoDoBrainDto(string Markdown, string Json);
