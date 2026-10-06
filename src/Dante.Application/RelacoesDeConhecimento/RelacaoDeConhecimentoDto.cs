using Dante.Application.Conhecimentos;
using Dante.Domain.RelacoesDeConhecimento;
namespace Dante.Application.RelacoesDeConhecimento;

public sealed record RelacaoDeConhecimentoDto(Guid Id, Guid IdOrigem, Guid IdDestino, TipoDeRelacao Tipo,
    ProvenienciaDto Proveniencia, DateTimeOffset CriadaEm, Guid? IdConhecimentoEscolhido = null,
    DateTimeOffset? ResolvidaEm = null, ProvenienciaDto? ProvenienciaDaResolucao = null);
