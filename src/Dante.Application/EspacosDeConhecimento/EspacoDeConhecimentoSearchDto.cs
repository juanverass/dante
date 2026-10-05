namespace Dante.Application.EspacosDeConhecimento;

// O proprietário é obrigatório: não existe pesquisa de espaços sem escopo (AD-33).
public sealed record EspacoDeConhecimentoSearchDto(Guid IdUsuario, string? TrechoDoNome = null,
    bool IncluirArquivados = false, int Limite = 50);
