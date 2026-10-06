using Dante.Domain.Conhecimentos;
namespace Dante.Application.ConversaDoBrain;

public sealed record AlvoDeConversaDto(Guid Id,int Revisao,string Origem,TipoDeConhecimento? Tipo,StatusDoConhecimento? Status,Sensibilidade Sensibilidade,string Descricao,int? NumeroDaParte=null);
