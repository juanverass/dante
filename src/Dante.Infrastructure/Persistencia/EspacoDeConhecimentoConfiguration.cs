using Dante.Domain.EspacosDeConhecimento;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Dante.Infrastructure.Persistencia;

public sealed class EspacoDeConhecimentoConfiguration : EntidadeConfiguration<EspacoDeConhecimento>
{
    public override void Configure(EntityTypeBuilder<EspacoDeConhecimento> b)
    {
        base.Configure(b);
        b.ToTable("espacos_de_conhecimento");
        b.Property(x => x.IdTenant).HasColumnName("id_tenant");
        b.Property(x => x.IdUsuario).HasColumnName("id_usuario");
        b.Property(x => x.Nome).HasColumnName("nome").HasMaxLength(100);
        b.Property(x => x.Descricao).HasColumnName("descricao").HasMaxLength(1000);
        b.Property(x => x.Estado).HasColumnName("estado");
        b.Ignore(x => x.Arquivado);
        b.HasIndex(x => new { x.IdUsuario, x.Estado });
    }
}
