using System.Data;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Dante.Infrastructure.Fila;

// Fila durável no PostgreSQL (AD-67). Reivindicação, heartbeat e encerramento são comandos atômicos com CAS por
// geração; o EF fica com o outbox (o trabalho entra na mesma unidade de trabalho da execução). Os instantes vêm do
// relógio injetado, não de now() do banco, para lease e backoff serem controláveis nos testes; com um único Worker por
// host, o relógio do processo é a referência. Nenhum método deixa transação aberta ao retornar.
public sealed class FilaDeTrabalhoPostgreSql(OrquestracaoDbContext context, TimeProvider relogio) : IFilaDeTrabalho
{
    private const string Tabela = OrquestracaoDbContext.Schema + ".trabalhos_da_fila";
    private const int Pendente = (int)EstadoDoTrabalho.Pendente;
    private const int Reivindicado = (int)EstadoDoTrabalho.Reivindicado;

    // Posse válida: mesma geração, mesmo dono e boot, ainda reivindicado e com lease não vencido.
    private const string CondicaoDePosse = "id = @id AND geracao = @geracao AND trabalhador = @trabalhador " +
        "AND boot_do_trabalhador = @boot AND estado = 1 AND lease_ate >= @agora";

    public DateTimeOffset Agora => relogio.GetUtcNow();

    public async Task EnfileirarAsync(TrabalhoDaFila trabalho, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trabalho);
        if (trabalho.Estado != EstadoDoTrabalho.Pendente) throw new InvalidOperationException("Só trabalho pendente entra na fila.");
        await context.Set<TrabalhoDaFila>().AddAsync(trabalho, cancellationToken);
    }

    public Task<TrabalhoDaFila?> ObterPorChaveAsync(string chaveDeDeduplicacao, CancellationToken cancellationToken = default) =>
        context.Set<TrabalhoDaFila>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.ChaveDeDeduplicacao == chaveDeDeduplicacao, cancellationToken);

    public async Task ExecutarEmTransacaoAsync(Func<CancellationToken, Task> gravacao,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gravacao);
        if (context.Database.CurrentTransaction is not null)
        {
            await gravacao(cancellationToken);
            return;
        }
        await using var transacao = await context.Database.BeginTransactionAsync(cancellationToken);
        await gravacao(cancellationToken);
        await transacao.CommitAsync(cancellationToken);
    }

    public async Task<TrabalhoReivindicado?> ReivindicarAsync(IdentidadeDoProcessador processador, TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processador);
        ValidarLease(lease);
        var agora = Agora;
        // Um único comando: a linha escolhida fica travada até o UPDATE, e SKIP LOCKED faz outro processador seguir
        // para a próxima em vez de esperar ou pegar a mesma.
        var sql = $"""
            WITH proximo AS (
              SELECT id FROM {Tabela}
              WHERE (estado = {Pendente} AND disponivel_em <= @agora) OR (estado = {Reivindicado} AND lease_ate < @agora)
              ORDER BY prioridade, disponivel_em, criado_em
              FOR UPDATE SKIP LOCKED
              LIMIT 1)
            UPDATE {Tabela} t
            SET estado = {Reivindicado}, trabalhador = @trabalhador, boot_do_trabalhador = @boot, lease_ate = @lease,
                geracao = t.geracao + 1, tentativas = t.tentativas + 1
            FROM proximo WHERE t.id = proximo.id
            RETURNING t.id, t.tipo, t.id_execucao, t.id_etapa, t.dados, t.chave_de_deduplicacao, t.tentativas,
                      t.maximo_de_tentativas, t.geracao, t.lease_ate
            """;
        return await ComConexaoAsync(async conexao =>
        {
            await using var comando = Comando(conexao, sql);
            comando.Parameters.AddWithValue("agora", agora);
            comando.Parameters.AddWithValue("lease", agora + lease);
            comando.Parameters.AddWithValue("trabalhador", processador.Trabalhador);
            comando.Parameters.AddWithValue("boot", processador.Boot);
            await using var leitor = await comando.ExecuteReaderAsync(cancellationToken);
            if (!await leitor.ReadAsync(cancellationToken)) return null;
            return new TrabalhoReivindicado(leitor.GetGuid(0), (TipoDeTrabalho)leitor.GetInt32(1),
                leitor.IsDBNull(2) ? null : leitor.GetGuid(2), leitor.IsDBNull(3) ? null : leitor.GetGuid(3),
                leitor.IsDBNull(4) ? null : leitor.GetString(4), leitor.GetString(5), leitor.GetInt32(6),
                leitor.GetInt32(7), leitor.GetInt64(8), leitor.GetFieldValue<DateTimeOffset>(9),
                processador.Trabalhador, processador.Boot);
        }, cancellationToken);
    }

    public async Task<bool> RenovarAsync(PosseDoTrabalho posse, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(posse);
        ValidarLease(lease);
        var agora = Agora;
        return await ComConexaoAsync(async conexao =>
        {
            await using var comando = Comando(conexao, $"UPDATE {Tabela} SET lease_ate = @lease WHERE {CondicaoDePosse}");
            Posse(comando, posse, agora);
            comando.Parameters.AddWithValue("lease", agora + lease);
            return await comando.ExecuteNonQueryAsync(cancellationToken) == 1;
        }, cancellationToken);
    }

    public async Task GravarComPosseAsync(PosseDoTrabalho posse, Func<CancellationToken, Task>? gravacao,
        EstadoDoTrabalho? encerrarComo, string? motivo = null, DateTimeOffset? disponivelEm = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(posse);
        if (encerrarComo == EstadoDoTrabalho.Reivindicado)
            throw new ArgumentException("Encerrar como reivindicado não existe.", nameof(encerrarComo));
        if (context.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("A gravação sob posse precisa da sua própria transação.");
        await using var transacao = await context.Database.BeginTransactionAsync(cancellationToken);
        var conexao = (NpgsqlConnection)context.Database.GetDbConnection();
        var agora = Agora;
        // A linha fica travada até o commit: nenhuma reivindicação nem heartbeat a altera no meio da gravação.
        await using (var trava = Comando(conexao, $"SELECT 1 FROM {Tabela} WHERE {CondicaoDePosse} FOR UPDATE"))
        {
            Posse(trava, posse, agora);
            if (await trava.ExecuteScalarAsync(cancellationToken) is null) throw new PosseDoTrabalhoPerdidaException();
        }
        if (gravacao is not null) await gravacao(cancellationToken);
        if (encerrarComo is { } estado)
        {
            var sql = estado == EstadoDoTrabalho.Pendente
                ? $"UPDATE {Tabela} SET estado = {Pendente}, disponivel_em = @disponivel, trabalhador = NULL, " +
                  "boot_do_trabalhador = NULL, lease_ate = NULL, ultimo_erro = @motivo WHERE id = @id AND geracao = @geracao"
                : $"UPDATE {Tabela} SET estado = @estado, encerrado_em = @agora, lease_ate = NULL, ultimo_erro = @motivo " +
                  "WHERE id = @id AND geracao = @geracao";
            await using var fim = Comando(conexao, sql);
            fim.Parameters.AddWithValue("id", posse.IdTrabalho);
            fim.Parameters.AddWithValue("geracao", posse.Geracao);
            fim.Parameters.AddWithValue("estado", (int)estado);
            fim.Parameters.AddWithValue("agora", agora);
            fim.Parameters.AddWithValue("disponivel", disponivelEm ?? agora);
            fim.Parameters.AddWithValue("motivo", (object?)Corte(motivo) ?? DBNull.Value);
            if (await fim.ExecuteNonQueryAsync(cancellationToken) != 1) throw new PosseDoTrabalhoPerdidaException();
        }
        await transacao.CommitAsync(cancellationToken);
    }

    public async Task<bool> DevolverAsync(PosseDoTrabalho posse, DateTimeOffset disponivelEm, string? erro,
        bool contarTentativa, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(posse);
        var agora = Agora;
        return await ComConexaoAsync(async conexao =>
        {
            await using var comando = Comando(conexao, $"""
                UPDATE {Tabela} SET estado = {Pendente}, disponivel_em = @disponivel, trabalhador = NULL,
                    boot_do_trabalhador = NULL, lease_ate = NULL, ultimo_erro = @erro,
                    tentativas = CASE WHEN @contar THEN tentativas ELSE GREATEST(tentativas - 1, 0) END
                WHERE {CondicaoDePosse}
                """);
            Posse(comando, posse, agora);
            comando.Parameters.AddWithValue("disponivel", disponivelEm);
            comando.Parameters.AddWithValue("erro", (object?)Corte(erro) ?? DBNull.Value);
            comando.Parameters.AddWithValue("contar", contarTentativa);
            return await comando.ExecuteNonQueryAsync(cancellationToken) == 1;
        }, cancellationToken);
    }

    public async Task<DateTimeOffset?> ProximaDisponibilidadeAsync(CancellationToken cancellationToken = default) =>
        await ComConexaoAsync(async conexao =>
        {
            await using var comando = Comando(conexao, $"""
                SELECT LEAST(
                  (SELECT min(disponivel_em) FROM {Tabela} WHERE estado = {Pendente}),
                  (SELECT min(lease_ate) FROM {Tabela} WHERE estado = {Reivindicado}))
                """);
            var valor = await comando.ExecuteScalarAsync(cancellationToken);
            return valor is DateTime instante ? new DateTimeOffset(DateTime.SpecifyKind(instante, DateTimeKind.Utc))
                : valor as DateTimeOffset?;
        }, cancellationToken);

    public async Task<int> DevolverDeBootsAnterioresAsync(IdentidadeDoProcessador atual,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(atual);
        var agora = Agora;
        return await ComConexaoAsync(async conexao =>
        {
            await using var comando = Comando(conexao, $"""
                UPDATE {Tabela} SET estado = {Pendente}, disponivel_em = @agora, trabalhador = NULL,
                    boot_do_trabalhador = NULL, lease_ate = NULL, ultimo_erro = 'boot anterior'
                WHERE estado = {Reivindicado} AND split_part(trabalhador, ':', 1) = @host AND boot_do_trabalhador <> @boot
                """);
            comando.Parameters.AddWithValue("agora", agora);
            comando.Parameters.AddWithValue("host", atual.Host);
            comando.Parameters.AddWithValue("boot", atual.Boot);
            return await comando.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<TrabalhoDaFila>> TravarAtivosDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default)
    {
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Travar os trabalhos da execução exige uma transação.");
        return await context.Set<TrabalhoDaFila>().FromSql(
                $"SELECT *, xmin FROM orquestracao.trabalhos_da_fila WHERE id_execucao = {idExecucao} AND estado IN (0, 1) ORDER BY id FOR UPDATE")
            .AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task<bool> DescartarPendenteAsync(Guid idTrabalho, string motivo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Descartar o trabalho travado exige a transação que o travou.");
        var conexao = (NpgsqlConnection)context.Database.GetDbConnection();
        await using var comando = Comando(conexao, $"UPDATE {Tabela} SET estado = {(int)EstadoDoTrabalho.Descartado}, " +
            $"encerrado_em = @agora, ultimo_erro = @motivo WHERE id = @id AND estado = {Pendente}");
        comando.Parameters.AddWithValue("id", idTrabalho);
        comando.Parameters.AddWithValue("agora", Agora);
        comando.Parameters.AddWithValue("motivo", Corte(motivo)!);
        return await comando.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<TrabalhoDaFila>> ListarAtivosAsync(IReadOnlyCollection<Guid> idsExecucao,
        CancellationToken cancellationToken = default) =>
        idsExecucao.Count == 0 ? [] : await context.Set<TrabalhoDaFila>().AsNoTracking()
            .Where(x => x.IdExecucao != null && idsExecucao.Contains(x.IdExecucao.Value) &&
                        (x.Estado == EstadoDoTrabalho.Pendente || x.Estado == EstadoDoTrabalho.Reivindicado))
            .ToListAsync(cancellationToken);

    // Comandos fora de transação usam a conexão do contexto e a devolvem ao pool em seguida.
    private async Task<T> ComConexaoAsync<T>(Func<NpgsqlConnection, Task<T>> acao, CancellationToken cancellationToken)
    {
        var conexao = (NpgsqlConnection)context.Database.GetDbConnection();
        var abriu = conexao.State != ConnectionState.Open;
        if (abriu) await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            return await acao(conexao);
        }
        finally
        {
            if (abriu && context.Database.CurrentTransaction is null) await context.Database.CloseConnectionAsync();
        }
    }

    private NpgsqlCommand Comando(NpgsqlConnection conexao, string sql) => new(sql, conexao)
    {
        Transaction = context.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction
    };

    private static void Posse(NpgsqlCommand comando, PosseDoTrabalho posse, DateTimeOffset agora)
    {
        comando.Parameters.AddWithValue("id", posse.IdTrabalho);
        comando.Parameters.AddWithValue("geracao", posse.Geracao);
        comando.Parameters.AddWithValue("trabalhador", posse.Trabalhador);
        comando.Parameters.AddWithValue("boot", posse.Boot);
        comando.Parameters.AddWithValue("agora", agora);
    }

    private static string? Corte(string? texto) => string.IsNullOrWhiteSpace(texto) ? null
        : texto.Length <= TrabalhoDaFila.TamanhoMaximoDoErro ? texto : texto[..TrabalhoDaFila.TamanhoMaximoDoErro];

    private static void ValidarLease(TimeSpan lease)
    {
        if (lease <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lease), "Lease precisa ser positivo.");
    }
}
