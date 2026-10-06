using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dante.Application;

internal static class ServicosDoBrain
{
    internal static void Registrar(IServiceCollection services)
    {
        services.TryAddScoped<Dante.Application.SegurancaDoBrain.AutorizacaoDoBrain>();
        Registrar<Dante.Application.ConversaDoBrain.ConversaDoBrainAppService>(services);
        Registrar<Dante.Application.ConversaDoBrain.ConsultaDaConversa>(services);
        Registrar<Dante.Application.ConversaDoBrain.CapturaDaConversa>(services);
        Registrar<Dante.Application.ConversaDoBrain.FontesDaConversa>(services);
        Registrar<Dante.Application.ConversaDoBrain.AlteracoesDaConversa>(services);
        Registrar<Dante.Application.ConversaDoBrain.InspecaoDaConversa>(services);
        Registrar<Dante.Application.ConversaDoBrain.ContextoDeTrabalhoDaConversa>(services);
        Registrar<Dante.Application.ConversaDoBrain.AvaliacaoDaConversa>(services);
        Registrar<Dante.Application.ConstrucaoDeContexto.ConstrutorDeContextoAppService>(services);
        Registrar<Dante.Application.AuditoriaDoBrain.InspecaoDoBrainAppService>(services);
        Registrar<Dante.Application.DocumentosFonte.DocumentoFonteAppService>(services);
        Registrar<Dante.Application.QualidadeDoBrain.ManutencaoDoBrainAppService>(services);
        Registrar<Dante.Application.BuscaDoBrain.BuscaDoBrainAppService>(services);
        Registrar<Dante.Application.ContextosDeTrabalho.ContextoDeTrabalhoAppService>(services);
        Registrar<Dante.Application.MetricasDoBrain.MetricasDoBrainAppService>(services);
        Registrar<Dante.Application.SegurancaDoBrain.LeituraDoBrainAppService>(services);
        Registrar<Dante.Application.CapturaDeConhecimento.ICapturaDeConhecimentoAppService, Dante.Application.CapturaDeConhecimento.CapturaDeConhecimentoAppService>(services);
        Registrar<Dante.Application.RelacoesDeConhecimento.IRelacaoDeConhecimentoAppService, Dante.Application.RelacoesDeConhecimento.RelacaoDeConhecimentoAppService>(services);
        Registrar<Dante.Application.EspacosDeConhecimento.IEspacoDeConhecimentoAppService, Dante.Application.EspacosDeConhecimento.EspacoDeConhecimentoAppService>(services);
        Registrar<Dante.Application.Projetos.IProjetoAppService, Dante.Application.Projetos.ProjetoAppService>(services);
        Registrar<Dante.Application.Conhecimentos.IConhecimentoAppService, Dante.Application.Conhecimentos.ConhecimentoAppService>(services);
    }

    // Brain é opcional: os hosts sem adapters de persistência também precisam validar e iniciar.
    // A Application registra seus serviços sempre, mas as portas são exigidas ao resolver o caso de uso.
    // Os testes de composição resolvem toda esta lista com adapters configurados para validar o grafo completo.
    private static void Registrar<TService>(IServiceCollection services) where TService : class =>
        services.TryAddScoped<TService>(provider => ActivatorUtilities.CreateInstance<TService>(provider));

    private static void Registrar<TPorta, TService>(IServiceCollection services)
        where TPorta : class where TService : class, TPorta =>
        services.TryAddScoped<TPorta>(provider => ActivatorUtilities.CreateInstance<TService>(provider));
}
