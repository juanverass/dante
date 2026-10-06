using Dante.Infrastructure.Modulos.CapturaDeConhecimento;
using Dante.Infrastructure.Modulos.Conhecimentos;
using Dante.Infrastructure.Modulos.ContextosDeTrabalho;
using Dante.Infrastructure.Modulos.DocumentosFonte;
using Dante.Infrastructure.Modulos.EspacosDeConhecimento;
using Dante.Infrastructure.Modulos.Projetos;
using Dante.Infrastructure.Modulos.RelacoesDeConhecimento;
using Dante.Infrastructure.QualidadeDoBrain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Infrastructure.Composicao;

internal static class BrainServiceCollectionExtensions
{
    internal static void AddBrain(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<Dante.Application.SegurancaDoBrain.AutorizacaoDoBrain>();
        services.AddSingleton(_ => new Dante.Infrastructure.SegurancaDoBrain.IdentidadeTelegramDoBrain(configuration));
        services.AddScoped<Dante.Application.EspacosDeConhecimento.IEspacoDeConhecimentoRepository, EspacoDeConhecimentoRepository>();
        services.AddScoped<Dante.Application.Projetos.IProjetoRepository, ProjetoRepository>();
        services.AddScoped<Dante.Application.Conhecimentos.IConhecimentoRepository, ConhecimentoRepository>();
        services.AddSingleton<Dante.Application.ConversaDoBrain.IEstadoDeConversaDoBrain, Dante.Infrastructure.ConversaDoBrain.EstadoDeConversaDoBrainEmMemoria>();
        services.AddScoped<Dante.Application.ConversaDoBrain.ConversaDoBrainAppService>();
        services.AddScoped<Dante.Application.ConversaDoBrain.ConsultaDaConversa>();
        services.AddScoped<Dante.Application.ConversaDoBrain.CapturaDaConversa>();
        services.AddScoped<Dante.Application.ConversaDoBrain.FontesDaConversa>();
        services.AddScoped<Dante.Application.ConversaDoBrain.AlteracoesDaConversa>();
        services.AddScoped<Dante.Application.ConversaDoBrain.InspecaoDaConversa>();
        services.AddScoped<Dante.Application.ConversaDoBrain.ContextoDeTrabalhoDaConversa>();
        services.AddScoped<Dante.Application.ConversaDoBrain.AvaliacaoDaConversa>();
        services.AddScoped<Dante.Application.ConstrucaoDeContexto.ConstrutorDeContextoAppService>();
        services.AddScoped<Dante.Application.AuditoriaDoBrain.IConsultaDeAuditoria, Dante.Infrastructure.AuditoriaDoBrain.ConsultaDeAuditoriaPostgreSql>();
        services.AddScoped<Dante.Application.AuditoriaDoBrain.InspecaoDoBrainAppService>();
        services.AddScoped<Dante.Application.DocumentosFonte.IDocumentoFonteRepository, DocumentoFonteRepository>();
        services.AddScoped<Dante.Application.DocumentosFonte.DocumentoFonteAppService>();
        services.AddScoped<Dante.Application.DocumentosFonte.IIndiceDeFontes, Dante.Infrastructure.DocumentosFonte.IndiceDeFontesPostgreSql>();
        services.AddSingleton<Dante.Application.DocumentosFonte.ILeitorDeFonteLocal>(_ => new Dante.Infrastructure.DocumentosFonte.LeitorDeFonteLocal(configuration));
        services.AddScoped<Dante.Application.QualidadeDoBrain.IConsultaDeQualidade, ConsultaDeQualidadePostgreSql>();
        services.AddScoped<Dante.Application.QualidadeDoBrain.ManutencaoDoBrainAppService>();
        services.AddScoped<Dante.Application.BuscaDoBrain.IIndiceDeBusca, Dante.Infrastructure.BuscaDoBrain.IndiceDeBuscaPostgreSql>();
        services.AddScoped<Dante.Infrastructure.BuscaDoBrain.IndiceDeBuscaPostgreSql>();
        services.AddScoped<Dante.Application.BuscaDoBrain.BuscaDoBrainAppService>();
        services.AddSingleton<Dante.Application.BuscaDoBrain.IGeradorDeEmbedding>(_ => new Dante.Infrastructure.BuscaDoBrain.GeradorDeEmbeddingHttp(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }), configuration));
        services.AddScoped<Dante.Application.ContextosDeTrabalho.IContextoDeTrabalhoRepository, ContextoDeTrabalhoRepository>();
        services.AddScoped<Dante.Application.ContextosDeTrabalho.ContextoDeTrabalhoAppService>();
        services.AddSingleton<Dante.Application.MetricasDoBrain.IRegistroDeMetricasDoBrain>(_ => new Dante.Infrastructure.MetricasDoBrain.RegistroDeMetricasEmArquivo(configuration));
        services.AddScoped<Dante.Application.MetricasDoBrain.MetricasDoBrainAppService>();
        services.AddScoped<Dante.Application.SegurancaDoBrain.LeituraDoBrainAppService>();
        services.AddScoped<Dante.Application.CapturaDeConhecimento.ICandidatoDeConhecimentoRepository, CandidatoDeConhecimentoRepository>();
        services.AddScoped<Dante.Application.CapturaDeConhecimento.ICapturaDeConhecimentoAppService, Dante.Application.CapturaDeConhecimento.CapturaDeConhecimentoAppService>();
        services.AddScoped<Dante.Application.RelacoesDeConhecimento.IRelacaoDeConhecimentoRepository, RelacaoDeConhecimentoRepository>();
        services.AddScoped<Dante.Application.RelacoesDeConhecimento.IRelacaoDeConhecimentoAppService, Dante.Application.RelacoesDeConhecimento.RelacaoDeConhecimentoAppService>();
        services.AddScoped<Dante.Application.EspacosDeConhecimento.IEspacoDeConhecimentoAppService, Dante.Application.EspacosDeConhecimento.EspacoDeConhecimentoAppService>();
        services.AddScoped<Dante.Application.Projetos.IProjetoAppService, Dante.Application.Projetos.ProjetoAppService>();
        services.AddScoped<Dante.Application.Conhecimentos.IConhecimentoAppService, Dante.Application.Conhecimentos.ConhecimentoAppService>();
    }
}
