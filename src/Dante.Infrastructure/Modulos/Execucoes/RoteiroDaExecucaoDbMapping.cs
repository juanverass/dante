using Dante.Domain.Execucoes;
using Dante.Domain.Grupos;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Execucoes;

// Estado durável do Roteiro (#308, decisão B): um por execução. Um trigger recusa DELETE e qualquer UPDATE fora da
// substituição do objetivo alterado pela redação (o snapshot nunca muda). Nenhuma coluna guarda o objetivo bruto.
public sealed class RoteiroDaExecucaoDbMapping : EntidadeConfiguration<RoteiroDaExecucao>
{
    public override void Configure(EntityTypeBuilder<RoteiroDaExecucao> b)
    {
        base.Configure(b);
        b.ToTable("roteiros_da_execucao", t =>
        {
            t.HasCheckConstraint("ck_roteiros_da_execucao_hashes",
                "hash_da_versao_do_grupo ~ '^[0-9a-f]{64}$' AND hash_do_snapshot ~ '^[0-9a-f]{64}$' AND " +
                "hash_do_objetivo_original ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("ck_roteiros_da_execucao_rodadas", "rodadas_de_revisao_maximas >= 0");
        });
        b.Property(x => x.IdExecucao).HasColumnName("id_execucao");
        b.Property(x => x.IdGrupo).HasColumnName("id_grupo");
        b.Property(x => x.NomeDoGrupo).HasColumnName("nome_do_grupo").HasMaxLength(GrupoDeAgentes.TamanhoMaximoDoNome);
        b.Property(x => x.IdVersaoDoGrupo).HasColumnName("id_versao_do_grupo");
        b.Property(x => x.NumeroDaVersaoDoGrupo).HasColumnName("numero_da_versao_do_grupo");
        b.Property(x => x.HashDaVersaoDoGrupo).HasColumnName("hash_da_versao_do_grupo")
            .HasMaxLength(RoteiroDaExecucao.TamanhoDoHash).IsFixedLength();
        b.Property(x => x.Passos).HasColumnName("passos").HasColumnType("text[]")
            .HasConversion(x => x.Select(Texto).ToArray(), x => Array.AsReadOnly(x.Select(Passo).ToArray()),
                new ValueComparer<IReadOnlyList<PassoDoRoteiro>>((a, c) => a!.SequenceEqual(c!),
                    lista => lista.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
                    lista => Array.AsReadOnly(lista.ToArray())));
        b.Property(x => x.RodadasDeRevisaoMaximas).HasColumnName("rodadas_de_revisao_maximas");
        b.Property(x => x.HashDoSnapshot).HasColumnName("hash_do_snapshot").HasMaxLength(RoteiroDaExecucao.TamanhoDoHash)
            .IsFixedLength();
        b.Property(x => x.ObjetivoRedigido).HasColumnName("objetivo_redigido")
            .HasMaxLength(RoteiroDaExecucao.TamanhoMaximoDoObjetivo);
        b.Property(x => x.ObjetivoAlteradoPelaRedacao).HasColumnName("objetivo_alterado_pela_redacao");
        b.Property(x => x.HashDoObjetivoOriginal).HasColumnName("hash_do_objetivo_original")
            .HasMaxLength(RoteiroDaExecucao.TamanhoDoHash).IsFixedLength();
        b.Ignore(x => x.TemCorrecao);
        b.Ignore(x => x.ObjetivoUtilizavel);
        b.HasOne<Execucao>().WithOne().HasForeignKey<RoteiroDaExecucao>(x => x.IdExecucao).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VersaoDoGrupo>().WithMany().HasForeignKey(x => x.IdVersaoDoGrupo).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<GrupoDeAgentes>().WithMany().HasForeignKey(x => x.IdGrupo).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.IdExecucao).IsUnique();
    }

    private static string Texto(PassoDoRoteiro passo) => passo.Papel is null ? passo.Tipo.ToString() : $"{passo.Tipo}:{passo.Papel}";

    private static PassoDoRoteiro Passo(string texto)
    {
        var separador = texto.IndexOf(':');
        return separador < 0
            ? new PassoDoRoteiro(Enum.Parse<TipoDePassoDoRoteiro>(texto))
            : new PassoDoRoteiro(Enum.Parse<TipoDePassoDoRoteiro>(texto[..separador]), texto[(separador + 1)..]);
    }
}
