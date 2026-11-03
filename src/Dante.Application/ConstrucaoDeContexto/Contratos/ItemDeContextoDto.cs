using Dante.Domain.Conhecimentos;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Application.Conhecimentos;
namespace Dante.Application.ConstrucaoDeContexto;

public sealed record ItemDeContextoDto(string Chave, Guid Id, int Revisao, string Origem, TipoDeConhecimento? Tipo,
    StatusDoConhecimento? Status, Sensibilidade Sensibilidade, string Conteudo, string? Referencia,
    string Motivo, double Relevancia, int TokensEstimados, int? InicioDaFonte = null,
    NaturezaDoConteudo? NaturezaDeOrigem = null, NaturezaDoConteudo? Natureza = null, AutoriaDaFonte? AutoriaDaFonte = null)
{
    public string Rotulo => Origem switch
    {
        "snapshot" => "Contexto operacional, não é conhecimento confirmado",
        "fonte_bruta" => RotulagemDoConhecimento.RotularFonte(AutoriaDaFonte ?? Dante.Domain.Conhecimentos.AutoriaDaFonte.Desconhecida),
        _ => RotulagemDoConhecimento.Rotular(NaturezaDeOrigem, Natureza, Status)
    };
}
