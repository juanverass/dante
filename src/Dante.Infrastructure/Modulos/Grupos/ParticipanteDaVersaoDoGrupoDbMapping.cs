using Dante.Domain.Agentes;
using Dante.Domain.Grupos;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Grupos;

// Participantes de uma versão de grupo: imutáveis como ela (trigger), papel único dentro da versão e agente
// referenciado por chave estrangeira (agentes nunca são removidos).
public sealed class ParticipanteDaVersaoDoGrupoDbMapping : EntidadeConfiguration<ParticipanteDaVersaoDoGrupo>
{
    public override void Configure(EntityTypeBuilder<ParticipanteDaVersaoDoGrupo> b)
    {
        base.Configure(b);
        b.ToTable("participantes_de_grupo",
            t => t.HasCheckConstraint("ck_participantes_de_grupo_papel", "papel ~ '^[a-z][a-z0-9-]{1,30}$'"));
        b.Property(x => x.IdVersaoDoGrupo).HasColumnName("id_versao_do_grupo");
        b.Property(x => x.IdAgente).HasColumnName("id_agente");
        b.Property(x => x.Papel).HasColumnName("papel").HasMaxLength(ConfiguracaoDoGrupo.TamanhoMaximoDoPapel);
        b.Property(x => x.Responsabilidades).HasColumnName("responsabilidades")
            .HasMaxLength(ConfiguracaoDoGrupo.TamanhoMaximoDasResponsabilidades);
        b.Property(x => x.PodeEscrever).HasColumnName("pode_escrever");
        b.Ignore(x => x.Participante);
        b.HasOne<Agente>().WithMany().HasForeignKey(x => x.IdAgente).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.IdVersaoDoGrupo, x.Papel }).IsUnique();
        b.HasIndex(x => x.IdAgente);
    }
}
