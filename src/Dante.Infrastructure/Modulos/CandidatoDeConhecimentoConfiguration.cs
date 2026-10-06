using System.Text.Json;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Dante.Infrastructure.Persistence;
namespace Dante.Infrastructure.Modulos;

public sealed class CandidatoDeConhecimentoConfiguration : EntidadeConfiguration<CandidatoDeConhecimento>
{
    private static List<AtoDoCandidato> Ler(string valor) => JsonSerializer.Deserialize<List<AtoDoCandidato>>(valor)!;
    public override void Configure(EntityTypeBuilder<CandidatoDeConhecimento> b)
    {
        base.Configure(b); b.ToTable("candidatos_de_conhecimento");
        b.Property(x => x.IdEspacoDeConhecimento).HasColumnName("id_espaco_de_conhecimento");
        b.Property(x => x.IdProjeto).HasColumnName("id_projeto");
        b.Property(x => x.Tipo).HasColumnName("tipo");
        b.Property(x => x.Conteudo).HasColumnName("conteudo").HasMaxLength(100000);
        b.Property(x => x.Sensibilidade).HasColumnName("sensibilidade");
        b.Property(x => x.Natureza).HasColumnName("natureza");
        b.Property(x => x.Modo).HasColumnName("modo");
        b.Property(x => x.Justificativa).HasColumnName("justificativa").HasMaxLength(2000);
        b.Property(x => x.Estado).HasColumnName("estado");
        b.Property(x => x.Impressao).HasColumnName("impressao").HasMaxLength(64);
        b.Property(x => x.IdConhecimento).HasColumnName("id_conhecimento");
        b.Property(x => x.IdIncidente).HasColumnName("id_incidente");
        b.Property(x => x.IdSolucao).HasColumnName("id_solucao");
        b.Ignore(x => x.Revisao); b.Ignore(x => x.Proveniencia); b.Ignore(x => x.Historico); b.Ignore(x => x.CriadoEm);
        b.Property<List<AtoDoCandidato>>("historico").HasColumnName("historico").HasColumnType("jsonb")
            .HasConversion(x => ConhecimentoConfiguration.Serializar(x), x => Ler(x))
            .Metadata.SetValueComparer(new ValueComparer<List<AtoDoCandidato>>((a,c) => ConhecimentoConfiguration.Serializar(a) == ConhecimentoConfiguration.Serializar(c),
                x => ConhecimentoConfiguration.Serializar(x).GetHashCode(), x => Ler(ConhecimentoConfiguration.Serializar(x))));
        b.HasIndex(x => new { x.IdEspacoDeConhecimento, x.IdProjeto, x.Impressao }).IsUnique().AreNullsDistinct(false);
        b.HasIndex(x => new { x.IdEspacoDeConhecimento, x.IdProjeto, x.Estado });
        b.HasOne<EspacoDeConhecimento>().WithMany().HasForeignKey(x => x.IdEspacoDeConhecimento).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Projeto>().WithMany().HasForeignKey(x => new { x.IdProjeto, x.IdEspacoDeConhecimento })
            .HasPrincipalKey(x => new { x.Id, x.IdEspacoDeConhecimento }).OnDelete(DeleteBehavior.Restrict);
        foreach (var propriedade in new[] { nameof(CandidatoDeConhecimento.IdConhecimento), nameof(CandidatoDeConhecimento.IdIncidente), nameof(CandidatoDeConhecimento.IdSolucao) })
            b.HasOne<Conhecimento>().WithMany().HasForeignKey(propriedade, nameof(CandidatoDeConhecimento.IdEspacoDeConhecimento))
                .HasPrincipalKey(nameof(Conhecimento.Id), nameof(Conhecimento.IdEspacoDeConhecimento)).OnDelete(DeleteBehavior.Restrict);
    }
}
