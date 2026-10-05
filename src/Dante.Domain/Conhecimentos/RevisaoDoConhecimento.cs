namespace Dante.Domain.Conhecimentos;

// Snapshot imutável da versão canônica e do ato que a produziu; correção não apaga a evidência anterior.
public sealed record RevisaoDoConhecimento(
    int Numero, TipoDeConhecimento Tipo, string? Conteudo, string? DadosEstruturados,
    StatusDoConhecimento Status, double? Confianca, Sensibilidade Sensibilidade,
    DateTimeOffset? ValidoDesde, DateTimeOffset? ValidoAte, IReadOnlyList<string> Tags,
    ProvenienciaDoConhecimento Proveniencia, DateTimeOffset RegistradaEm, Guid? IdConhecimentoSubstituto);
