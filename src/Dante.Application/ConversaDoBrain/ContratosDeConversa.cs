using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
namespace Dante.Application.ConversaDoBrain;

public enum IntencaoDoBrain { Nenhuma, Consultar, Experiencia, Capturar, Corrigir, Invalidar, Relacionar, Origem, Confirmar, Cancelar, Escolher, ListarCandidatos, Inspecionar, Exportar, ImportarFonte, CapturarFonte, AtualizarContexto, MostrarContexto, AvaliarRetomada, Metricas }
public sealed record IntencaoResolvidaDto(IntencaoDoBrain Intencao,string Texto="",int? Numero=null,int? SegundoNumero=null,string? Nome=null,string? Formato=null);
// IdSessao: sessão do agente ativa no canal, usada só para associar a avaliação de retomada (#148).
public sealed record PedidoDeConversaDto(string IdConversa,string Texto,string ReferenciaDaMensagem,string? TrechoSelecionado=null,string? ReferenciaDoTrecho=null,string? IdSessao=null);
public sealed record ChaveDeConversaDto(Guid IdTenant,Guid IdUsuario,Guid IdEspaco,Guid? IdProjeto,string IdConversa);
public sealed record AlvoDeConversaDto(Guid Id,int Revisao,string Origem,TipoDeConhecimento? Tipo,StatusDoConhecimento? Status,Sensibilidade Sensibilidade,string Descricao,int? NumeroDaParte=null);
public sealed record AlteracaoPendenteDto(string Acao,AlvoDeConversaDto? Alvo=null,AlvoDeConversaDto? SegundoAlvo=null,string? Conteudo=null,DateTimeOffset? ExpiraEm=null);
public sealed record EstadoDeConversaDto(Guid Versao,IReadOnlyList<AlvoDeConversaDto> Resultados,AlteracaoPendenteDto? Pendente,string? UltimoTermo,DateTimeOffset AtualizadoEm,bool ContextoDeAlteracao=false,bool ConfirmacaoExpirada=false);
public interface IEstadoDeConversaDoBrain
{
    EstadoDeConversaDto Obter(ChaveDeConversaDto chave);
    EstadoDeConversaDto Salvar(ChaveDeConversaDto chave,EstadoDeConversaDto estado);
    bool ConsumirPendente(ChaveDeConversaDto chave,Guid versao);
    void LimparConversa(IdentidadeDoBrain identidade,string idConversa);
}
