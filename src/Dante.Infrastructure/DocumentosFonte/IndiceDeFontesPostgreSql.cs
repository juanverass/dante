using System.Globalization;
using Dante.Application.BuscaDoBrain;
using Dante.Application.DocumentosFonte;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
namespace Dante.Infrastructure.DocumentosFonte;

public sealed class IndiceDeFontesPostgreSql(DanteDbContext contexto, LeituraDoBrainAppService leitura) : IIndiceDeFontes
{
    private async Task<NpgsqlCommand> Comando(string sql, AcessoAoBrain acesso, CancellationToken ct)
    {
        await leitura.ValidarAcessoAsync(acesso, ct); await contexto.Database.OpenConnectionAsync(ct);
        var comando = new NpgsqlCommand(sql, (NpgsqlConnection)contexto.Database.GetDbConnection());
        Param(comando,"espaco",NpgsqlDbType.Uuid,acesso.IdEspacoDeConhecimento); Param(comando,"projeto",NpgsqlDbType.Uuid,acesso.IdProjeto);
        Param(comando,"conf",NpgsqlDbType.Boolean,acesso.PermitirConfidencial); return comando;
    }
    private static void Param(NpgsqlCommand c, string nome, NpgsqlDbType tipo, object? valor) =>
        c.Parameters.Add(new NpgsqlParameter(nome,tipo) { Value = valor ?? DBNull.Value });
    private const string Escopo = "d.id_espaco_de_conhecimento=@espaco AND d.id_projeto IS NOT DISTINCT FROM @projeto AND NOT d.removido AND (d.sensibilidade<3 OR d.sensibilidade=3 AND @conf)";
    private async Task<bool> Vetores(AcessoAoBrain acesso, CancellationToken ct)
    {
        await using var c = await Comando("SELECT EXISTS(SELECT 1 FROM pg_attribute WHERE attrelid='brain_index.partes_fontes'::regclass AND attname='vetor' AND NOT attisdropped)",acesso,ct);
        return (bool)(await c.ExecuteScalarAsync(ct))!;
    }
    public async Task<IReadOnlyList<ResultadoDaBuscaDto>> BuscarAsync(AcessoAoBrain acesso, BuscaDoBrainSearchDto filtro,
        ModeloEmbedding? modelo, float[]? vetor, CancellationToken cancellationToken = default)
    {
        if (filtro.IdConhecimento is not null || filtro.Tipo is not null || filtro.Status is not null || filtro.Tags.Count > 0) return [];
        var sem = vetor is not null && modelo is not null && await Vetores(acesso,cancellationToken);
        var score = sem ? "CASE WHEN p.modelo=@modelo AND p.dimensao=@dimensao AND p.vetor IS NOT NULL THEN 1-(p.vetor <=> CAST(@vetor AS vector)) END" : "NULL::double precision";
        await using var c = await Comando($"""
            WITH candidatos AS (
              SELECT d.id,d.revisao,p.numero,p.inicio,p.conteudo,d.origem,d.hash,d.sensibilidade,
                ts_rank_cd(p.documento,websearch_to_tsquery('portuguese',@texto))::double precision lexical,{score} semantico
              FROM brain_data.documentos_fonte d JOIN brain_index.partes_fontes p ON p.id_documento=d.id AND p.revisao=d.revisao
              WHERE {Escopo} AND (@sens IS NULL OR d.sensibilidade=@sens)
                AND (@desde IS NULL OR d.atualizado_em>=@desde) AND (@ate IS NULL OR d.atualizado_em<@ate)
            ), ranks AS (
              SELECT *,CASE WHEN lexical>0 THEN rank() OVER(ORDER BY lexical DESC,id,numero) END rl,
                CASE WHEN semantico>0 THEN rank() OVER(ORDER BY semantico DESC NULLS LAST,id,numero) END rs FROM candidatos
            ) SELECT id,revisao,numero,inicio,conteudo,origem,hash,sensibilidade,lexical,semantico,
              COALESCE(1.0/(60+rl),0)+COALESCE(1.0/(60+rs),0) score FROM ranks
              WHERE lexical>0 OR semantico>=0.6 ORDER BY score DESC,id,numero LIMIT @limite OFFSET @offset
            """,acesso,cancellationToken);
        Param(c,"texto",NpgsqlDbType.Text,filtro.Texto); Param(c,"sens",NpgsqlDbType.Integer,filtro.Sensibilidade is { } s ? (int)s : null);
        Param(c,"desde",NpgsqlDbType.TimestampTz,filtro.CriadoDesde);Param(c,"ate",NpgsqlDbType.TimestampTz,filtro.CriadoAte);
        Param(c,"limite",NpgsqlDbType.Integer,filtro.Limite);Param(c,"offset",NpgsqlDbType.Integer,filtro.Deslocamento);
        if(sem) { Param(c,"modelo",NpgsqlDbType.Text,modelo!.Chave);Param(c,"dimensao",NpgsqlDbType.Integer,modelo.Dimensao);Param(c,"vetor",NpgsqlDbType.Text,"["+string.Join(',',vetor!.Select(x=>x.ToString("R",CultureInfo.InvariantCulture)))+"]"); }
        var resultado = new List<ResultadoDaBuscaDto>(); await using var r = await c.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            var trecho = new TrechoDaFonteDto(r.GetGuid(0),r.GetInt32(1),r.GetInt32(2),r.GetInt32(3),ProtecaoDeSegredos.Redigir(r.GetString(4))!,ProtecaoDeSegredos.Redigir(r.GetString(5))!,r.GetString(6));
            var item = new LeituraProtegidaDto(trecho.IdDocumento,acesso.IdEspacoDeConhecimento,acesso.IdProjeto,TipoDeConhecimento.Referencia,
                StatusDoConhecimento.Inferido,(Sensibilidade)r.GetInt32(7),trecho.Revisao,trecho.Conteudo,null,[],trecho.Origem,trecho.Referencia,false);
            resultado.Add(new(item,OrigemDoResultado.FonteBruta,r.GetDouble(10),r.GetDouble(8),r.IsDBNull(9)?null:r.GetDouble(9),"Trecho de fonte bruta; não constitui conhecimento confirmado.",trecho));
        }
        return resultado;
    }
    public async Task<TrechoDaFonteDto?> ObterTrechoAsync(AcessoAoBrain acesso, Guid id, int revisao, int numero, CancellationToken cancellationToken = default)
    {
        await using var c = await Comando($"SELECT d.id,d.revisao,p.numero,p.inicio,p.conteudo,d.origem,d.hash FROM brain_data.documentos_fonte d JOIN brain_index.partes_fontes p ON p.id_documento=d.id AND p.revisao=d.revisao WHERE {Escopo} AND d.id=@id AND d.revisao=@rev AND p.numero=@numero",acesso,cancellationToken);
        Param(c,"id",NpgsqlDbType.Uuid,id);Param(c,"rev",NpgsqlDbType.Integer,revisao);Param(c,"numero",NpgsqlDbType.Integer,numero);
        await using var r = await c.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? new(r.GetGuid(0),r.GetInt32(1),r.GetInt32(2),r.GetInt32(3),r.GetString(4),r.GetString(5),r.GetString(6)) : null;
    }
    public async Task ReconstruirAsync(AcessoAoBrain acesso, CancellationToken cancellationToken = default)
    {
        await using var c = await Comando("UPDATE brain_data.documentos_fonte d SET revisao=revisao WHERE d.id_espaco_de_conhecimento=@espaco AND d.id_projeto IS NOT DISTINCT FROM @projeto",acesso,cancellationToken);
        await c.ExecuteNonQueryAsync(cancellationToken);
    }
    public async Task<int> ReindexarAsync(AcessoAoBrain acesso, IGeradorDeEmbedding embeddings, int limite, CancellationToken cancellationToken = default)
    {
        if (embeddings.Modelo is not { } modelo || !await Vetores(acesso,cancellationToken)) return 0;
        var escopo = embeddings.Externo ? acesso with { PermitirConfidencial=false } : acesso;
        var pendentes = new List<TrechoDaFonteDto>();
        await using (var c = await Comando($"SELECT d.id,d.revisao,p.numero,p.inicio,p.conteudo,d.origem,d.hash FROM brain_data.documentos_fonte d JOIN brain_index.partes_fontes p ON p.id_documento=d.id AND p.revisao=d.revisao WHERE {Escopo} AND (p.modelo IS DISTINCT FROM @modelo OR p.vetor IS NULL) ORDER BY d.id,p.numero LIMIT @limite",escopo,cancellationToken))
        {
            Param(c,"modelo",NpgsqlDbType.Text,modelo.Chave);Param(c,"limite",NpgsqlDbType.Integer,limite);
            await using var r = await c.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken)) pendentes.Add(new(r.GetGuid(0),r.GetInt32(1),r.GetInt32(2),r.GetInt32(3),r.GetString(4),r.GetString(5),r.GetString(6)));
        }
        var gravados = 0;
        foreach (var parte in pendentes)
        {
            if (ProtecaoDeSegredos.ContemSegredo(parte.Conteudo)) continue;
            var vetor = await embeddings.GerarAsync(parte.Conteudo,cancellationToken); if(vetor is null) continue;
            vetor = BuscaDoBrainAppService.Normalizar(vetor,modelo.Dimensao);
            await using var c = await Comando($"""
                WITH atual AS (SELECT d.id FROM brain_data.documentos_fonte d WHERE {Escopo} AND d.id=@id AND d.revisao=@rev FOR SHARE OF d)
                UPDATE brain_index.partes_fontes p SET modelo=@modelo,dimensao=@dimensao,vetor=CAST(@vetor AS vector)
                FROM atual WHERE p.id_documento=atual.id AND p.revisao=@rev AND p.numero=@numero
                """,escopo,cancellationToken);
            Param(c,"id",NpgsqlDbType.Uuid,parte.IdDocumento);Param(c,"rev",NpgsqlDbType.Integer,parte.Revisao);Param(c,"numero",NpgsqlDbType.Integer,parte.Numero);
            Param(c,"modelo",NpgsqlDbType.Text,modelo.Chave);Param(c,"dimensao",NpgsqlDbType.Integer,modelo.Dimensao);
            Param(c,"vetor",NpgsqlDbType.Text,"["+string.Join(',',vetor.Select(x=>x.ToString("R",CultureInfo.InvariantCulture)))+"]");
            gravados += await c.ExecuteNonQueryAsync(cancellationToken);
        }
        return gravados;
    }
}
