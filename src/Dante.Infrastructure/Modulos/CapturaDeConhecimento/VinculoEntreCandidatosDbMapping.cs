using System.Text.Json;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;
using Dante.Domain.RelacoesDeConhecimento;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Dante.Infrastructure.Modulos.Conhecimentos;
using Dante.Infrastructure.Persistence;
namespace Dante.Infrastructure.Modulos.CapturaDeConhecimento;

public sealed class VinculoEntreCandidatosDbMapping : EntidadeConfiguration<VinculoEntreCandidatos>
{
    public override void Configure(EntityTypeBuilder<VinculoEntreCandidatos> b)
    {
        base.Configure(b); b.ToTable("vinculos_entre_candidatos");
        b.Property(x => x.IdEspacoDeConhecimento).HasColumnName("id_espaco_de_conhecimento");
        b.Property(x => x.IdProjeto).HasColumnName("id_projeto");
        b.Property(x => x.IdCandidatoIncidente).HasColumnName("id_candidato_incidente");
        b.Property(x => x.IdCandidatoSolucao).HasColumnName("id_candidato_solucao");
        b.Property(x => x.IdConhecimentoIncidente).HasColumnName("id_conhecimento_incidente");
        b.Property(x => x.IdConhecimentoSolucao).HasColumnName("id_conhecimento_solucao");
        b.Property(x => x.RevisaoConhecimentoIncidente).HasColumnName("revisao_conhecimento_incidente");
        b.Property(x => x.RevisaoConhecimentoSolucao).HasColumnName("revisao_conhecimento_solucao");
        b.Property(x => x.IdRelacao).HasColumnName("id_relacao");
        b.Property(x => x.Estado).HasColumnName("estado");
        b.Property(x => x.MotivoDoEncerramento).HasColumnName("motivo_do_encerramento").HasMaxLength(500);
        b.Property(x => x.Proveniencia).HasColumnName("proveniencia").HasColumnType("jsonb")
            .HasConversion(x => ConhecimentoDbMapping.Serializar(x), x => JsonSerializer.Deserialize<ProvenienciaDoConhecimento>(x, (JsonSerializerOptions?)null)!);
        b.Property(x => x.ProvenienciaDoEncerramento).HasColumnName("proveniencia_do_encerramento").HasColumnType("jsonb")
            .HasConversion(x => ConhecimentoDbMapping.Serializar(x), x => JsonSerializer.Deserialize<ProvenienciaDoConhecimento>(x, (JsonSerializerOptions?)null));
        b.Property(x => x.CriadoEm).HasColumnName("criado_em");
        b.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em");
        // Uma intenção por par; repetir o pedido devolve a existente.
        b.HasIndex(x => new { x.IdEspacoDeConhecimento, x.IdCandidatoIncidente, x.IdCandidatoSolucao }).IsUnique();
        b.HasIndex(x => new { x.IdEspacoDeConhecimento, x.IdCandidatoSolucao });
        b.HasOne<EspacoDeConhecimento>().WithMany().HasForeignKey(x => x.IdEspacoDeConhecimento).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Projeto>().WithMany().HasForeignKey(x => new { x.IdProjeto, x.IdEspacoDeConhecimento })
            .HasPrincipalKey(x => new { x.Id, x.IdEspacoDeConhecimento }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CandidatoDeConhecimento>().WithMany().HasForeignKey(x => x.IdCandidatoIncidente).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CandidatoDeConhecimento>().WithMany().HasForeignKey(x => x.IdCandidatoSolucao).OnDelete(DeleteBehavior.Restrict);
        foreach (var propriedade in new[] { nameof(VinculoEntreCandidatos.IdConhecimentoIncidente), nameof(VinculoEntreCandidatos.IdConhecimentoSolucao) })
            b.HasOne<Conhecimento>().WithMany().HasForeignKey(propriedade, nameof(VinculoEntreCandidatos.IdEspacoDeConhecimento))
                .HasPrincipalKey(nameof(Conhecimento.Id), nameof(Conhecimento.IdEspacoDeConhecimento)).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RelacaoDeConhecimento>().WithMany().HasForeignKey(x => x.IdRelacao).OnDelete(DeleteBehavior.Restrict);
    }
}
