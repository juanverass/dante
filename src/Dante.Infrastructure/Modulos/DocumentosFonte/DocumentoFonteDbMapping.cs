using Dante.Domain.DocumentosFonte;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Dante.Infrastructure.Persistence;
namespace Dante.Infrastructure.Modulos.DocumentosFonte;
public sealed class DocumentoFonteDbMapping : EntidadeConfiguration<DocumentoFonte>
{
    public override void Configure(EntityTypeBuilder<DocumentoFonte> b)
    {
        base.Configure(b); b.ToTable("documentos_fonte");
        b.Property(x => x.IdEspacoDeConhecimento).HasColumnName("id_espaco_de_conhecimento");
        b.Property(x => x.IdProjeto).HasColumnName("id_projeto");
        b.Property(x => x.Origem).HasColumnName("origem").HasMaxLength(500);
        b.Property(x => x.Formato).HasColumnName("formato").HasMaxLength(32);
        b.Property(x => x.Conteudo).HasColumnName("conteudo"); b.Property(x => x.Hash).HasColumnName("hash").HasMaxLength(64);
        b.Property(x => x.Sensibilidade).HasColumnName("sensibilidade"); b.Property(x => x.Revisao).HasColumnName("revisao");
        b.Property(x => x.IdResponsavel).HasColumnName("id_responsavel"); b.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em");
        b.Property(x => x.Removido).HasColumnName("removido");
        b.HasIndex(x => new { x.IdEspacoDeConhecimento, x.IdProjeto, x.Origem }).IsUnique().AreNullsDistinct(false);
        b.HasOne<EspacoDeConhecimento>().WithMany().HasForeignKey(x => x.IdEspacoDeConhecimento).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Projeto>().WithMany().HasForeignKey(x => new { x.IdProjeto, x.IdEspacoDeConhecimento }).HasPrincipalKey(x => new { x.Id, x.IdEspacoDeConhecimento }).OnDelete(DeleteBehavior.Restrict);
    }
}
