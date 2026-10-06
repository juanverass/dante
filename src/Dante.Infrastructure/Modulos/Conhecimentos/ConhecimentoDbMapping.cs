using System.Text.Json;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Dante.Infrastructure.Persistence;
namespace Dante.Infrastructure.Modulos.Conhecimentos;

public sealed class ConhecimentoDbMapping : EntidadeConfiguration<Conhecimento>
{
    internal static string Serializar<T>(T valor) => JsonSerializer.Serialize(valor);
    internal static List<RevisaoDoConhecimento> LerHistorico(string valor) => JsonSerializer.Deserialize<List<RevisaoDoConhecimento>>(valor)!;
    public override void Configure(EntityTypeBuilder<Conhecimento> b)
    {
        base.Configure(b);
        b.ToTable("conhecimentos");
        b.Property(x => x.IdEspacoDeConhecimento).HasColumnName("id_espaco_de_conhecimento");
        b.Property(x => x.IdProjeto).HasColumnName("id_projeto");
        b.Property(x => x.IdAutor).HasColumnName("id_autor");
        b.Property(x => x.Tipo).HasColumnName("tipo");
        b.Property(x => x.Conteudo).HasColumnName("conteudo").HasMaxLength(100000);
        b.Property(x => x.DadosEstruturados).HasColumnName("dados_estruturados").HasColumnType("jsonb");
        b.Property(x => x.Status).HasColumnName("status");
        b.Property(x => x.Confianca).HasColumnName("confianca");
        b.Property(x => x.Sensibilidade).HasColumnName("sensibilidade");
        b.Property(x => x.CriadoEm).HasColumnName("criado_em");
        b.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em");
        b.Property(x => x.ValidoDesde).HasColumnName("valido_desde");
        b.Property(x => x.ValidoAte).HasColumnName("valido_ate");
        b.Property(x => x.IdConhecimentoSubstituto).HasColumnName("id_conhecimento_substituto");
        b.Ignore(x => x.Revisao); b.Ignore(x => x.Proveniencia); b.Ignore(x => x.Historico);
        // Histórico é um value object imutável do agregado, não envelope genérico de entidade.
        b.Property<List<RevisaoDoConhecimento>>("historico").HasColumnName("historico").HasColumnType("jsonb")
            .HasConversion(x => Serializar(x), x => LerHistorico(x))
            .Metadata.SetValueComparer(new ValueComparer<List<RevisaoDoConhecimento>>(
                (a, c) => Serializar(a) == Serializar(c), x => Serializar(x).GetHashCode(), x => LerHistorico(Serializar(x))));
        b.Property(x => x.Tags).HasColumnName("tags").HasColumnType("text[]")
            .HasConversion(x => x.ToArray(), x => Array.AsReadOnly(x))
            .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<string>>(
                (a, c) => a!.SequenceEqual(c!), x => Serializar(x).GetHashCode(), x => Array.AsReadOnly(x.ToArray())));
        b.HasAlternateKey(x => new { x.Id, x.IdEspacoDeConhecimento });
        b.HasOne<EspacoDeConhecimento>().WithMany().HasForeignKey(x => x.IdEspacoDeConhecimento).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Projeto>().WithMany().HasForeignKey(x => new { x.IdProjeto, x.IdEspacoDeConhecimento })
            .HasPrincipalKey(x => new { x.Id, x.IdEspacoDeConhecimento }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Conhecimento>().WithMany().HasForeignKey(x => new { x.IdConhecimentoSubstituto, x.IdEspacoDeConhecimento })
            .HasPrincipalKey(x => new { x.Id, x.IdEspacoDeConhecimento }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.IdEspacoDeConhecimento, x.IdProjeto, x.Status });
    }
}
