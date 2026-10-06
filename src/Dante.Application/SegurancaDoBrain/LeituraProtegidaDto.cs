using Dante.Domain.Conhecimentos;
namespace Dante.Application.SegurancaDoBrain;

public sealed record LeituraProtegidaDto(Guid Id, Guid IdEspacoDeConhecimento, Guid? IdProjeto,
    TipoDeConhecimento Tipo, StatusDoConhecimento Status, Sensibilidade Sensibilidade, int Revisao,
    string? Conteudo, string? DadosEstruturados, IReadOnlyList<string> Tags, string? Origem,
    string? ReferenciaDaFonte, bool ConteudoProtegido);
