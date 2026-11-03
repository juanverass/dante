using System.Text.Json;
using Dante.Domain.Conhecimentos;
using Dante.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Dante.Tests;

internal static class ConhecimentoLegadoDeTeste
{
    // Grava o schema anterior à #265, sem usar o mapping atual nem inventar natureza para revisões antigas.
    internal static Task InserirAsync(DanteDbContext db, Conhecimento item)
    {
        var historico = JsonSerializer.SerializeToNode(item.Historico)!.AsArray();
        foreach (var revisao in historico) revisao!.AsObject().Remove("Natureza");
        var json = historico.ToJsonString();
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO brain_data.conhecimentos
                (id,id_espaco_de_conhecimento,id_projeto,id_autor,tipo,conteudo,dados_estruturados,status,confianca,
                 sensibilidade,criado_em,atualizado_em,valido_desde,valido_ate,tags,historico,id_conhecimento_substituto)
            VALUES ({item.Id},{item.IdEspacoDeConhecimento},{item.IdProjeto},{item.IdAutor},{(int)item.Tipo},{item.Conteudo},
                    CAST({item.DadosEstruturados} AS jsonb),{(int)item.Status},{item.Confianca},{(int)item.Sensibilidade},
                    {item.CriadoEm},{item.AtualizadoEm},{item.ValidoDesde},{item.ValidoAte},{item.Tags.ToArray()},CAST({json} AS jsonb),{item.IdConhecimentoSubstituto})
            """);
    }
}
