namespace Dante.Application.Projetos;

// O espaço é obrigatório: não existe pesquisa de projetos sem escopo (AD-33).
public sealed record ProjetoSearchDto(Guid IdEspacoDeConhecimento, string? TrechoDoNome = null,
    bool IncluirArquivados = false, int Limite = 50);
