using Dante.Domain.Execucoes;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Execucoes;

// Síntese validada do coordenador (AD-75, D7, #310 PR 3). Uma por execução. Trigger: só numa Coordenada, pela etapa de
// síntese dela (turno do coordenador congelado, fora de plano), com a sessão vinculada à etapa e o hash dos bytes do
// conteúdo; depois de gravada, nunca alterada nem removida.
public sealed class SinteseDaCoordenacaoDbMapping : EntidadeConfiguration<SinteseDaCoordenacao>
{
    public override void Configure(EntityTypeBuilder<SinteseDaCoordenacao> b)
    {
        base.Configure(b);
        b.ToTable("sinteses_da_coordenacao", t =>
            t.HasCheckConstraint("ck_sinteses_da_coordenacao_hash", "hash ~ '^[0-9a-f]{64}$'"));
        b.Property(x => x.IdExecucao).HasColumnName("id_execucao");
        b.Property(x => x.IdEtapa).HasColumnName("id_etapa");
        b.Property(x => x.IdSessaoDeAgente).HasColumnName("id_sessao_de_agente");
        // Texto, não jsonb: o hash é dos bytes exatos da forma canônica.
        b.Property(x => x.Conteudo).HasColumnName("conteudo").HasColumnType("text");
        b.Property(x => x.Hash).HasColumnName("hash").HasMaxLength(SinteseDaCoordenacao.TamanhoDoHash).IsFixedLength();
        b.Property(x => x.RegistradaEm).HasColumnName("registrada_em");
        b.HasOne<Execucao>().WithMany().HasForeignKey(x => x.IdExecucao).OnDelete(DeleteBehavior.Restrict);
        // Etapa da própria execução (chave alternativa da etapa): nunca a de outra.
        b.HasOne<EtapaDaExecucao>().WithMany().HasForeignKey(x => new { x.IdEtapa, x.IdExecucao })
            .HasPrincipalKey(x => new { x.Id, x.IdExecucao }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SessaoDeAgente>().WithMany().HasForeignKey(x => x.IdSessaoDeAgente).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.IdExecucao).IsUnique();
        b.HasIndex(x => x.IdEtapa).IsUnique();
    }
}
