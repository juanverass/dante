using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Dante.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dante.Tests;

public sealed class InfrastructureArchitectureTests
{
    [Fact]
    public void MigrationsESnapshotPertencemSomenteAInfrastructure()
    {
        var assembly = typeof(DanteDbContext).Assembly;
        var migrations = assembly.GetTypes().Where(t => !t.IsAbstract && typeof(Migration).IsAssignableFrom(t)).ToArray();
        Assert.Equal(10, migrations.Length);
        var snapshot = Assert.Single(assembly.GetTypes(), t => !t.IsAbstract && typeof(ModelSnapshot).IsAssignableFrom(t));
        foreach (var tipo in migrations.Append(snapshot))
        {
            Assert.Equal("Dante.Infrastructure.Data.Migrations", tipo.Namespace);
            Assert.Single(Directory.EnumerateFiles(Path.Combine(Raiz(), "src", "Dante.Infrastructure", "Data", "Migrations"), "*.cs"),
                arquivo => Path.GetFileName(arquivo) == tipo.Name + ".cs" || Path.GetFileName(arquivo).EndsWith("_" + tipo.Name + ".cs", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("Dante.Worker")]
    [InlineData("Dante.WebApi")]
    public void HostsNaoDeclaramPersistenciaENaoAplicamMigrations(string host)
    {
        var arquivos = Fontes(host).ToArray();
        var persistencia = new Regex(@"\b(DbContext|DbSet|Migration|ModelSnapshot|IEntityTypeConfiguration|NpgsqlConnection)\b|\b(?:Migrate|MigrateAsync|EnsureCreated|EnsureCreatedAsync)\s*\(");
        Assert.All(arquivos, arquivo => Assert.DoesNotMatch(persistencia, File.ReadAllText(arquivo)));
        Assert.DoesNotContain(arquivos, arquivo => Path.GetFileName(arquivo).EndsWith("Repository.cs", StringComparison.Ordinal));
        var program = File.ReadAllText(Path.Combine(Raiz(), "src", host, "Program.cs"));
        Assert.Contains("AddApplication().AddInfrastructure(builder.Configuration)", program);
        Assert.Contains("ComandosDoBrain.ExecutarAsync", program);
        Assert.DoesNotContain(Directory.EnumerateDirectories(Path.Combine(Raiz(), "src", host), "*", SearchOption.AllDirectories),
            pasta => Path.GetFileName(pasta) is "Migrations" or "Persistence" or "Persistencia");
    }

    [Fact]
    public void MigracaoProdutivaSoExisteNaAdministracaoExplicita()
    {
        var migracao = new Regex(@"\b(?:Migrate|MigrateAsync|EnsureCreated|EnsureCreatedAsync)\s*\(");
        var chamadas = Fontes("Dante.Infrastructure").Where(arquivo => migracao.IsMatch(File.ReadAllText(arquivo)));
        Assert.Equal([Path.Combine(Raiz(), "src", "Dante.Infrastructure", "Banco", "AdministracaoDoBanco.cs")], chamadas);
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(Raiz(), "src"), "*.csproj", SearchOption.AllDirectories),
            arquivo => Path.GetFileName(arquivo).Contains("Migrator", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> Fontes(string projeto) => Directory.EnumerateFiles(
        Path.Combine(Raiz(), "src", projeto), "*.cs", SearchOption.AllDirectories).Where(arquivo =>
            !arquivo.Split(Path.DirectorySeparatorChar).Any(segmento => segmento is "obj" or "bin"));

    private static string Raiz()
    {
        for (var diretorio = new DirectoryInfo(AppContext.BaseDirectory); diretorio is not null; diretorio = diretorio.Parent)
            if (File.Exists(Path.Combine(diretorio.FullName, "Dante.sln"))) return diretorio.FullName;
        throw new DirectoryNotFoundException("Solution não encontrada.");
    }
}
