using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Domain.Grupos;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Execucoes;

// Snapshot dos participantes de um Roteiro (#308): imutável (trigger), papel único por execução e versão exata do
// agente por chave estrangeira.
public sealed class ParticipanteDaExecucaoDbMapping : EntidadeConfiguration<ParticipanteDaExecucao>
{
    public override void Configure(EntityTypeBuilder<ParticipanteDaExecucao> b)
    {
        base.Configure(b);
        b.ToTable("participantes_da_execucao", t =>
        {
            t.HasCheckConstraint("ck_participantes_da_execucao_hash", "hash_da_configuracao_do_agente ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("ck_participantes_da_execucao_versao", "numero_da_versao_do_agente > 0");
        });
        b.Property(x => x.IdExecucao).HasColumnName("id_execucao");
        b.Property(x => x.Papel).HasColumnName("papel").HasMaxLength(ConfiguracaoDoGrupo.TamanhoMaximoDoPapel);
        b.Property(x => x.HandleDoAgente).HasColumnName("handle_do_agente").HasMaxLength(Agente.TamanhoMaximoDoHandle);
        b.Property(x => x.IdVersaoDoAgente).HasColumnName("id_versao_do_agente");
        b.Property(x => x.NumeroDaVersaoDoAgente).HasColumnName("numero_da_versao_do_agente");
        b.Property(x => x.HashDaConfiguracaoDoAgente).HasColumnName("hash_da_configuracao_do_agente")
            .HasMaxLength(ParticipanteDaExecucao.TamanhoDoHash).IsFixedLength();
        b.Property(x => x.PodeEscrever).HasColumnName("pode_escrever");
        b.Property(x => x.Responsabilidades).HasColumnName("responsabilidades")
            .HasMaxLength(ConfiguracaoDoGrupo.TamanhoMaximoDasResponsabilidades);
        b.HasOne<Execucao>().WithMany().HasForeignKey(x => x.IdExecucao).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VersaoDoAgente>().WithMany().HasForeignKey(x => x.IdVersaoDoAgente).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.IdExecucao, x.Papel }).IsUnique();
        b.HasIndex(x => x.IdVersaoDoAgente);
    }
}
