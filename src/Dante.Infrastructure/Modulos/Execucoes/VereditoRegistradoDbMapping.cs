using Dante.Domain.Execucoes;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Execucoes;

// Veredito estruturado do revisor (#308, decisão D): um por etapa (índice único) e imutável (trigger da migration).
public sealed class VereditoRegistradoDbMapping : EntidadeConfiguration<VereditoRegistrado>
{
    public override void Configure(EntityTypeBuilder<VereditoRegistrado> b)
    {
        base.Configure(b);
        b.ToTable("vereditos_de_revisao", t =>
        {
            t.HasCheckConstraint("ck_vereditos_de_revisao_hashes",
                "hash_do_artefato ~ '^[0-9a-f]{64}$' AND hash_do_conteudo ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("ck_vereditos_de_revisao_rodada", "rodada >= 0");
        });
        b.Property(x => x.IdExecucao).HasColumnName("id_execucao");
        b.Property(x => x.IdEtapa).HasColumnName("id_etapa");
        b.Property(x => x.Rodada).HasColumnName("rodada");
        b.Property(x => x.HashDoArtefato).HasColumnName("hash_do_artefato").HasMaxLength(VereditoRegistrado.TamanhoDoHash)
            .IsFixedLength();
        b.Property(x => x.Veredito).HasColumnName("veredito");
        b.Property(x => x.Resumo).HasColumnName("resumo").HasMaxLength(VereditoRegistrado.TamanhoMaximoDoResumo);
        b.Property(x => x.Itens).HasColumnName("itens").HasColumnType("text[]")
            .HasConversion(x => x.ToArray(), x => Array.AsReadOnly(x), new ValueComparer<IReadOnlyList<string>>(
                (a, c) => a!.SequenceEqual(c!), lista => lista.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
                lista => Array.AsReadOnly(lista.ToArray())));
        b.Property(x => x.Sessao).HasColumnName("sessao").HasMaxLength(32);
        b.Property(x => x.HashDoConteudo).HasColumnName("hash_do_conteudo").HasMaxLength(VereditoRegistrado.TamanhoDoHash)
            .IsFixedLength();
        b.Property(x => x.RegistradoEm).HasColumnName("registrado_em");
        b.HasOne<Execucao>().WithMany().HasForeignKey(x => x.IdExecucao).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<EtapaDaExecucao>().WithMany().HasForeignKey(x => x.IdEtapa).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.IdEtapa).IsUnique();
    }
}
