using Dante.Domain.Conhecimentos;

namespace Dante.Application.DocumentosFonte;

// Evidência de teste/validação registrada por agente (#257). Não há campo de autoria, origem ou escopo:
// autoria é sempre Agente, a origem é gerada pelo servidor e o escopo vem do acesso autorizado.
public sealed record RegistroDeEvidenciaDto(string Titulo, string Conteudo, IReadOnlyList<string> Tags,
    Sensibilidade Sensibilidade, string? Justificativa, string Proveniencia);
