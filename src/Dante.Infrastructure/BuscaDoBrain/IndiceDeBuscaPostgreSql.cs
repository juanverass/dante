using System.Globalization;
using Dante.Application.BuscaDoBrain;
using Dante.Application.SegurancaDoBrain;
using Dante.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
namespace Dante.Infrastructure.BuscaDoBrain;

public sealed class IndiceDeBuscaPostgreSql(DanteDbContext contexto) : IIndiceDeBusca
{
    private async Task<NpgsqlCommand> ComandoAsync(string sql, CancellationToken ct)
    {
        await contexto.Database.OpenConnectionAsync(ct);
        return new NpgsqlCommand(sql, (NpgsqlConnection)contexto.Database.GetDbConnection());
    }
    private static void Param(NpgsqlCommand c, string nome, NpgsqlDbType tipo, object? valor) =>
        c.Parameters.Add(new NpgsqlParameter(nome, tipo) { Value = valor ?? DBNull.Value });
    public async Task<bool> VetoresDisponiveisAsync(CancellationToken cancellationToken = default)
    {
        await using var c = await ComandoAsync("SELECT EXISTS(SELECT 1 FROM pg_attribute WHERE attrelid = 'brain_index.representacoes'::regclass AND attname = 'vetor' AND NOT attisdropped)", cancellationToken);
        return (bool)(await c.ExecuteScalarAsync(cancellationToken))!;
    }
    public async Task PrepararVetoresAsync(CancellationToken cancellationToken = default)
    {
        // Operação administrativa explícita; falta de extensão não bloqueia migrations/lexical.
        await using var c = await ComandoAsync("CREATE EXTENSION IF NOT EXISTS vector; ALTER TABLE brain_index.representacoes ADD COLUMN IF NOT EXISTS vetor vector", cancellationToken);
        await c.ExecuteNonQueryAsync(cancellationToken);
    }
    public async Task ReconstruirLexicalAsync(CancellationToken cancellationToken = default)
    {
        await using var c = await ComandoAsync("""
            INSERT INTO brain_index.trabalhos(id_conhecimento,revisao,documento)
            SELECT id,jsonb_array_length(historico),to_tsvector('portuguese',coalesce(conteudo,'') || ' ' || coalesce(dados_estruturados::text,'') || ' ' || array_to_string(tags,' ')) FROM brain_data.conhecimentos
            ON CONFLICT(id_conhecimento) DO UPDATE SET revisao=EXCLUDED.revisao,documento=EXCLUDED.documento
            """, cancellationToken);
        await c.ExecuteNonQueryAsync(cancellationToken);
    }
    private static void Escopo(NpgsqlCommand c, AcessoAoBrain acesso)
    {
        Param(c, "espaco", NpgsqlDbType.Uuid, acesso.IdEspacoDeConhecimento); Param(c, "projeto", NpgsqlDbType.Uuid, acesso.IdProjeto);
        Param(c, "confidencial", NpgsqlDbType.Boolean, acesso.PermitirConfidencial);
    }
    private const string Autorizado = "c.id_espaco_de_conhecimento = @espaco AND c.id_projeto IS NOT DISTINCT FROM @projeto AND (c.sensibilidade < 3 OR c.sensibilidade = 3 AND @confidencial)";
    private const string Valido = "c.status NOT IN (3,4) AND (c.valido_desde IS NULL OR c.valido_desde <= @instante) AND (c.valido_ate IS NULL OR c.valido_ate > @instante)";
    public async Task<IReadOnlyList<MatchDaBusca>> BuscarAsync(AcessoAoBrain acesso, BuscaDoBrainSearchDto filtro,
        ModeloEmbedding? modelo, float[]? vetor, CancellationToken cancellationToken = default)
    {
        var semantico = vetor is not null && modelo is not null;
        var scoreVetor = semantico ? "CASE WHEN r.vetor IS NOT NULL THEN 1 - (r.vetor <=> CAST(@vetor AS vector)) END" : "NULL::double precision";
        var join = semantico ? "LEFT JOIN brain_index.representacoes r ON r.id_conhecimento = c.id AND r.revisao = t.revisao AND r.modelo = @modelo AND r.dimensao = @dimensao" : "";
        // Somente fragmentos SQL constantes; todos os valores são parâmetros.
        var sql = $"""
            WITH candidatos AS (
                SELECT c.id, t.revisao,
                    CASE WHEN @id IS NOT NULL THEN 1 ELSE ts_rank_cd(t.documento, websearch_to_tsquery('portuguese', @texto)) END::double precision AS lexical,
                    {scoreVetor} AS semantico
                FROM brain_data.conhecimentos c JOIN brain_index.trabalhos t ON t.id_conhecimento = c.id
                {join}
                WHERE c.id_espaco_de_conhecimento = @espaco AND c.id_projeto IS NOT DISTINCT FROM @projeto
                  AND ((c.sensibilidade < 3 OR c.sensibilidade = 3 AND @confidencial) OR @id = c.id)
                  AND {Valido}
                  AND (@id IS NULL OR c.id = @id)
                  AND (@tipo IS NULL OR c.tipo = @tipo) AND (@status IS NULL OR c.status = @status)
                  AND (@sensibilidade IS NULL OR c.sensibilidade = @sensibilidade)
                  AND (@desde IS NULL OR c.criado_em >= @desde) AND (@ate IS NULL OR c.criado_em < @ate)
                  AND (@id IS NOT NULL OR NOT EXISTS(SELECT 1 FROM unnest(@tags::text[]) tag WHERE NOT EXISTS(SELECT 1 FROM unnest(c.tags) existente WHERE lower(existente) = lower(tag))))
            ), ranks AS (
                SELECT *, CASE WHEN lexical > 0 THEN rank() OVER (ORDER BY lexical DESC, id) END AS rl,
                    CASE WHEN semantico > 0 THEN rank() OVER (ORDER BY semantico DESC NULLS LAST, id) END AS rs
                FROM candidatos
            )
            SELECT id, revisao, COALESCE(1.0/(60+rl),0) + COALESCE(1.0/(60+rs),0) AS score, lexical, semantico
            FROM ranks WHERE lexical > 0 OR semantico > 0
            ORDER BY score DESC, id LIMIT @limite OFFSET @offset
            """;
        await using var c = await ComandoAsync(sql, cancellationToken); Escopo(c, acesso);
        Param(c,"id",NpgsqlDbType.Uuid,filtro.IdConhecimento); Param(c,"texto",NpgsqlDbType.Text,filtro.Texto);
        Param(c,"tipo",NpgsqlDbType.Integer,filtro.Tipo is null ? null : (int)filtro.Tipo);
        Param(c,"status",NpgsqlDbType.Integer,filtro.Status is null ? null : (int)filtro.Status);
        Param(c,"sensibilidade",NpgsqlDbType.Integer,filtro.Sensibilidade is null ? null : (int)filtro.Sensibilidade);
        Param(c,"desde",NpgsqlDbType.TimestampTz,filtro.CriadoDesde); Param(c,"ate",NpgsqlDbType.TimestampTz,filtro.CriadoAte);
        Param(c,"instante",NpgsqlDbType.TimestampTz,filtro.ValidoEm ?? DateTimeOffset.UtcNow);
        Param(c,"tags",NpgsqlDbType.Array | NpgsqlDbType.Text,filtro.Tags.ToArray());
        Param(c,"limite",NpgsqlDbType.Integer,filtro.Limite); Param(c,"offset",NpgsqlDbType.Integer,filtro.Deslocamento);
        if (semantico) { Param(c,"vetor",NpgsqlDbType.Text,TextoVetor(vetor!)); Param(c,"modelo",NpgsqlDbType.Text,modelo!.Chave); Param(c,"dimensao",NpgsqlDbType.Integer,modelo.Dimensao); }
        var resultados = new List<MatchDaBusca>(); await using var reader = await c.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) resultados.Add(new(reader.GetGuid(0), reader.GetInt32(1), Convert.ToDouble(reader.GetValue(2),CultureInfo.InvariantCulture), reader.GetDouble(3), reader.IsDBNull(4) ? null : reader.GetDouble(4)));
        return resultados;
    }
    public async Task<IReadOnlyList<Guid>> ListarPendentesAsync(AcessoAoBrain acesso, ModeloEmbedding modelo, int limite, CancellationToken cancellationToken = default)
    {
        await using var c = await ComandoAsync($"SELECT c.id FROM brain_data.conhecimentos c JOIN brain_index.trabalhos t ON t.id_conhecimento = c.id WHERE {Autorizado} AND {Valido} AND NOT EXISTS(SELECT 1 FROM brain_index.representacoes r WHERE r.id_conhecimento = c.id AND r.revisao = t.revisao AND r.modelo = @modelo AND r.vetor IS NOT NULL) ORDER BY c.id LIMIT @limite", cancellationToken);
        Escopo(c,acesso); Param(c,"modelo",NpgsqlDbType.Text,modelo.Chave); Param(c,"limite",NpgsqlDbType.Integer,limite); Param(c,"instante",NpgsqlDbType.TimestampTz,DateTimeOffset.UtcNow);
        var ids = new List<Guid>(); await using var reader = await c.ExecuteReaderAsync(cancellationToken);
        while(await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0)); return ids;
    }
    public async Task<bool> GravarAsync(Guid id, int revisao, ModeloEmbedding modelo, float[] vetor, CancellationToken cancellationToken = default)
    {
        // SELECT revalida revisão e classificação depois da chamada ao gerador; escrita concorrente permanece pendente.
        await using var c = await ComandoAsync("""
            INSERT INTO brain_index.representacoes(id_conhecimento, revisao, modelo, provedor, nome, versao, dimensao, vetor, gerado_em)
            SELECT c.id, @revisao, @modelo, @provedor, @nome, @versao, @dimensao, CAST(@vetor AS vector), now()
            FROM brain_data.conhecimentos c JOIN brain_index.trabalhos t ON t.id_conhecimento = c.id
            WHERE c.id = @id AND t.revisao = @revisao AND c.status NOT IN(3,4) AND c.sensibilidade < 4
            ON CONFLICT(id_conhecimento,modelo) DO UPDATE SET revisao=EXCLUDED.revisao, vetor=EXCLUDED.vetor, gerado_em=EXCLUDED.gerado_em
            """, cancellationToken);
        Param(c,"id",NpgsqlDbType.Uuid,id); Param(c,"revisao",NpgsqlDbType.Integer,revisao); Param(c,"modelo",NpgsqlDbType.Text,modelo.Chave);
        Param(c,"provedor",NpgsqlDbType.Text,modelo.Provedor); Param(c,"nome",NpgsqlDbType.Text,modelo.Nome); Param(c,"versao",NpgsqlDbType.Text,modelo.Versao);
        Param(c,"dimensao",NpgsqlDbType.Integer,modelo.Dimensao); Param(c,"vetor",NpgsqlDbType.Text,TextoVetor(vetor));
        return await c.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
    public async Task LimparModeloAsync(AcessoAoBrain acesso, ModeloEmbedding modelo, CancellationToken cancellationToken = default)
    {
        await using var c = await ComandoAsync("DELETE FROM brain_index.representacoes r USING brain_data.conhecimentos c WHERE r.id_conhecimento = c.id AND c.id_espaco_de_conhecimento = @espaco AND c.id_projeto IS NOT DISTINCT FROM @projeto AND r.modelo = @modelo", cancellationToken);
        Escopo(c,acesso); Param(c,"modelo",NpgsqlDbType.Text,modelo.Chave); await c.ExecuteNonQueryAsync(cancellationToken);
    }
    private static string TextoVetor(float[] vetor) => "[" + string.Join(",", vetor.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";
}
