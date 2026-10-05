using Dante.Application.Comum;
using Dante.Domain.Comum;
using Dante.Infrastructure;
using Dante.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dante.Tests;

public sealed class PersistenciaTests
{
    [Fact]
    public void SemConfiguracaoNaoRegistraBanco()
    {
        var services = new ServiceCollection().AddInfrastructure(new ConfigurationBuilder().Build());
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(DanteDbContext));
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(IUnitOfWork));
    }

    [Fact]
    public void PortasCompartilhamContextoSomenteDentroDoEscopo()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Dante"] = "Host=localhost;Database=nao_conectar"
        }).Build();
        using var provider = new ServiceCollection().AddInfrastructure(config).BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var primeiro = provider.CreateScope();
        using var segundo = provider.CreateScope();
        Assert.Same(primeiro.ServiceProvider.GetRequiredService<DanteDbContext>(),
            primeiro.ServiceProvider.GetRequiredService<DanteDbContext>());
        Assert.NotSame(primeiro.ServiceProvider.GetRequiredService<DanteDbContext>(),
            segundo.ServiceProvider.GetRequiredService<DanteDbContext>());
        Assert.IsType<UnitOfWork>(primeiro.ServiceProvider.GetRequiredService<IUnitOfWork>());
        Assert.IsType<Repository<RegistroDeTeste>>(primeiro.ServiceProvider.GetRequiredService<IRepository<RegistroDeTeste>>());
    }

    [Fact]
    public void ModeloProducaoNaoAntecipaBrainEMigrationNaoTemMudancasPendentes()
    {
        using var context = new DanteDbContext(new DbContextOptionsBuilder<DanteDbContext>()
            .UseNpgsql("Host=localhost;Database=nao_conectar").Options);
        Assert.Empty(context.Model.GetEntityTypes());
        Assert.Single(context.Database.GetMigrations());
        Assert.False(context.Database.HasPendingModelChanges());
        var script = context.GetService<IMigrator>().GenerateScript();
        Assert.Contains("brain_data", script);
        Assert.DoesNotContain("CREATE EXTENSION", script);
    }

    [Fact]
    public void ConfigurationPreservaIdEMapeiaConcorrenciaSemMembroNoDominio()
    {
        using var context = CriarContexto("Host=localhost;Database=nao_conectar");
        var entity = context.Model.FindEntityType(typeof(RegistroDeTeste))!;
        Assert.Equal("brain_data", entity.GetSchema());
        Assert.Equal("id", entity.FindProperty("Id")!.GetColumnName());
        Assert.Equal(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never,
            entity.FindProperty("Id")!.ValueGenerated);
        Assert.True(entity.FindProperty("Versao")!.IsConcurrencyToken);
        Assert.True(entity.FindProperty("Versao")!.IsShadowProperty());
        Assert.Equal("xmin", entity.FindProperty("Versao")!.GetColumnName());
    }

    [Fact]
    public void CopiaDesanexadaNaoContornaConcorrencia()
    {
        using var context = CriarContexto("Host=localhost;Database=nao_conectar");
        var repository = new Repository<RegistroDeTeste>(context);
        Assert.Throws<InvalidOperationException>(() => repository.Atualizar(new RegistroDeTeste("nome")));
        Assert.Throws<InvalidOperationException>(() => repository.Remover(new RegistroDeTeste("nome")));
    }

    [PostgreSqlFact]
    public async Task PostgreSqlRealValidaMigrationCrudAtomicidadeConcorrenciaECancelamento()
    {
        // Connection de admin apenas de teste. Cada execução cria/remove seu próprio database aleatório.
        var adminString = Environment.GetEnvironmentVariable("DANTE_TEST_POSTGRES")!;
        var database = "dante_168_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(adminString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin))
            await create.ExecuteNonQueryAsync();
        var builder = new NpgsqlConnectionStringBuilder(adminString) { Database = database, Pooling = false };
        try
        {
            var options = new DbContextOptionsBuilder<DanteDbContext>().UseNpgsql(builder.ConnectionString,
                pg => pg.MigrationsHistoryTable("__EFMigrationsHistory", "brain_meta")).Options;
            await using (var production = new DanteDbContext(options))
            {
                await production.Database.MigrateAsync();
                await production.Database.MigrateAsync();
                Assert.Single(await production.Database.GetAppliedMigrationsAsync());
                Assert.Empty(await production.Database.GetPendingMigrationsAsync());
                await production.GetService<IMigrator>().MigrateAsync("0");
                Assert.Empty(await production.Database.GetAppliedMigrationsAsync());
                await production.Database.MigrateAsync();
            }
            // DDL gerado do modelo fictício; nenhuma entidade de teste entra no assembly de produção.
            await using var primeiro = CriarContexto(builder.ConnectionString);
            await primeiro.Database.ExecuteSqlRawAsync(primeiro.Database.GenerateCreateScript());
            var repo = new Repository<RegistroDeTeste>(primeiro);
            var registro = new RegistroDeTeste("inicial");
            await repo.AdicionarAsync(registro);
            await using (var antes = CriarContexto(builder.ConnectionString))
                Assert.Null(await new Repository<RegistroDeTeste>(antes).ObterPorIdAsync(registro.Id));
            await new UnitOfWork(primeiro).SalvarAlteracoesAsync();

            await using var segundo = CriarContexto(builder.ConnectionString);
            var concorrente = (await new Repository<RegistroDeTeste>(segundo).ObterPorIdAsync(registro.Id))!;
            registro.Nome = "alterado";
            repo.Atualizar(registro);
            await new UnitOfWork(primeiro).SalvarAlteracoesAsync();
            concorrente.Nome = "obsoleto";
            new Repository<RegistroDeTeste>(segundo).Atualizar(concorrente);
            await Assert.ThrowsAsync<ConflitoDeConcorrenciaException>(() => new UnitOfWork(segundo).SalvarAlteracoesAsync());

            await using (var atomic = CriarContexto(builder.ConnectionString))
            {
                var atomicRepo = new Repository<RegistroDeTeste>(atomic);
                await atomicRepo.AdicionarAsync(new RegistroDeTeste("valido"));
                await atomicRepo.AdicionarAsync(new RegistroDeTeste(new string('x', 200)));
                await Assert.ThrowsAsync<DbUpdateException>(() => new UnitOfWork(atomic).SalvarAlteracoesAsync());
            }
            await using (var verificar = CriarContexto(builder.ConnectionString))
            {
                Assert.Equal(1, await verificar.Set<RegistroDeTeste>().CountAsync());
                var salvo = (await new Repository<RegistroDeTeste>(verificar).ObterPorIdAsync(registro.Id))!;
                Assert.Equal("alterado", salvo.Nome);
                new Repository<RegistroDeTeste>(verificar).Remover(salvo);
                await new UnitOfWork(verificar).SalvarAlteracoesAsync();
            }
            await using (var verificar = CriarContexto(builder.ConnectionString))
            {
                Assert.Empty(await verificar.Set<RegistroDeTeste>().ToListAsync());
                using var cancelado = new CancellationTokenSource();
                cancelado.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    new Repository<RegistroDeTeste>(verificar).ObterPorIdAsync(Guid.NewGuid(), cancelado.Token));
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static ContextoDeTeste CriarContexto(string connectionString) => new(
        new DbContextOptionsBuilder<ContextoDeTeste>().UseNpgsql(connectionString).Options);

    private sealed class ContextoDeTeste(DbContextOptions<ContextoDeTeste> options) : DanteDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyConfiguration(new RegistroConfiguration());
        }
    }

    private sealed class RegistroConfiguration : EntidadeConfiguration<RegistroDeTeste>
    {
        public override void Configure(EntityTypeBuilder<RegistroDeTeste> builder)
        {
            base.Configure(builder);
            builder.ToTable("registros_de_teste");
            builder.Property(entity => entity.Nome).HasColumnName("nome").HasMaxLength(100).IsRequired();
        }
    }

    private sealed class RegistroDeTeste(string nome) : EntidadeBase
    {
        public string Nome { get; set; } = nome;
    }
}

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DANTE_TEST_POSTGRES")))
            Skip = "Defina DANTE_TEST_POSTGRES para PostgreSQL de teste com permissão CREATE DATABASE.";
    }
}
