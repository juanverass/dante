using Dante.Domain.Execucoes;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Execucoes;

// Revisões do plano de uma Coordenada (AD-75, #310). Uma por número na execução, em cadeia (cada substituta aponta
// para a anterior, uma só vez), de 1 a 3. Triggers recusam DELETE, alterar conteúdo, hash e identidade, e transições
// fora das do domínio; no commit, um plano Aprovado precisa de uma decisão Aprovado do gate sobre o mesmo hash, e uma
// revisão substituta, da anterior marcada Substituido.
public sealed class PlanoDaCoordenacaoDbMapping : EntidadeConfiguration<PlanoDaCoordenacao>
{
    public override void Configure(EntityTypeBuilder<PlanoDaCoordenacao> b)
    {
        base.Configure(b);
        b.ToTable("planos_da_coordenacao", t =>
        {
            t.HasCheckConstraint("ck_planos_da_coordenacao_hash", "hash ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("ck_planos_da_coordenacao_revisao",
                $"revisao BETWEEN 1 AND {PlanoDaCoordenacao.RevisoesMaximas} AND " +
                $"(revisao = 1) = (id_plano_anterior IS NULL) AND (revisao = 1) = (motivo = {(int)MotivoDoPlano.Inicial})");
            t.HasCheckConstraint("ck_planos_da_coordenacao_unidades",
                $"quantidade_de_unidades BETWEEN 1 AND {ValidacaoDoPlano.MaximoDeUnidades}");
            t.HasCheckConstraint("ck_planos_da_coordenacao_decisao",
                $"(estado = {(int)EstadoDoPlano.AguardandoAprovacao}) = (decidido_em IS NULL AND substituido_em IS NULL) " +
                $"OR estado = {(int)EstadoDoPlano.Substituido}");
        });
        b.Property(x => x.IdExecucao).HasColumnName("id_execucao");
        b.Property(x => x.Revisao).HasColumnName("revisao");
        b.Property(x => x.IdPlanoAnterior).HasColumnName("id_plano_anterior");
        b.Property(x => x.Motivo).HasColumnName("motivo");
        // Texto, não jsonb: o hash é dos bytes exatos da forma canônica.
        b.Property(x => x.Conteudo).HasColumnName("conteudo").HasColumnType("text");
        b.Property(x => x.Hash).HasColumnName("hash").HasMaxLength(PlanoDaCoordenacao.TamanhoDoHash).IsFixedLength();
        b.Property(x => x.QuantidadeDeUnidades).HasColumnName("quantidade_de_unidades");
        b.Property(x => x.Estado).HasColumnName("estado");
        b.Property(x => x.CriadoEm).HasColumnName("criado_em");
        b.Property(x => x.DecididoEm).HasColumnName("decidido_em");
        b.Property(x => x.SubstituidoEm).HasColumnName("substituido_em");
        b.HasOne<Execucao>().WithMany().HasForeignKey(x => x.IdExecucao).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PlanoDaCoordenacao>().WithMany().HasForeignKey(x => x.IdPlanoAnterior).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.IdExecucao, x.Revisao }).IsUnique();
        b.HasIndex(x => x.IdPlanoAnterior).IsUnique();
    }
}
