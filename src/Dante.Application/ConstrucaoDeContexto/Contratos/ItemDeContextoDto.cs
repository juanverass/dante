using Dante.Domain.Conhecimentos;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Application.Conhecimentos;
namespace Dante.Application.ConstrucaoDeContexto;

public sealed record ItemDeContextoDto(string Chave, Guid Id, int Revisao, string Origem, TipoDeConhecimento? Tipo,
    StatusDoConhecimento? Status, Sensibilidade Sensibilidade, string Conteudo, string? Referencia,
    string Motivo, double Relevancia, int TokensEstimados, int? InicioDaFonte = null,
    NaturezaDoConteudo? NaturezaDeOrigem = null, NaturezaDoConteudo? Natureza = null, AutoriaDaFonte? AutoriaDaFonte = null,
    bool Referenciado = false, string? Titulo = null, string? ReferenciaDaFonte = null,
    IReadOnlyList<CampoDoSnapshot>? CamposDoSnapshot = null, IReadOnlyList<string>? Relacoes = null,
    IReadOnlyList<string>? ConsolidadaEm = null, string? CorpoSemTitulo = null)
{
    // CorpoSemTitulo (conhecimento): Conteudo sem o JSON de DadosEstruturados, quando ele é só {"titulo"}; o título vai em
    // Titulo. Só muda a exibição: seleção, deduplicação e prova de consolidação continuam sobre Conteudo inteiro.
    // ReferenciaDaFonte: proveniência registrada do conhecimento (prova de consolidação, #147); Referencia é a citada.
    // ConsolidadaEm: fonte cuja íntegra foi omitida porque cada trecho está contido nesses conhecimentos selecionados.
    public string Rotulo => Origem switch
    {
        "snapshot" => "Contexto operacional, não é conhecimento confirmado",
        "candidato" => "Pendente, não confirmado",
        "fonte_bruta" => RotulagemDoConhecimento.RotularFonte(AutoriaDaFonte ?? Dante.Domain.Conhecimentos.AutoriaDaFonte.Desconhecida),
        _ => RotulagemDoConhecimento.Rotular(NaturezaDeOrigem, Natureza, Status)
    };
}
