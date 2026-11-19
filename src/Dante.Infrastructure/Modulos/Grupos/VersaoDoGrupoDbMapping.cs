using Dante.Domain.Grupos;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Grupos;

// Versões imutáveis de grupo (AD-69): (grupo, número) único e um trigger da migration recusa UPDATE e DELETE. O roteiro
// é gravado como text[] na ordem dos passos ("Tipo" ou "Tipo:papel"); os participantes, em tabela própria.
public sealed class VersaoDoGrupoDbMapping : EntidadeConfiguration<VersaoDoGrupo>
{
    public override void Configure(EntityTypeBuilder<VersaoDoGrupo> b)
    {
        base.Configure(b);
        b.ToTable("versoes_de_grupo", t =>
        {
            t.HasCheckConstraint("ck_versoes_de_grupo_numero_positivo", "numero > 0");
            t.HasCheckConstraint("ck_versoes_de_grupo_hash", "hash_da_configuracao ~ '^[0-9a-f]{64}$'");
            // Estritamente sequencial nesta etapa (limite global 1, AD-63 inativa).
            t.HasCheckConstraint("ck_versoes_de_grupo_paralelismo", "paralelismo_maximo = 1");
            t.HasCheckConstraint("ck_versoes_de_grupo_rodadas",
                $"rodadas_de_revisao_maximas BETWEEN 0 AND {ConfiguracaoDoGrupo.RodadasMaximasPermitidas}");
            t.HasCheckConstraint("ck_versoes_de_grupo_duracao",
                "duracao_maxima_em_minutos IS NULL OR duracao_maxima_em_minutos > 0");
            // Modo Coordenador (AD-75): sempre com o papel do coordenador, sem roteiro e sem rodadas; o Roteiro, nunca
            // com coordenador.
            t.HasCheckConstraint("ck_versoes_de_grupo_coordenador",
                $"(modo = {(int)ModoDeCoordenacao.Coordenador}) = (papel_do_coordenador IS NOT NULL) AND " +
                $"(modo <> {(int)ModoDeCoordenacao.Coordenador} OR (cardinality(roteiro) = 0 AND rodadas_de_revisao_maximas = 0))");
        });
        b.Property(x => x.IdGrupo).HasColumnName("id_grupo");
        b.Property(x => x.Numero).HasColumnName("numero");
        b.Property(x => x.Modo).HasColumnName("modo");
        b.Property(x => x.Roteiro).HasColumnName("roteiro").HasColumnType("text[]")
            .HasConversion(x => x.Select(Texto).ToArray(), x => Array.AsReadOnly(x.Select(Passo).ToArray()),
                Comparador<PassoDoRoteiro>());
        b.Property(x => x.PapelDoCoordenador).HasColumnName("papel_do_coordenador")
            .HasMaxLength(ConfiguracaoDoGrupo.TamanhoMaximoDoPapel);
        b.Property(x => x.RodadasDeRevisaoMaximas).HasColumnName("rodadas_de_revisao_maximas");
        b.Property(x => x.ParalelismoMaximo).HasColumnName("paralelismo_maximo");
        b.Property(x => x.RepositoriosPermitidos).HasColumnName("repositorios_permitidos").HasColumnType("text[]")
            .HasConversion(x => x.ToArray(), x => Array.AsReadOnly(x), Comparador<string>());
        b.Property(x => x.DuracaoMaximaEmMinutos).HasColumnName("duracao_maxima_em_minutos");
        b.Property(x => x.HashDaConfiguracao).HasColumnName("hash_da_configuracao")
            .HasMaxLength(VersaoDoGrupo.TamanhoDoHash).IsFixedLength();
        b.Property(x => x.IdDoAutor).HasColumnName("id_do_autor");
        b.Property(x => x.CriadaEm).HasColumnName("criada_em");
        b.Ignore(x => x.Configuracao);
        b.Ignore(x => x.Integra);
        b.HasMany(x => x.Participantes).WithOne().HasForeignKey(x => x.IdVersaoDoGrupo).OnDelete(DeleteBehavior.Restrict);
        b.Navigation(x => x.Participantes).HasField("participantes").UsePropertyAccessMode(PropertyAccessMode.Field);
        b.HasOne<GrupoDeAgentes>().WithMany().HasForeignKey(x => x.IdGrupo).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.IdGrupo, x.Numero }).IsUnique();
    }

    private static string Texto(PassoDoRoteiro passo) => passo.Papel is null ? passo.Tipo.ToString() : $"{passo.Tipo}:{passo.Papel}";

    private static PassoDoRoteiro Passo(string texto)
    {
        var separador = texto.IndexOf(':');
        return separador < 0
            ? new PassoDoRoteiro(Enum.Parse<TipoDePassoDoRoteiro>(texto))
            : new PassoDoRoteiro(Enum.Parse<TipoDePassoDoRoteiro>(texto[..separador]), texto[(separador + 1)..]);
    }

    private static ValueComparer<IReadOnlyList<T>> Comparador<T>() => new(
        (a, b) => a!.SequenceEqual(b!),
        lista => lista.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
        lista => Array.AsReadOnly(lista.ToArray()));
}
