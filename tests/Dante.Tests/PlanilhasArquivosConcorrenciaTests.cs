using System.Text.Json;
using Dante.Application.Planilhas;
using Dante.Infrastructure.Planilhas;

namespace Dante.Tests;

public sealed class PlanilhasArquivosConcorrenciaTests
{
    // Força os dois serviços a observar o mesmo snapshot antes da mutação lógica.
    private sealed class LeituraSincronizada
    {
        private readonly TaskCompletionSource pronta = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int leituras;
        public async Task AguardarAsync()
        {
            var numero = Interlocked.Increment(ref leituras);
            if (numero == 2) pronta.TrySetResult();
            if (numero <= 2) await pronta.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class CadastroSincronizado(CadastroDePlanilhasEmArquivo store, LeituraSincronizada barreira) : ICadastroDePlanilhas
    {
        public async Task<IReadOnlyList<PlanilhaCadastradaDto>> ListarAsync(CancellationToken ct = default)
        {
            var resultado = await store.ListarAsync(ct);
            await barreira.AguardarAsync();
            return resultado;
        }
        public async Task<PlanilhaCadastradaDto?> ObterAsync(string alias, CancellationToken ct = default)
        {
            var resultado = await store.ObterAsync(alias, ct);
            await barreira.AguardarAsync();
            return resultado;
        }
        public Task SalvarAsync(PlanilhaCadastradaDto planilha, CancellationToken ct = default) => store.SalvarAsync(planilha, ct);
        public Task<bool> RemoverAsync(string alias, CancellationToken ct = default) => store.RemoverAsync(alias, ct);
        public Task<PlanilhaCadastradaDto> AtualizarAsync(string alias,
            Func<IReadOnlyList<PlanilhaCadastradaDto>, PlanilhaCadastradaDto> atualizar, CancellationToken ct = default) =>
            store.AtualizarAsync(alias, atualizar, ct);
    }

    private static (PlanilhasAppService A, PlanilhasAppService B) Servicos(AmbienteDePlanilhas ambiente)
    {
        var diretorio = Path.GetDirectoryName(ambiente.Cadastro.Caminho)!;
        var barreira = new LeituraSincronizada();
        PlanilhasAppService Criar() => new(ambiente.Adapter, ambiente.OAuth,
            new CadastroSincronizado(new CadastroDePlanilhasEmArquivo(diretorio), barreira), ambiente.Auditoria);
        return (Criar(), Criar());
    }

    [Fact]
    public async Task DuasInstanciasAnotamRegioesNoMesmoAliasSemPerda()
    {
        using var ambiente = new AmbienteDePlanilhas();
        await ambiente.CadastrarAsync();
        var (a, b) = Servicos(ambiente);
        await Task.WhenAll(a.AnotarRegiaoAsync("financas", "primeira", "Dados!A1"),
            b.AnotarRegiaoAsync("financas", "segunda", "Dados!B1"));
        Assert.Equal(["primeira", "segunda"], (await ambiente.Cadastro.ObterAsync("financas"))!.Regioes.Select(r => r.Nome));
    }

    [Fact]
    public async Task DuasInstanciasNaoCadastramMesmoIdComAliasesDiferentes()
    {
        using var ambiente = new AmbienteDePlanilhas();
        ambiente.Google.AdicionarAba("Dados");
        var (a, b) = Servicos(ambiente);
        async Task<bool> Cadastrar(PlanilhasAppService servico, string alias)
        {
            try { await servico.CadastrarAsync(alias, ambiente.Google.IdDaPlanilha); return true; }
            catch (FalhaDePlanilhaException exception)
            {
                Assert.Equal(MotivoDaFalhaDePlanilha.Conflito, exception.Motivo);
                return false;
            }
        }
        var resultados = await Task.WhenAll(Task.Run(() => Cadastrar(a, "primeira")), Task.Run(() => Cadastrar(b, "segunda")));
        Assert.Single(resultados, r => r);
        Assert.Single(await ambiente.Cadastro.ListarAsync());
    }

    [Fact]
    public async Task AdicionarERemoverRegioesConcorrentesNaoRessuscitaSnapshotAntigo()
    {
        using var ambiente = new AmbienteDePlanilhas();
        await ambiente.CadastrarAsync();
        await ambiente.Servico.AnotarRegiaoAsync("financas", "antiga", "Dados!A1");
        var (a, b) = Servicos(ambiente);
        await Task.WhenAll(a.AnotarRegiaoAsync("financas", "nova", "Dados!B1"), b.RemoverRegiaoAsync("financas", "antiga"));
        Assert.Equal("nova", Assert.Single((await ambiente.Cadastro.ObterAsync("financas"))!.Regioes).Nome);
    }

    [Fact]
    public async Task InstanciasIndependentesPreservamTodosOsCadastros()
    {
        var diretorio = Directory.CreateTempSubdirectory("dante-cadastro-lock-").FullName;
        try
        {
            var a = new CadastroDePlanilhasEmArquivo(diretorio);
            var b = new CadastroDePlanilhasEmArquivo(diretorio);
            await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() =>
                (i % 2 == 0 ? a : b).SalvarAsync(new PlanilhaCadastradaDto { Alias = $"p{i}", IdDaPlanilha = $"id{i}" }))));
            Assert.Equal(40, (await a.ListarAsync()).Count);
        }
        finally { Directory.Delete(diretorio, true); }
    }

    [Fact]
    public async Task InstanciasIndependentesPreservamLinhasInteirasDaAuditoria()
    {
        var diretorio = Directory.CreateTempSubdirectory("dante-auditoria-lock-").FullName;
        try
        {
            var a = new AuditoriaDePlanilhasEmArquivo(diretorio);
            var b = new AuditoriaDePlanilhasEmArquivo(diretorio);
            await Task.WhenAll(Enumerable.Range(0, 60).Select(i => Task.Run(() =>
                (i % 2 == 0 ? a : b).RegistrarAsync(new RegistroDeAuditoriaDePlanilha(DateTimeOffset.UtcNow,
                    "conta", "id", "alias", "Dados", "atualizar", $"A{i + 1}", "antes", null, "depois", "teste", "codex")))));
            var linhas = File.ReadAllLines(a.Caminho);
            Assert.Equal(60, linhas.Length);
            Assert.Equal(60, linhas.Select(l => JsonSerializer.Deserialize<RegistroDeAuditoriaDePlanilha>(l)!.Endereco).Distinct().Count());
        }
        finally { Directory.Delete(diretorio, true); }
    }
}
