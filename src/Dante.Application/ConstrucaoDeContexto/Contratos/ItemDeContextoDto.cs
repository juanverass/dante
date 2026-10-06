using Dante.Domain.Conhecimentos;
namespace Dante.Application.ConstrucaoDeContexto;

public sealed record ItemDeContextoDto(string Chave, Guid Id, int Revisao, string Origem, TipoDeConhecimento? Tipo,
    StatusDoConhecimento? Status, Sensibilidade Sensibilidade, string Conteudo, string? Referencia,
    string Motivo, double Relevancia, int TokensEstimados, int? InicioDaFonte = null);
