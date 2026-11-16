namespace Dante.Application.Agentes;

public sealed record NovoAgenteDto(string Handle, ConfiguracaoDoAgenteDto Configuracao, long IdDoAutor,
    string? Nome = null, string? Descricao = null);
