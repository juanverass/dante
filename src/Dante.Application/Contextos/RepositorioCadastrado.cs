namespace Dante.Application.Contextos;

// O que a resolução de contexto precisa de um repositório cadastrado; GitHub e bindings de ambiente ficam no adapter.
public sealed record RepositorioCadastrado(string Alias, string Caminho);
