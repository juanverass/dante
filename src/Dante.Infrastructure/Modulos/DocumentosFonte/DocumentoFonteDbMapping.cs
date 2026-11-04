using Dante.Domain.DocumentosFonte;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
        b.Property(x => x.Autoria).HasColumnName("autoria");
        // Metadados da #257; documentos anteriores ficam null/vazio.
        b.Property(x => x.Titulo).HasColumnName("titulo").HasMaxLength(DocumentoFonte.TamanhoMaximoDoTitulo);
        b.Property(x => x.Tags).HasColumnName("tags").HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]")
            .HasConversion(x => x.ToArray(), x => Array.AsReadOnly(x))
            .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<string>>(
                (a, c) => a!.SequenceEqual(c!), x => x.Aggregate(0, (h, t) => HashCode.Combine(h, t.GetHashCode())), x => Array.AsReadOnly(x.ToArray())));
        b.Property(x => x.Justificativa).HasColumnName("justificativa").HasMaxLength(DocumentoFonte.TamanhoMaximoDaJustificativa);
        b.Property(x => x.Proveniencia).HasColumnName("proveniencia").HasMaxLength(DocumentoFonte.TamanhoMaximoDaProveniencia);
        // Detecção de evidências com o mesmo conteúdo no escopo; não impõe unicidade.
        b.HasIndex(x => new { x.IdEspacoDeConhecimento, x.IdProjeto, x.Hash });
        b.HasIndex(x => new { x.IdEspacoDeConhecimento, x.IdProjeto, x.Origem }).IsUnique().AreNullsDistinct(false);
        b.HasOne<EspacoDeConhecimento>().WithMany().HasForeignKey(x => x.IdEspacoDeConhecimento).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Projeto>().WithMany().HasForeignKey(x => new { x.IdProjeto, x.IdEspacoDeConhecimento }).HasPrincipalKey(x => new { x.Id, x.IdEspacoDeConhecimento }).OnDelete(DeleteBehavior.Restrict);
    }
}
