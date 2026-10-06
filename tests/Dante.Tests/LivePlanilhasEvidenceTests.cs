using Dante.Application;
using Dante.Application.Planilhas;
using Dante.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

// #224: evidência opt-in contra o Google real, com a conta conectada por /google connect e a planilha cadastrada por
// /planilha add (a planilha de treino é o cenário pretendido). Lê metadados, busca DANTE_LIVE_GOOGLE_TERMO e, com
// DANTE_LIVE_GOOGLE_CELULA (ex.: 'Aba'!Z99, uma célula vazia), escreve um marcador e restaura o valor anterior.
public sealed class LivePlanilhasEvidenceTests
{
    [LiveGoogleFact]
    public async Task PlanilhaRealEDescritaBuscadaEEditadaPelaCapacidadeGenerica()
    {
        var alias = Environment.GetEnvironmentVariable("DANTE_LIVE_GOOGLE_PLANILHA")!;
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        await using var provider = new ServiceCollection().AddApplication().AddInfrastructure(configuration).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var planilhas = scope.ServiceProvider.GetRequiredService<PlanilhasAppService>();
        Assert.True((await planilhas.ObterConexaoAsync()).Conectada, "Conecte com /google connect antes.");

        var descricao = await planilhas.DescreverAsync(alias);
        Assert.NotEmpty(descricao.Planilha.Abas);
        var primeira = descricao.Planilha.Abas.First(a => a.AreaUsada is not null);
        var leitura = await planilhas.LerAsync(alias, $"{IntervaloA1.DaAba(primeira.Titulo)}!A1:F10");
        Assert.NotEmpty(leitura.Celulas);
        if (Environment.GetEnvironmentVariable("DANTE_LIVE_GOOGLE_TERMO") is { Length: > 0 } termo)
            Assert.NotEmpty((await planilhas.BuscarAsync(alias, termo)).Ocorrencias);

        if (Environment.GetEnvironmentVariable("DANTE_LIVE_GOOGLE_CELULA") is not { Length: > 0 } celula) return;
        var origem = new OrigemDaSolicitacao("teste", "LivePlanilhasEvidenceTests");
        var anterior = (await planilhas.LerAsync(alias, celula, 1)).Celulas.SingleOrDefault();
        var marcador = $"dante-{Guid.NewGuid():N}"[..14];
        await planilhas.AtualizarAsync(new EscritaNaPlanilhaDto
        {
            Planilha = alias,
            Alteracoes = [new AlteracaoDeIntervaloDto { Intervalo = celula, Valores = [[ValorDeCelula.DeTexto(marcador)]] }]
        }, origem);
        Assert.Equal(marcador, (await planilhas.LerAsync(alias, celula, 1)).Celulas.Single().ValorExibido);
        await planilhas.AtualizarAsync(new EscritaNaPlanilhaDto
        {
            Planilha = alias,
            Alteracoes = [new AlteracaoDeIntervaloDto
            {
                Intervalo = celula,
                Valores = [[anterior?.Formula is { } formula ? ValorDeCelula.DeTexto(formula) : anterior?.ValorBruto ?? ValorDeCelula.Vazio]],
                ValoresEsperados = [[marcador]]
            }]
        }, origem);
    }

    private sealed class LiveGoogleFactAttribute : FactAttribute
    {
        public LiveGoogleFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("DANTE_LIVE_GOOGLE") != "1" ||
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DANTE_LIVE_GOOGLE_PLANILHA")))
                Skip = "Evidência com o Google real; rode com DANTE_LIVE_GOOGLE=1 e DANTE_LIVE_GOOGLE_PLANILHA=<alias>.";
        }
    }
}
