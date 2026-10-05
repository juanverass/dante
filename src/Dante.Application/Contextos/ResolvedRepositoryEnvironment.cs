namespace Dante.Application.Contextos;

// Legado movido do Worker na #166: o nome em inglês fica até a migração explícita (AD-38). Saída do caso de uso de
// resolução de contexto; os valores vêm do adapter do catálogo e nunca do Domain.
public sealed record ResolvedRepositoryEnvironment(IReadOnlyDictionary<string, string> Values, bool HasSecrets);
