using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Dante.Infrastructure.Data;
namespace Dante.Infrastructure.Banco;

// Operações administrativas explícitas, fora do runtime dos agentes. A conta migradora é escolhida pelo host.
public sealed class AdministracaoDoBanco(DanteDbContext context)
{
    public Task MigrarAsync(CancellationToken cancellationToken = default) => context.Database.MigrateAsync(cancellationToken);

    public async Task<bool> VerificarSaudeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await context.Database.CanConnectAsync(cancellationToken) ||
                (await context.Database.GetPendingMigrationsAsync(cancellationToken)).Any() ||
                (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).Except(context.Database.GetMigrations()).Any()) return false;
            await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT NOT (rolsuper OR rolcreatedb OR rolcreaterole OR rolbypassrls) FROM pg_roles WHERE rolname = current_user", connection);
            return await command.ExecuteScalarAsync(cancellationToken) is true;
        }
        catch (NpgsqlException) { return false; }
    }

    public async Task BackupAsync(string caminho, CancellationToken cancellationToken = default)
    {
        caminho = Path.GetFullPath(caminho);
        if (File.Exists(caminho)) throw new InvalidOperationException("Destino de backup já existe.");
        var temporario = caminho + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Criar antes de pg_dump para que nunca exista uma janela de leitura pública.
            await using (var file = new FileStream(temporario, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporario, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            await ExecutarAsync("pg_dump", ["--format=custom", "--no-owner", "--no-acl", "--file", temporario], cancellationToken);
            File.Move(temporario, caminho, false);
        }
        finally { if (File.Exists(temporario)) File.Delete(temporario); }
    }

    public async Task RestaurarAsync(string caminho, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(caminho)) throw new ArgumentException("Backup não encontrado.");
        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_tables WHERE schemaname NOT IN ('pg_catalog', 'information_schema')", connection);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0)
            throw new InvalidOperationException("Restore exige banco vazio separado.");
        await ExecutarAsync("pg_restore", ["--exit-on-error", "--single-transaction", "--no-owner", "--no-acl", "--dbname", connection.Database, Path.GetFullPath(caminho)], cancellationToken);
    }

    private async Task ExecutarAsync(string executavel, string[] argumentos, CancellationToken cancellationToken)
    {
        var config = new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString());
        // Sem credenciais na linha de comando ou nos logs; stderr do cliente não é propagado.
        var start = new ProcessStartInfo(executavel) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        start.Environment["PGHOST"] = config.Host;
        start.Environment["PGPORT"] = config.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["PGDATABASE"] = config.Database;
        start.Environment["PGUSER"] = config.Username;
        start.Environment["PGPASSWORD"] = config.Password;
        start.Environment["PGSSLMODE"] = config.SslMode switch { SslMode.VerifyCA => "verify-ca", SslMode.VerifyFull => "verify-full", _ => config.SslMode.ToString().ToLowerInvariant() };
        foreach (var argumento in argumentos) start.ArgumentList.Add(argumento);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cliente PostgreSQL indisponível.");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stderr, stdout);
            if (process.ExitCode != 0) throw new InvalidOperationException("Operação administrativa PostgreSQL falhou.");
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
    }
}
