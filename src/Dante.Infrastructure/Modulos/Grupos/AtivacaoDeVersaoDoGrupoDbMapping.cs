using Dante.Domain.Grupos;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Grupos;

// Decisões humanas de ativação (#308, decisão F): só acrescentadas. Um trigger recusa UPDATE e DELETE e confere, na
// inserção, que versão, número e hash aprovado são exatamente os da versão gravada.
public sealed class AtivacaoDeVersaoDoGrupoDbMapping : EntidadeConfiguration<AtivacaoDeVersaoDoGrupo>
{
    public override void Configure(EntityTypeBuilder<AtivacaoDeVersaoDoGrupo> b)
    {
        base.Configure(b);
        b.ToTable("ativacoes_de_grupo",
            t => t.HasCheckConstraint("ck_ativacoes_de_grupo_hash", "hash_aprovado ~ '^[0-9a-f]{64}$'"));
        b.Property(x => x.IdGrupo).HasColumnName("id_grupo");
        b.Property(x => x.IdVersaoDoGrupo).HasColumnName("id_versao_do_grupo");
        b.Property(x => x.NumeroDaVersao).HasColumnName("numero_da_versao");
        b.Property(x => x.HashAprovado).HasColumnName("hash_aprovado").HasMaxLength(VersaoDoGrupo.TamanhoDoHash)
            .IsFixedLength();
        b.Property(x => x.IdDoAutor).HasColumnName("id_do_autor");
        b.Property(x => x.Em).HasColumnName("em");
        b.HasOne<GrupoDeAgentes>().WithMany().HasForeignKey(x => x.IdGrupo).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VersaoDoGrupo>().WithMany().HasForeignKey(x => x.IdVersaoDoGrupo).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.IdGrupo, x.Em });
    }
}
