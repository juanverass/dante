namespace Dante.Application.ConversaDoBrain;

public sealed record ChaveDeConversaDto(Guid IdTenant,Guid IdUsuario,Guid IdEspaco,Guid? IdProjeto,string IdConversa);
