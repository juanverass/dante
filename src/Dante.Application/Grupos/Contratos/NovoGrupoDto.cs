namespace Dante.Application.Grupos;

public sealed record NovoGrupoDto(string Nome, ConfiguracaoDoGrupoDto Configuracao, long IdDoAutor,
    string? Descricao = null);
