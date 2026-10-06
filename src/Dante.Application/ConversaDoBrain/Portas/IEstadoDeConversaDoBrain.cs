using Dante.Application.SegurancaDoBrain;
namespace Dante.Application.ConversaDoBrain;

public interface IEstadoDeConversaDoBrain
{
    EstadoDeConversaDto Obter(ChaveDeConversaDto chave);
    EstadoDeConversaDto Salvar(ChaveDeConversaDto chave,EstadoDeConversaDto estado);
    bool ConsumirPendente(ChaveDeConversaDto chave,Guid versao);
    void LimparConversa(IdentidadeDoBrain identidade,string idConversa);
}
