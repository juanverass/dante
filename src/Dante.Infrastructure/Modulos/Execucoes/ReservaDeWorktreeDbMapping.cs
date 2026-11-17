using Dante.Domain.Execucoes;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Execucoes;

// Reservas de worktree (#307, AD-71). Entre as ativas (Reservada=0, Pronta=1), os índices únicos parciais são a barreira
// do banco: um caminho, uma branch por repositório e um alias por execução. A etapa é ligada por FK composta com a
// execução, como as dependências: o banco recusa reserva de etapa de outra execução.
public sealed class ReservaDeWorktreeDbMapping : EntidadeConfiguration<ReservaDeWorktree>
{
    internal const string FiltroAtivas = "estado IN (0, 1)";

    public override void Configure(EntityTypeBuilder<ReservaDeWorktree> b)
    {
        base.Configure(b);
        b.ToTable("reservas_de_worktree", t =>
        {
            t.HasCheckConstraint("ck_reservas_de_worktree_fora_do_checkout", "caminho <> caminho_do_repositorio");
            t.HasCheckConstraint("ck_reservas_de_worktree_sha", "char_length(sha_base) = 40");
            t.HasCheckConstraint("ck_reservas_de_worktree_estado", "estado BETWEEN 0 AND 5");
        });
        b.Property(x => x.IdExecucao).HasColumnName("id_execucao");
        b.Property(x => x.IdEtapa).HasColumnName("id_etapa");
        b.Property(x => x.Alias).HasColumnName("alias").HasMaxLength(ReservaDeWorktree.TamanhoMaximoDoAlias);
        b.Property(x => x.Principal).HasColumnName("principal");
        b.Property(x => x.CaminhoDoRepositorio).HasColumnName("caminho_do_repositorio")
            .HasMaxLength(ReservaDeWorktree.TamanhoMaximoDoCaminho);
        b.Property(x => x.Caminho).HasColumnName("caminho").HasMaxLength(ReservaDeWorktree.TamanhoMaximoDoCaminho);
        b.Property(x => x.Branch).HasColumnName("branch").HasMaxLength(ReservaDeWorktree.TamanhoMaximoDaBranch);
        b.Property(x => x.ShaBase).HasColumnName("sha_base").HasMaxLength(ReservaDeWorktree.TamanhoDoSha);
        b.Property(x => x.Estado).HasColumnName("estado");
        b.Property(x => x.MaquinaDoWorker).HasColumnName("maquina_do_worker")
            .HasMaxLength(ReservaDeWorktree.TamanhoMaximoDaMaquina);
        b.Property(x => x.Motivo).HasColumnName("motivo").HasMaxLength(Execucao.TamanhoMaximoDoTexto);
        b.Property(x => x.CriadaEm).HasColumnName("criada_em");
        b.Property(x => x.ProntaEm).HasColumnName("pronta_em");
        b.Property(x => x.LiberadaEm).HasColumnName("liberada_em");
        b.Property(x => x.EncerradaEm).HasColumnName("encerrada_em");
        b.Ignore(x => x.Ativa);
        b.Ignore(x => x.Final);
        b.HasOne<Execucao>().WithMany().HasForeignKey(x => x.IdExecucao).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<EtapaDaExecucao>().WithMany().HasForeignKey(x => new { x.IdEtapa, x.IdExecucao })
            .HasPrincipalKey(x => new { x.Id, x.IdExecucao }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Caminho).IsUnique().HasFilter(FiltroAtivas)
            .HasDatabaseName("ux_reservas_de_worktree_caminho_ativa");
        b.HasIndex(x => new { x.CaminhoDoRepositorio, x.Branch }).IsUnique().HasFilter(FiltroAtivas)
            .HasDatabaseName("ux_reservas_de_worktree_branch_ativa");
        b.HasIndex(x => new { x.IdExecucao, x.Alias }).IsUnique().HasFilter(FiltroAtivas)
            .HasDatabaseName("ux_reservas_de_worktree_alias_ativa");
        b.HasIndex(x => new { x.MaquinaDoWorker, x.Estado });
    }
}
