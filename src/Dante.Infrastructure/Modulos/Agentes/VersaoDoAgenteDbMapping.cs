using Dante.Domain.Agentes;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Agentes;

// Versões imutáveis (AD-68): (agente, número) único e um trigger da migration recusa UPDATE e DELETE. Listas como
// text[] com os nomes dos valores, na ordem canônica da configuração.
public sealed class VersaoDoAgenteDbMapping : EntidadeConfiguration<VersaoDoAgente>
{
    public override void Configure(EntityTypeBuilder<VersaoDoAgente> b)
    {
        base.Configure(b);
        b.ToTable("versoes_de_agente", t =>
        {
            t.HasCheckConstraint("ck_versoes_de_agente_numero_positivo", "numero > 0");
            t.HasCheckConstraint("ck_versoes_de_agente_hash", "hash_da_configuracao ~ '^[0-9a-f]{64}$'");
            // Delegação (AD-70, #309): limites todos presentes ou todos ausentes, dentro do teto; paralelismo 1.
            var teto = LimitesDeDelegacao.Teto;
            t.HasCheckConstraint("ck_versoes_de_agente_limites_de_delegacao",
                "(profundidade_maxima_de_delegacao IS NULL AND delegacoes_por_execucao IS NULL AND " +
                "minutos_para_iniciar_delegacao IS NULL AND minutos_de_delegacao IS NULL AND paralelismo_de_delegacoes IS NULL) OR " +
                "(profundidade_maxima_de_delegacao IS NOT NULL AND delegacoes_por_execucao IS NOT NULL AND " +
                "minutos_para_iniciar_delegacao IS NOT NULL AND minutos_de_delegacao IS NOT NULL AND paralelismo_de_delegacoes IS NOT NULL AND " +
                $"profundidade_maxima_de_delegacao BETWEEN 1 AND {teto.ProfundidadeMaxima} AND " +
                $"delegacoes_por_execucao BETWEEN 1 AND {teto.DelegacoesPorExecucao} AND " +
                $"minutos_para_iniciar_delegacao BETWEEN 1 AND {teto.MinutosParaIniciar} AND " +
                $"minutos_de_delegacao BETWEEN 1 AND {teto.MinutosDeDuracao} AND " +
                $"paralelismo_de_delegacoes BETWEEN 1 AND {teto.Paralelismo})");
            t.HasCheckConstraint("ck_versoes_de_agente_consulta_com_coordenacao",
                $"cardinality(pode_consultar) = 0 OR '{nameof(FerramentaDoDante.Coordenacao)}' = ANY (ferramentas)");
        });
        b.Property(x => x.IdAgente).HasColumnName("id_agente");
        b.Property(x => x.Numero).HasColumnName("numero");
        b.Property(x => x.Driver).HasColumnName("driver");
        b.Property(x => x.Modelo).HasColumnName("modelo").HasMaxLength(ConfiguracaoDoAgente.TamanhoMaximoDoModelo);
        b.Property(x => x.Esforco).HasColumnName("esforco").HasMaxLength(ConfiguracaoDoAgente.TamanhoMaximoDoModelo);
        b.Property(x => x.Instrucoes).HasColumnName("instrucoes").HasMaxLength(ConfiguracaoDoAgente.TamanhoMaximoDasInstrucoes);
        b.Property(x => x.Capacidades).HasColumnName("capacidades").HasColumnType("text[]")
            .HasConversion(x => x.Select(valor => valor.ToString()).ToArray(),
                x => Array.AsReadOnly(x.Select(Enum.Parse<CapacidadeDoAgente>).ToArray()), Comparador<CapacidadeDoAgente>());
        b.Property(x => x.PerfilPadrao).HasColumnName("perfil_padrao");
        b.Property(x => x.PerfilMaximo).HasColumnName("perfil_maximo");
        b.Property(x => x.PodeEscrever).HasColumnName("pode_escrever");
        b.Property(x => x.Ferramentas).HasColumnName("ferramentas").HasColumnType("text[]")
            .HasConversion(x => x.Select(valor => valor.ToString()).ToArray(),
                x => Array.AsReadOnly(x.Select(Enum.Parse<FerramentaDoDante>).ToArray()), Comparador<FerramentaDoDante>());
        b.Property(x => x.Repositorios).HasColumnName("repositorios").HasColumnType("text[]")
            .HasConversion(x => x.ToArray(), x => Array.AsReadOnly(x), Comparador<string>());
        b.Property(x => x.ExecucoesSimultaneas).HasColumnName("execucoes_simultaneas");
        b.Property(x => x.TokensPorExecucao).HasColumnName("tokens_por_execucao");
        b.Property(x => x.TurnosPorEtapa).HasColumnName("turnos_por_etapa");
        b.Property(x => x.MinutosPorEtapa).HasColumnName("minutos_por_etapa");
        b.Property(x => x.PodeConsultar).HasColumnName("pode_consultar").HasColumnType("text[]").HasDefaultValueSql("'{}'")
            .HasConversion(x => x.ToArray(), x => Array.AsReadOnly(x), Comparador<string>());
        b.Property(x => x.ProfundidadeMaximaDeDelegacao).HasColumnName("profundidade_maxima_de_delegacao");
        b.Property(x => x.DelegacoesPorExecucao).HasColumnName("delegacoes_por_execucao");
        b.Property(x => x.MinutosParaIniciarDelegacao).HasColumnName("minutos_para_iniciar_delegacao");
        b.Property(x => x.MinutosDeDelegacao).HasColumnName("minutos_de_delegacao");
        b.Property(x => x.ParalelismoDeDelegacoes).HasColumnName("paralelismo_de_delegacoes");
        b.Property(x => x.HashDaConfiguracao).HasColumnName("hash_da_configuracao").HasMaxLength(VersaoDoAgente.TamanhoDoHash)
            .IsFixedLength();
        b.Property(x => x.IdDoAutor).HasColumnName("id_do_autor");
        b.Property(x => x.CriadaEm).HasColumnName("criada_em");
        b.Ignore(x => x.Configuracao);
        b.Ignore(x => x.Integra);
        b.HasOne<Agente>().WithMany().HasForeignKey(x => x.IdAgente).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.IdAgente, x.Numero }).IsUnique();
    }

    // Listas convertidas não são coleções primitivas para o EF: a comparação é pelo conteúdo, na ordem gravada.
    private static ValueComparer<IReadOnlyList<T>> Comparador<T>() => new(
        (a, b) => a!.SequenceEqual(b!),
        lista => lista.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
        lista => Array.AsReadOnly(lista.ToArray()));
}
