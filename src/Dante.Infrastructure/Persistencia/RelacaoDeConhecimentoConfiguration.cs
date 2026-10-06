using System.Text.Json;
using Dante.Domain.Conhecimentos;
using Dante.Domain.RelacoesDeConhecimento;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Dante.Infrastructure.Persistencia;

public sealed class RelacaoDeConhecimentoConfiguration : EntidadeConfiguration<RelacaoDeConhecimento>
{
    public override void Configure(EntityTypeBuilder<RelacaoDeConhecimento> b)
    {
        base.Configure(b); b.ToTable("relacoes_de_conhecimento");
        b.Property(x => x.IdEspacoDeConhecimento).HasColumnName("id_espaco_de_conhecimento");
        b.Property(x => x.IdProjeto).HasColumnName("id_projeto");
        b.Property(x => x.IdOrigem).HasColumnName("id_origem");
        b.Property(x => x.IdDestino).HasColumnName("id_destino");
        b.Property(x => x.Tipo).HasColumnName("tipo");
        b.Property(x => x.CriadaEm).HasColumnName("criada_em");
        b.Property(x => x.Proveniencia).HasColumnName("proveniencia").HasColumnType("jsonb")
            .HasConversion(x => ConhecimentoConfiguration.Serializar(x), x => JsonSerializer.Deserialize<ProvenienciaDoConhecimento>(x, (JsonSerializerOptions?)null)!);
        b.Property(x => x.IdConhecimentoEscolhido).HasColumnName("id_conhecimento_escolhido");
        b.Property(x => x.ResolvidaEm).HasColumnName("resolvida_em");
        b.Property(x => x.ProvenienciaDaResolucao).HasColumnName("proveniencia_da_resolucao").HasColumnType("jsonb")
            .HasConversion(x => ConhecimentoConfiguration.Serializar(x), x => JsonSerializer.Deserialize<ProvenienciaDoConhecimento>(x, (JsonSerializerOptions?)null));
        b.HasOne<Conhecimento>().WithMany().HasForeignKey(x => new { x.IdConhecimentoEscolhido, x.IdEspacoDeConhecimento })
            .HasPrincipalKey(x => new { x.Id, x.IdEspacoDeConhecimento }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.IdEspacoDeConhecimento, x.IdOrigem, x.IdDestino, x.Tipo }).IsUnique();
        b.HasIndex(x => new { x.IdEspacoDeConhecimento, x.IdDestino });
        b.HasOne<Conhecimento>().WithMany().HasForeignKey(x => new { x.IdOrigem, x.IdEspacoDeConhecimento })
            .HasPrincipalKey(x => new { x.Id, x.IdEspacoDeConhecimento }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Conhecimento>().WithMany().HasForeignKey(x => new { x.IdDestino, x.IdEspacoDeConhecimento })
            .HasPrincipalKey(x => new { x.Id, x.IdEspacoDeConhecimento }).OnDelete(DeleteBehavior.Restrict);
    }
}
