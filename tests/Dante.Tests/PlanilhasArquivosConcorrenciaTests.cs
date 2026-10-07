using System.Text.Json;
using Dante.Application.Planilhas;
using Dante.Infrastructure.Planilhas;

namespace Dante.Tests;

public sealed class PlanilhasArquivosConcorrenciaTests
{
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
