using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class EventoDeOrquestracaoDbMapping : EntidadeConfiguration<EventoDeOrquestracao>
{
    public override void Configure(EntityTypeBuilder<EventoDeOrquestracao> b)
    {
        base.Configure(b);
        b.ToTable("eventos", t =>
        {
            t.HasCheckConstraint("ck_eventos_com_dono", "id_execucao IS NOT NULL OR id_sessao_de_agente IS NOT NULL");
            t.HasCheckConstraint("ck_eventos_etapa_com_execucao", "id_etapa IS NULL OR id_execucao IS NOT NULL");
        });
        // Sequência global atribuída pelo banco no INSERT; leitores retomam por ela.
        b.Property(x => x.Sequencia).HasColumnName("sequencia").ValueGeneratedOnAdd()
            .HasDefaultValueSql($"nextval('{OrquestracaoDbContext.Schema}.{OrquestracaoDbContext.SequenciaDeEventos}')");
        b.Property(x => x.Tipo).HasColumnName("tipo");
        b.Property(x => x.IdExecucao).HasColumnName("id_execucao");
        b.Property(x => x.IdEtapa).HasColumnName("id_etapa");
        b.Property(x => x.IdSessaoDeAgente).HasColumnName("id_sessao_de_agente");
        b.Property(x => x.IdTurno).HasColumnName("id_turno").HasMaxLength(EtapaDaExecucao.TamanhoMaximoDoTurno);
        b.Property(x => x.Dados).HasColumnName("dados").HasColumnType("jsonb");
        b.Property(x => x.OcorridoEm).HasColumnName("ocorrido_em");
        b.HasOne<Execucao>().WithMany().HasForeignKey(x => x.IdExecucao).OnDelete(DeleteBehavior.Restrict);
        // A etapa do evento é da mesma execução do evento.
        b.HasOne<EtapaDaExecucao>().WithMany().HasForeignKey(x => new { x.IdEtapa, x.IdExecucao })
            .HasPrincipalKey(x => new { x.Id, x.IdExecucao }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SessaoDeAgente>().WithMany().HasForeignKey(x => x.IdSessaoDeAgente).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Sequencia).IsUnique();
        b.HasIndex(x => new { x.IdExecucao, x.Sequencia });
        b.HasIndex(x => new { x.IdSessaoDeAgente, x.Sequencia });
    }
}
