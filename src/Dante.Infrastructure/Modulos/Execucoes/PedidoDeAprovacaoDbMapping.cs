using Dante.Domain.Execucoes;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Execucoes;

// Gate humano do Roteiro (#308, decisão F). No máximo um pedido pendente por execução (índice único parcial); um
// trigger recusa DELETE, qualquer UPDATE de pedido já decidido e troca de execução, etapa ou hashes.
public sealed class PedidoDeAprovacaoDbMapping : EntidadeConfiguration<PedidoDeAprovacao>
{
    public override void Configure(EntityTypeBuilder<PedidoDeAprovacao> b)
    {
        base.Configure(b);
        b.ToTable("pedidos_de_aprovacao", t =>
        {
            t.HasCheckConstraint("ck_pedidos_de_aprovacao_hashes",
                "hash_do_snapshot ~ '^[0-9a-f]{64}$' AND hash_do_artefato ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("ck_pedidos_de_aprovacao_decisao",
                $"(estado = {(int)EstadoDoPedidoDeAprovacao.Pendente}) = (decidido_em IS NULL) AND " +
                $"(estado IN ({(int)EstadoDoPedidoDeAprovacao.Pendente}, {(int)EstadoDoPedidoDeAprovacao.Cancelado})) = " +
                "(id_do_decisor IS NULL)");
            // Finalidade gravada (AD-75, D4): o gate do plano sempre aponta para o plano decidido; o do Roteiro, nunca.
            t.HasCheckConstraint("ck_pedidos_de_aprovacao_finalidade",
                $"(finalidade = {(int)FinalidadeDaAprovacao.PlanoSomenteLeitura}) = (id_plano IS NOT NULL) AND " +
                $"finalidade IN ({(int)FinalidadeDaAprovacao.ArtefatoDoRoteiro}, {(int)FinalidadeDaAprovacao.PlanoSomenteLeitura})");
        });
        b.Property(x => x.IdExecucao).HasColumnName("id_execucao");
        b.Property(x => x.IdEtapa).HasColumnName("id_etapa");
        b.Property(x => x.Rodada).HasColumnName("rodada");
        b.Property(x => x.HashDoSnapshot).HasColumnName("hash_do_snapshot").HasMaxLength(PedidoDeAprovacao.TamanhoDoHash)
            .IsFixedLength();
        b.Property(x => x.HashDoArtefato).HasColumnName("hash_do_artefato").HasMaxLength(PedidoDeAprovacao.TamanhoDoHash)
            .IsFixedLength();
        b.Property(x => x.Estado).HasColumnName("estado");
        b.Property(x => x.CanalDoDecisor).HasColumnName("canal_do_decisor").HasMaxLength(Execucao.TamanhoMaximoDoCanal);
        b.Property(x => x.IdDoDecisor).HasColumnName("id_do_decisor");
        b.Property(x => x.DecididoEm).HasColumnName("decidido_em");
        b.Property(x => x.Motivo).HasColumnName("motivo").HasMaxLength(PedidoDeAprovacao.TamanhoMaximoDoMotivo);
        b.Property(x => x.CriadoEm).HasColumnName("criado_em");
        b.Property(x => x.Finalidade).HasColumnName("finalidade");
        b.Property(x => x.IdPlano).HasColumnName("id_plano");
        b.Ignore(x => x.Pendente);
        b.HasOne<Execucao>().WithMany().HasForeignKey(x => x.IdExecucao).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<EtapaDaExecucao>().WithMany().HasForeignKey(x => x.IdEtapa).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.IdExecucao).IsUnique().HasFilter($"estado = {(int)EstadoDoPedidoDeAprovacao.Pendente}")
            .HasDatabaseName("ux_pedidos_de_aprovacao_pendente_por_execucao");
        b.HasIndex(x => x.IdEtapa).IsUnique();
        b.HasOne<PlanoDaCoordenacao>().WithMany().HasForeignKey(x => x.IdPlano).OnDelete(DeleteBehavior.Restrict);
        // Um pedido por revisão do plano.
        b.HasIndex(x => x.IdPlano).IsUnique().HasFilter("id_plano IS NOT NULL");
    }
}
