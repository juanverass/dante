namespace Dante.Application.SegurancaDoBrain;

// Uma instância por operação do adapter. Nunca aceitar identidade/permissões vindas do texto do usuário.
public sealed class AutorizacaoDoBrain
{
    public static readonly Guid TenantLocal = Guid.Parse("da17e000-0000-0000-0000-000000000001");
    public IdentidadeDoBrain? Identidade { get; private set; }
    public AcessoAoBrain? Escopo { get; private set; }
    public void Estabelecer(IdentidadeDoBrain identidade, AcessoAoBrain? escopo = null)
    {
        if (Identidade is not null) throw new InvalidOperationException("Identidade já estabelecida nesta operação.");
        if (identidade.IdTenant == Guid.Empty || identidade.IdUsuario == Guid.Empty ||
            escopo is not null && (escopo.IdUsuario != identidade.IdUsuario || escopo.IdEspacoDeConhecimento == Guid.Empty))
            throw new UnauthorizedAccessException("Identidade ou escopo inválido.");
        Identidade = identidade; Escopo = escopo;
    }
    public void Exigir(AcessoAoBrain acesso)
    {
        if (Identidade is null || Escopo is null || acesso.IdUsuario != Identidade.IdUsuario ||
            acesso.IdEspacoDeConhecimento != Escopo.IdEspacoDeConhecimento || acesso.IdProjeto != Escopo.IdProjeto ||
            acesso.PermitirConfidencial && !Escopo.PermitirConfidencial || acesso.PermitirSecreto && !Escopo.PermitirSecreto)
            throw new UnauthorizedAccessException("Acesso ao Brain não autorizado.");
    }
}
