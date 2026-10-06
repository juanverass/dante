namespace Dante.Application.ConversaDoBrain;

public sealed record IntencaoResolvidaDto(IntencaoDoBrain Intencao,string Texto="",int? Numero=null,int? SegundoNumero=null,string? Nome=null,string? Formato=null);
