namespace Dante.Application.SegurancaDoBrain;

// Permissões explícitas fornecidas pelo adapter autorizado, nunca inferidas do prompt.
public sealed record AcessoAoBrain(Guid IdUsuario, Guid IdEspacoDeConhecimento, Guid? IdProjeto,
    bool PermitirConfidencial = false, bool PermitirSecreto = false);
