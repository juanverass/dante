using System.Security.Cryptography;
using System.Text;
using Npgsql;
namespace Dante.Tests;

// Role LOGIN temporária ligada a um banco temporário (#246). A senha é aleatória por execução e só existe em memória:
// o servidor recebe o verificador SCRAM-SHA-256 calculado aqui, nunca o texto, nem em log de statements.
// Funciona igual sob SCRAM e sob trust; nada de pg_hba ou contas de produção.
internal sealed class RoleDeTeste : IAsyncDisposable
{
    private readonly Banco banco;
    private readonly string senha = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string Nome { get; }
    // Connection string do banco temporário autenticando como esta role.
    public string ConnectionString => new NpgsqlConnectionStringBuilder(banco.ConnectionString) { Username = Nome, Password = senha }.ConnectionString;

    private RoleDeTeste(Banco banco, string prefixo) { this.banco = banco; Nome = prefixo + "_" + Guid.NewGuid().ToString("N"); }

    public static async Task<RoleDeTeste> CriarAsync(Banco banco, string prefixo)
    {
        var role = new RoleDeTeste(banco, prefixo);
        await using var admin = new NpgsqlConnection(Banco.Administrativa); await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE ROLE {role.Nome} LOGIN PASSWORD '{VerificadorScram(role.senha)}'", admin);
        await create.ExecuteNonQueryAsync();
        return role;
    }

    // Formato armazenado pelo PostgreSQL (RFC 5802/7677): SCRAM-SHA-256$<iterações>:<sal>$<StoredKey>:<ServerKey>.
    internal static string VerificadorScram(string senha, byte[]? sal = null, int iteracoes = 4096)
    {
        sal ??= RandomNumberGenerator.GetBytes(16);
        var salgada = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(senha), sal, iteracoes, HashAlgorithmName.SHA256, 32);
        var chaveCliente = HMACSHA256.HashData(salgada, "Client Key"u8);
        var chaveServidor = HMACSHA256.HashData(salgada, "Server Key"u8);
        return $"SCRAM-SHA-256${iteracoes}:{Convert.ToBase64String(sal)}${Convert.ToBase64String(SHA256.HashData(chaveCliente))}:{Convert.ToBase64String(chaveServidor)}";
    }

    public async ValueTask DisposeAsync()
    {
        Exception? falha = null;
        try
        {
            // Grants vivem no banco temporário; se ele já foi removido, não há dependência a desfazer.
            await using var noBanco = new NpgsqlConnection(banco.ConnectionString); await noBanco.OpenAsync();
            await using var dropOwned = new NpgsqlCommand($"DROP OWNED BY {Nome}", noBanco); await dropOwned.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.InvalidCatalogName or PostgresErrorCodes.UndefinedObject) { }
        catch (Exception ex) { falha = ex; }
        // DROP ROLE é tentado mesmo se DROP OWNED falhou; só a role desta instância, pelo nome aleatório.
        await using var admin = new NpgsqlConnection(Banco.Administrativa); await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP ROLE IF EXISTS {Nome}", admin);
        try { await drop.ExecuteNonQueryAsync(); }
        catch (Exception ex) { throw new InvalidOperationException($"Role temporária {Nome} não foi removida.", falha is null ? ex : new AggregateException(falha, ex)); }
    }
}
