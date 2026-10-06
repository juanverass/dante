using Dante.Application.Conhecimentos;
using Dante.Application.EspacosDeConhecimento;
using Dante.Application.Projetos;
using Dante.Application.RelacoesDeConhecimento;
namespace Dante.Application.AuditoriaDoBrain;

public sealed record AuditoriaDoBrainDto(int VersaoFormato, EspacoDeConhecimentoDto Espaco, IReadOnlyList<ProjetoDto> Projetos,
    IReadOnlyList<ConhecimentoDto> Conhecimentos, IReadOnlyList<RelacaoDeConhecimentoDto> Relacoes,
    IReadOnlyList<FonteAuditadaDto> Fontes, bool TemMais);
