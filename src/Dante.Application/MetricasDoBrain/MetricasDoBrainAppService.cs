using Dante.Application.SegurancaDoBrain;
namespace Dante.Application.MetricasDoBrain;

// Compara o contexto enviado pelo Brain numa retomada com o histórico bruto das sessões anteriores do mesmo escopo
// (#148). Limiares explícitos e fixos: a economia é hipótese a validar, não critério ajustável pelo resultado.
public sealed class MetricasDoBrainAppService(IRegistroDeMetricasDoBrain registro, LeituraDoBrainAppService leitura, AutorizacaoDoBrain autorizacao)
{
    public const double LimiteDeGanho=0.5, LimiteDeNeutralidade=1.0;
    public async Task<string> AvaliarAsync(AcessoAoBrain acesso,string? idSessao,MetricaDoBrainDto avaliacao,CancellationToken cancellationToken=default)
    {
        await leitura.ValidarAcessoAsync(acesso,cancellationToken);var identidade=autorizacao.Identidade!;
        if(new[]{avaliacao.Repeticoes,avaliacao.Esclarecimentos,avaliacao.Incorretos,avaliacao.Irrelevantes,avaliacao.Relevantes}.Any(x=>x is < 0 or > 1000))
            throw new ArgumentException("Avaliação inválida.");
        var metricas=await registro.ListarAsync(identidade.IdTenant,acesso.IdUsuario,acesso.IdEspacoDeConhecimento,acesso.IdProjeto,cancellationToken);
        var sessao=idSessao is not null&&metricas.Any(x=>x.IdSessao==idSessao)?idSessao:
            metricas.Where(x=>x.Tipo==MetricaDoBrainDto.Envio).OrderBy(x=>x.Em).LastOrDefault()?.IdSessao;
        if(sessao is null)throw new InvalidOperationException("Nenhuma sessão com Brain neste escopo.");
        var agente=metricas.LastOrDefault(x=>x.IdSessao==sessao&&x.Agente is not null)?.Agente;
        await registro.RegistrarAsync(avaliacao with{Tipo=MetricaDoBrainDto.Avaliacao,Em=DateTimeOffset.UtcNow,IdTenant=identidade.IdTenant,IdUsuario=acesso.IdUsuario,
            IdEspacoDeConhecimento=acesso.IdEspacoDeConhecimento,IdProjeto=acesso.IdProjeto,IdSessao=sessao,Agente=agente},cancellationToken);
        return sessao;
    }
    public async Task<ResumoDeMetricasDto> ResumirAsync(AcessoAoBrain acesso,CancellationToken cancellationToken=default)
    {
        await leitura.ValidarAcessoAsync(acesso,cancellationToken);var identidade=autorizacao.Identidade!;
        return Resumir(await registro.ListarAsync(identidade.IdTenant,acesso.IdUsuario,acesso.IdEspacoDeConhecimento,acesso.IdProjeto,cancellationToken));
    }
    public static ResumoDeMetricasDto Resumir(IReadOnlyList<MetricaDoBrainDto> registradas) => AgregacaoDeMetricas.Resumir(registradas);
    public static string Formatar(ResumoDeMetricasDto resumo) => ApresentacaoDeMetricas.Formatar(resumo);
}
