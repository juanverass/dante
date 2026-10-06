using Dante.Application.SegurancaDoBrain;
namespace Dante.Application.AuditoriaDoBrain;

public interface IConsultaDeAuditoria
{
    Task<DadosDeAuditoria> ConsultarAsync(AcessoAoBrain acesso, AuditoriaDoBrainSearchDto filtro,
        bool exportacao, CancellationToken cancellationToken = default);
}
