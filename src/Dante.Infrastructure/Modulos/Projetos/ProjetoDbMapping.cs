using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Dante.Infrastructure.Persistence;
namespace Dante.Infrastructure.Modulos.Projetos;

public sealed class ProjetoDbMapping : EntidadeConfiguration<Projeto>
{
    public override void Configure(EntityTypeBuilder<Projeto> b)
    {
        base.Configure(b);
        b.ToTable("projetos");
        b.Property(x => x.IdEspacoDeConhecimento).HasColumnName("id_espaco_de_conhecimento");
        b.Property(x => x.Nome).HasColumnName("nome").HasMaxLength(100);
        b.Property(x => x.Descricao).HasColumnName("descricao").HasMaxLength(1000);
        b.Property(x => x.AliasDoRepositorio).HasColumnName("alias_do_repositorio").HasMaxLength(100);
        b.Property(x => x.Estado).HasColumnName("estado");
        b.Ignore(x => x.Arquivado);
        b.HasAlternateKey(x => new { x.Id, x.IdEspacoDeConhecimento });
        b.HasOne<EspacoDeConhecimento>().WithMany().HasForeignKey(x => x.IdEspacoDeConhecimento).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.IdEspacoDeConhecimento, x.Estado });
    }
}
