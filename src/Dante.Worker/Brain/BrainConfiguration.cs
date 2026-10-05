using Dante.Worker.Brain.Postgres;
using Microsoft.Extensions.Configuration;

namespace Dante.Worker.Brain;

internal static class BrainConfiguration
{
    internal static void Register(IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Brain:Enabled")) return;
        var connection = configuration["Brain:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connection)) throw new BrainStorageException(BrainStorageFailure.Unavailable);
        var root = configuration["Brain:SourcesRoot"] ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dante", "brain", "sources");
        services.AddSingleton(_ => new BrainSourceFiles(root));
        services.AddSingleton(p => new PostgresBrainStorage(connection, p.GetRequiredService<BrainSourceFiles>(), requireRestrictedRole: true));
        services.AddSingleton<IBrainStorage>(p => p.GetRequiredService<PostgresBrainStorage>());
        services.AddSingleton<IBrainDerivedIndex>(p => p.GetRequiredService<PostgresBrainStorage>());
    }

    internal static async Task<int> RunAsync(string[] args, IConfiguration configuration, CancellationToken ct = default)
    {
        try
        {
            if (args.Length < 2 || args[0] != "--brain" || args[1] is not ("migrate" or "health" or "rebuild" or "backup" or "restore"))
                throw new ArgumentException("Uso: --brain migrate|health|rebuild|backup <diretório>|restore <diretório>.");
            var command = args[1];
            if (args.Length != (command is "backup" or "restore" ? 3 : 2)) throw new ArgumentException("Argumentos Brain inválidos.");
            var key = command == "health" ? "Brain:ConnectionString" : "Brain:MigrationConnectionString";
            var connection = configuration[key];
            if (string.IsNullOrWhiteSpace(connection)) throw new ArgumentException("Configuração de conexão Brain ausente.");
            var root = configuration["Brain:SourcesRoot"] ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dante", "brain", "sources");
            var sources = new BrainSourceFiles(root);
            await using var storage = new PostgresBrainStorage(connection, sources, requireRestrictedRole: command == "health");
            switch (command)
            {
                case "migrate": await storage.MigrateAsync(ct); break;
                case "rebuild": await storage.RebuildAsync(ct); break;
                case "health":
                    var health = await storage.HealthAsync(ct);
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(health));
                    return health.CanonicalAvailable && health.SchemaCompatible && health.SourcesIntact ? 0 : 1;
                case "backup": await new BrainBackupService(connection, sources, configuration["Brain:ToolsDirectory"]).BackupAsync(args[2], ct); break;
                case "restore": await new BrainBackupService(connection, sources, configuration["Brain:ToolsDirectory"]).RestoreAsync(args[2], ct); break;
            }
            Console.WriteLine("Operação Brain concluída.");
            return 0;
        }
        catch (BrainStorageException e) { Console.Error.WriteLine(e.Message); return 1; }
        catch (ArgumentException) { Console.Error.WriteLine("Comando ou configuração Brain inválido."); return 1; }
        catch (IOException) { Console.Error.WriteLine("Falha de arquivos na operação Brain."); return 1; }
        catch (UnauthorizedAccessException) { Console.Error.WriteLine("Acesso aos arquivos Brain recusado."); return 1; }
        catch (System.Text.Json.JsonException) { Console.Error.WriteLine("Manifesto Brain inválido."); return 1; }
    }
}
