using Dante.Domain.Execucoes;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class DependenciaDeEtapaDbMapping : EntidadeConfiguration<DependenciaDeEtapa>
{
    public override void Configure(EntityTypeBuilder<DependenciaDeEtapa> b)
    {
        base.Configure(b);
        b.ToTable("dependencias_de_etapa", t => t.HasCheckConstraint("ck_dependencias_de_etapa_distintas",
            "id_etapa <> id_etapa_requerida"));
        b.Property(x => x.IdExecucao).HasColumnName("id_execucao");
        b.Property(x => x.IdEtapa).HasColumnName("id_etapa");
        b.Property(x => x.IdEtapaRequerida).HasColumnName("id_etapa_requerida");
        // As duas pontas são etapas da mesma execução: FKs compostas com a execução.
        b.HasOne<EtapaDaExecucao>().WithMany().HasForeignKey(x => new { x.IdEtapa, x.IdExecucao })
            .HasPrincipalKey(x => new { x.Id, x.IdExecucao }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<EtapaDaExecucao>().WithMany().HasForeignKey(x => new { x.IdEtapaRequerida, x.IdExecucao })
            .HasPrincipalKey(x => new { x.Id, x.IdExecucao }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Execucao>().WithMany().HasForeignKey(x => x.IdExecucao).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.IdEtapa, x.IdEtapaRequerida }).IsUnique();
    }
}
