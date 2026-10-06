using Dante.Application.Comum;
using Dante.Application.Conhecimentos;
using Dante.Application.Mapeamento;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.ContextosDeTrabalho;
namespace Dante.Application.ContextosDeTrabalho;

public interface IContextoDeTrabalhoRepository : IRepository<ContextoDeTrabalho>
{
    Task<ContextoDeTrabalho?> ObterDoEscopoAsync(Guid idEspaco, Guid? idProjeto, CancellationToken cancellationToken = default);
}
public sealed record ContextoDeTrabalhoDto(Guid Id, Guid IdEspacoDeConhecimento, Guid? IdProjeto, DadosDoContexto Dados,
    Sensibilidade Sensibilidade, int Revisao, Guid IdResponsavel, string Origem, DateTimeOffset AtualizadoEm,
    DateTimeOffset? ExpiraEm, AuditoriaDoContexto? AuditoriaAnterior);
public sealed class ContextoDeTrabalhoAppService(IContextoDeTrabalhoRepository contextos, IConhecimentoRepository conhecimentos,
    LeituraDoBrainAppService leitura, IUnitOfWork unitOfWork, IMapsterTypeAdapter typeAdapter)
{
    public async Task<ContextoDeTrabalhoDto> SubstituirAsync(AcessoAoBrain acesso, int revisaoEsperada, DadosDoContexto dados,
        Sensibilidade sensibilidade, string origem, DateTimeOffset? expiraEm = null, CancellationToken cancellationToken = default)
    {
        await leitura.ValidarAcessoAsync(acesso, cancellationToken);
        ArgumentNullException.ThrowIfNull(dados);
        ProtecaoDeSegredos.GarantirSeguro([origem, dados.Objetivo, dados.Tarefa, dados.Progresso, dados.UltimoResultado,
            .. dados.Referencias, .. dados.Pendencias, .. dados.ProximosPassos]);
        var campos = new[] { dados.Objetivo, dados.Tarefa, dados.Progresso, dados.UltimoResultado }.Concat(dados.Referencias).Concat(dados.Pendencias).Concat(dados.ProximosPassos);
        if (campos.Any(x => x.Contains("chain-of-thought", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("<thinking>", StringComparison.OrdinalIgnoreCase) || x.Contains("raciocínio privado", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Snapshot aceita somente resultados operacionais selecionados.");
        var agora = DateTimeOffset.UtcNow;
        foreach (var id in dados.IdsDecisoesConfirmadas)
        {
            var item = await conhecimentos.ObterPorIdAsync(id, cancellationToken);
            if (item is null || item.IdEspacoDeConhecimento != acesso.IdEspacoDeConhecimento || item.IdProjeto != acesso.IdProjeto ||
                item.Tipo != TipoDeConhecimento.Decisao || item.Status != StatusDoConhecimento.Confirmado || !item.EstaValidoEm(agora) ||
                item.Sensibilidade > sensibilidade) throw new ArgumentException("Decisão exige conhecimento confirmado, válido e compatível no mesmo escopo.");
        }
        var contexto = await contextos.ObterDoEscopoAsync(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, cancellationToken);
        if (contexto is null)
        {
            if (revisaoEsperada != 0) throw new InvalidOperationException("Snapshot inicial exige revisão zero.");
            contexto = new(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, dados, sensibilidade, acesso.IdUsuario, origem, agora, expiraEm);
            await contextos.AdicionarAsync(contexto, cancellationToken);
        }
        else { contexto.Substituir(revisaoEsperada, dados, sensibilidade, acesso.IdUsuario, origem, agora, expiraEm); contextos.Atualizar(contexto); }
        await unitOfWork.SalvarAlteracoesAsync(cancellationToken);
        return typeAdapter.Mapear<ContextoDeTrabalho, ContextoDeTrabalhoDto>(contexto);
    }
    public async Task<ContextoDeTrabalhoDto?> RetomarAsync(AcessoAoBrain acesso, CancellationToken cancellationToken = default)
    {
        await leitura.ValidarAcessoAsync(acesso, cancellationToken);
        var contexto = await contextos.ObterDoEscopoAsync(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, cancellationToken);
        if (contexto is null || !contexto.EstaAtivoEm(DateTimeOffset.UtcNow) || contexto.Sensibilidade == Sensibilidade.Secreto ||
            contexto.Sensibilidade == Sensibilidade.Confidencial && !acesso.PermitirConfidencial) return null;
        // Somente versão ativa, nunca conteúdo antigo ou transcript upstream.
        var dto = typeAdapter.Mapear<ContextoDeTrabalho, ContextoDeTrabalhoDto>(contexto);
        return dto with { AuditoriaAnterior = dto.AuditoriaAnterior is null ? null : dto.AuditoriaAnterior with { Origem = "[origem anterior omitida]" } };
    }
}
