using Dante.Domain.Execucoes;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Execucoes;

// Fila durável (AD-67). Enums gravados como inteiros: o SQL da fila usa os valores de EstadoDoTrabalho.
public sealed class TrabalhoDaFilaDbMapping : EntidadeConfiguration<TrabalhoDaFila>
{
    public override void Configure(EntityTypeBuilder<TrabalhoDaFila> b)
    {
        base.Configure(b);
        b.ToTable("trabalhos_da_fila", t =>
        {
            t.HasCheckConstraint("ck_trabalhos_da_fila_tentativas", "tentativas >= 0 AND maximo_de_tentativas > 0");
            t.HasCheckConstraint("ck_trabalhos_da_fila_geracao", "geracao >= 0");
            t.HasCheckConstraint("ck_trabalhos_da_fila_etapa_com_execucao", "id_etapa IS NULL OR id_execucao IS NOT NULL");
            // Reivindicado sempre tem dono, boot e lease; os demais estados nunca têm lease.
            t.HasCheckConstraint("ck_trabalhos_da_fila_posse",
                "(estado = 1 AND trabalhador IS NOT NULL AND boot_do_trabalhador IS NOT NULL AND lease_ate IS NOT NULL) " +
                "OR (estado <> 1 AND lease_ate IS NULL)");
        });
        b.Property(x => x.Tipo).HasColumnName("tipo");
        b.Property(x => x.Estado).HasColumnName("estado");
        b.Property(x => x.IdExecucao).HasColumnName("id_execucao");
        b.Property(x => x.IdEtapa).HasColumnName("id_etapa");
        b.Property(x => x.Dados).HasColumnName("dados").HasColumnType("jsonb");
        b.Property(x => x.ChaveDeDeduplicacao).HasColumnName("chave_de_deduplicacao")
            .HasMaxLength(TrabalhoDaFila.TamanhoMaximoDaChave);
        b.Property(x => x.Prioridade).HasColumnName("prioridade");
        b.Property(x => x.DisponivelEm).HasColumnName("disponivel_em");
        b.Property(x => x.Tentativas).HasColumnName("tentativas");
        b.Property(x => x.MaximoDeTentativas).HasColumnName("maximo_de_tentativas");
        b.Property(x => x.Trabalhador).HasColumnName("trabalhador").HasMaxLength(TrabalhoDaFila.TamanhoMaximoDoTrabalhador);
        b.Property(x => x.BootDoTrabalhador).HasColumnName("boot_do_trabalhador");
        b.Property(x => x.LeaseAte).HasColumnName("lease_ate");
        b.Property(x => x.Geracao).HasColumnName("geracao");
        b.Property(x => x.UltimoErro).HasColumnName("ultimo_erro").HasMaxLength(TrabalhoDaFila.TamanhoMaximoDoErro);
        b.Property(x => x.CriadoEm).HasColumnName("criado_em");
        b.Property(x => x.EncerradoEm).HasColumnName("encerrado_em");
        b.Ignore(x => x.Terminal);
        b.HasOne<Execucao>().WithMany().HasForeignKey(x => x.IdExecucao).OnDelete(DeleteBehavior.Restrict);
        // A etapa do trabalho é da mesma execução do trabalho.
        b.HasOne<EtapaDaExecucao>().WithMany().HasForeignKey(x => new { x.IdEtapa, x.IdExecucao })
            .HasPrincipalKey(x => new { x.Id, x.IdExecucao }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ChaveDeDeduplicacao).IsUnique();
        // Reivindicação: pendentes por prioridade e disponibilidade; reivindicados pelo vencimento do lease.
        b.HasIndex(x => new { x.Prioridade, x.DisponivelEm }).HasFilter("estado = 0");
        b.HasIndex(x => x.LeaseAte).HasFilter("estado = 1");
        b.HasIndex(x => x.IdExecucao);
    }
}
