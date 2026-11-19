using Dante.Domain.Execucoes;
using Dante.Domain.Grupos;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Execucoes;

// Snapshot de uma Coordenada (AD-75, #310): um por execução. Um trigger recusa DELETE e qualquer UPDATE fora da
// substituição do objetivo alterado pela redação (o snapshot nunca muda). Nenhuma coluna guarda o objetivo bruto.
public sealed class CoordenacaoDaExecucaoDbMapping : EntidadeConfiguration<CoordenacaoDaExecucao>
{
    public override void Configure(EntityTypeBuilder<CoordenacaoDaExecucao> b)
    {
        base.Configure(b);
        b.ToTable("coordenacoes_da_execucao", t => t.HasCheckConstraint("ck_coordenacoes_da_execucao_hashes",
            "hash_da_versao_do_grupo ~ '^[0-9a-f]{64}$' AND hash_do_snapshot ~ '^[0-9a-f]{64}$' AND " +
            "hash_do_objetivo_original ~ '^[0-9a-f]{64}$'"));
        b.Property(x => x.IdExecucao).HasColumnName("id_execucao");
        b.Property(x => x.IdGrupo).HasColumnName("id_grupo");
        b.Property(x => x.NomeDoGrupo).HasColumnName("nome_do_grupo").HasMaxLength(GrupoDeAgentes.TamanhoMaximoDoNome);
        b.Property(x => x.IdVersaoDoGrupo).HasColumnName("id_versao_do_grupo");
        b.Property(x => x.NumeroDaVersaoDoGrupo).HasColumnName("numero_da_versao_do_grupo");
        b.Property(x => x.HashDaVersaoDoGrupo).HasColumnName("hash_da_versao_do_grupo")
            .HasMaxLength(CoordenacaoDaExecucao.TamanhoDoHash).IsFixedLength();
        b.Property(x => x.PapelDoCoordenador).HasColumnName("papel_do_coordenador")
            .HasMaxLength(ConfiguracaoDoGrupo.TamanhoMaximoDoPapel);
        b.Property(x => x.HashDoSnapshot).HasColumnName("hash_do_snapshot").HasMaxLength(CoordenacaoDaExecucao.TamanhoDoHash)
            .IsFixedLength();
        b.Property(x => x.ObjetivoRedigido).HasColumnName("objetivo_redigido")
            .HasMaxLength(CoordenacaoDaExecucao.TamanhoMaximoDoObjetivo);
        b.Property(x => x.ObjetivoAlteradoPelaRedacao).HasColumnName("objetivo_alterado_pela_redacao");
        b.Property(x => x.HashDoObjetivoOriginal).HasColumnName("hash_do_objetivo_original")
            .HasMaxLength(CoordenacaoDaExecucao.TamanhoDoHash).IsFixedLength();
        b.Ignore(x => x.ObjetivoUtilizavel);
        b.HasOne<Execucao>().WithOne().HasForeignKey<CoordenacaoDaExecucao>(x => x.IdExecucao)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VersaoDoGrupo>().WithMany().HasForeignKey(x => x.IdVersaoDoGrupo).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<GrupoDeAgentes>().WithMany().HasForeignKey(x => x.IdGrupo).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.IdExecucao).IsUnique();
    }
}
