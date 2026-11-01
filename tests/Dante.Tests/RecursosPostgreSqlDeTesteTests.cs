using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace Dante.Tests;

// Regressões da infraestrutura PostgreSQL dos testes (#246): credencial administrativa preservada, autenticação
// SCRAM das roles temporárias e limpeza de bancos/roles criados pela execução, em sucesso e em falha.
public sealed class RecursosPostgreSqlDeTesteTests
{
    [Fact]
    public void ConnectionStringDoBancoTemporarioPreservaCredencialAdministrativa()
    {
        var banco = new NpgsqlConnectionStringBuilder(Banco.ParaBanco(
            "Host=pg.local;Port=5433;Database=postgres;Username=admin_teste;Password='s3;cr=t';SSL Mode=Require", "brain_test_x"));
        Assert.Equal("s3;cr=t", banco.Password);
        Assert.Equal(("pg.local", 5433, "admin_teste", "brain_test_x"), (banco.Host, banco.Port, banco.Username, banco.Database));
        Assert.Equal(SslMode.Require, banco.SslMode);
        Assert.False(banco.Pooling);
    }

    [Fact]
    public void VerificadorScramReproduzVetorDaRfc7677SemExporSenha()
    {
        // RFC 7677, seção 3: senha "pencil", sal e iterações do servidor, prova do cliente e assinatura do servidor.
        var verificador = RoleDeTeste.VerificadorScram("pencil", Convert.FromBase64String("W22ZaJ0SNY7soEsUEjb6gQ=="));
        Assert.Matches(@"^SCRAM-SHA-256\$4096:W22ZaJ0SNY7soEsUEjb6gQ==\$[A-Za-z0-9+/]{43}=:[A-Za-z0-9+/]{43}=$", verificador);
        Assert.DoesNotContain("pencil", verificador);
        var chaves = verificador[(verificador.LastIndexOf('$') + 1)..].Split(':');
        var (storedKey, serverKey) = (Convert.FromBase64String(chaves[0]), Convert.FromBase64String(chaves[1]));
        var authMessage = Encoding.UTF8.GetBytes("n=user,r=rOprNGfwEbeRWgbNEkqO," +
            "r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096," +
            "c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0");
        Assert.Equal("6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=", Convert.ToBase64String(HMACSHA256.HashData(serverKey, authMessage)));
        var prova = Convert.FromBase64String("dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=");
        var assinatura = HMACSHA256.HashData(storedKey, authMessage);
        var chaveCliente = prova.Zip(assinatura, (a, b) => (byte)(a ^ b)).ToArray();
        Assert.Equal(storedKey, SHA256.HashData(chaveCliente));
        // Sal aleatório por padrão: duas roles com a mesma senha não compartilham verificador.
        Assert.NotEqual(RoleDeTeste.VerificadorScram("pencil"), RoleDeTeste.VerificadorScram("pencil"));
    }

    [PostgreSqlFact]
    public async Task BancoTemporarioAutenticaMigraESaiDoServidorAposUso()
    {
        string nome;
        await using (var banco = await Banco.CriarAsync())
        {
            nome = banco.Nome;
            Assert.Equal(new NpgsqlConnectionStringBuilder(Banco.Administrativa).Password,
                new NpgsqlConnectionStringBuilder(banco.ConnectionString).Password);
            await using var c = banco.Contexto();
            Assert.Equal(c.Database.GetMigrations(), await c.Database.GetAppliedMigrationsAsync());
            Assert.True(await ExisteAsync("pg_database", "datname", nome));
        }
        Assert.False(await ExisteAsync("pg_database", "datname", nome));
    }

    [PostgreSqlFact]
    public async Task FalhaNaMigrationRemoveBancoTemporario()
    {
        string? nome = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Banco.CriarAsync(migracao: c =>
        {
            nome = c.Database.GetDbConnection().Database;
            throw new InvalidOperationException("falha simulada na migration");
        }));
        Assert.StartsWith("brain_test_", nome);
        Assert.False(await ExisteAsync("pg_database", "datname", nome!));
    }

    [PostgreSqlFact]
    public async Task RoleTemporariaAutenticaPorSenhaESaiDoServidorAposSucessoEFalha()
    {
        await using var banco = await Banco.CriarAsync(migrar: false);
        string sucesso;
        await using (var role = await RoleDeTeste.CriarAsync(banco, "auth"))
        {
            sucesso = role.Nome;
            Assert.NotNull(new NpgsqlConnectionStringBuilder(role.ConnectionString).Password);
            await using var conexao = new NpgsqlConnection(role.ConnectionString); await conexao.OpenAsync();
            await using var quem = new NpgsqlCommand("SELECT current_user", conexao);
            Assert.Equal(sucesso, await quem.ExecuteScalarAsync());
        }
        Assert.False(await ExisteAsync("pg_roles", "rolname", sucesso));

        // Falha com privilégio concedido no banco: DROP OWNED precisa rodar antes de DROP ROLE.
        string? falha = null;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var role = await RoleDeTeste.CriarAsync(banco, "auth"); falha = role.Nome;
            await ExecutarAsync(banco.ConnectionString, $"CREATE TABLE dependente_{Guid.NewGuid():N}(id int); GRANT SELECT ON ALL TABLES IN SCHEMA public TO {role.Nome}");
            throw new InvalidOperationException("falha simulada no teste");
        });
        Assert.False(await ExisteAsync("pg_roles", "rolname", falha!));

        // Banco removido antes da role (ordem inversa de dispose): a role ainda sai do servidor.
        var outro = await Banco.CriarAsync(migrar: false);
        var orfa = await RoleDeTeste.CriarAsync(outro, "auth");
        await ExecutarAsync(outro.ConnectionString, $"GRANT CREATE ON SCHEMA public TO {orfa.Nome}");
        await outro.DisposeAsync(); await orfa.DisposeAsync();
        Assert.False(await ExisteAsync("pg_database", "datname", outro.Nome));
        Assert.False(await ExisteAsync("pg_roles", "rolname", orfa.Nome));
    }

    private static async Task<bool> ExisteAsync(string catalogo, string coluna, string nome)
    {
        await using var admin = new NpgsqlConnection(Banco.Administrativa); await admin.OpenAsync();
        await using var comando = new NpgsqlCommand($"SELECT EXISTS(SELECT 1 FROM {catalogo} WHERE {coluna} = @nome)", admin);
        comando.Parameters.AddWithValue("nome", nome);
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task ExecutarAsync(string conexao, string sql)
    {
        await using var c = new NpgsqlConnection(conexao); await c.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, c); await comando.ExecuteNonQueryAsync();
    }
}
