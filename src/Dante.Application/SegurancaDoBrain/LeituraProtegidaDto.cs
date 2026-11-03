using Dante.Domain.Conhecimentos;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Application.Conhecimentos;
namespace Dante.Application.SegurancaDoBrain;

public sealed record LeituraProtegidaDto(Guid Id, Guid IdEspacoDeConhecimento, Guid? IdProjeto,
    TipoDeConhecimento Tipo, StatusDoConhecimento Status, Sensibilidade Sensibilidade, int Revisao,
    string? Conteudo, string? DadosEstruturados, IReadOnlyList<string> Tags, string? Origem,
    string? ReferenciaDaFonte, bool ConteudoProtegido, NaturezaDoConteudo? NaturezaDeOrigem = null,
    NaturezaDoConteudo? Natureza = null, AutoriaDaFonte? AutoriaDaFonte = null)
{
    public string Rotulo => AutoriaDaFonte is { } autoria ? RotulagemDoConhecimento.RotularFonte(autoria) :
        RotulagemDoConhecimento.Rotular(NaturezaDeOrigem, Natureza, Status);
}
