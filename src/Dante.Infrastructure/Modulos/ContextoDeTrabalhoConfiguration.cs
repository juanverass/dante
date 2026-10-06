using System.Text.Json;
using Dante.Domain.ContextosDeTrabalho;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Dante.Infrastructure.Persistence;
namespace Dante.Infrastructure.Modulos;
public sealed class ContextoDeTrabalhoConfiguration : EntidadeConfiguration<ContextoDeTrabalho>
{
    private static string Serializar<T>(T valor) => JsonSerializer.Serialize(valor);
    private static DadosDoContexto LerDados(string valor) => JsonSerializer.Deserialize<DadosDoContexto>(valor)!;
    public override void Configure(EntityTypeBuilder<ContextoDeTrabalho> b)
    {
        base.Configure(b); b.ToTable("contextos_de_trabalho");
        b.Property(x => x.IdEspacoDeConhecimento).HasColumnName("id_espaco_de_conhecimento");
        b.Property(x => x.IdProjeto).HasColumnName("id_projeto");
        b.Property(x => x.Dados).HasColumnName("dados").HasColumnType("jsonb").HasConversion(x => Serializar(x), x => LerDados(x))
            .Metadata.SetValueComparer(new ValueComparer<DadosDoContexto>((a,c) => Serializar(a) == Serializar(c), x => Serializar(x).GetHashCode(), x => LerDados(Serializar(x))));
        b.Property(x => x.AuditoriaAnterior).HasColumnName("auditoria_anterior").HasColumnType("jsonb")
            .HasConversion(x => Serializar(x), x => JsonSerializer.Deserialize<AuditoriaDoContexto>(x));
        b.Property(x => x.Sensibilidade).HasColumnName("sensibilidade");
        b.Property(x => x.Revisao).HasColumnName("revisao"); b.Property(x => x.IdResponsavel).HasColumnName("id_responsavel");
        b.Property(x => x.Origem).HasColumnName("origem").HasMaxLength(200);
        b.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em"); b.Property(x => x.ExpiraEm).HasColumnName("expira_em");
        b.HasIndex(x => new { x.IdEspacoDeConhecimento, x.IdProjeto }).IsUnique().AreNullsDistinct(false);
        b.HasOne<EspacoDeConhecimento>().WithMany().HasForeignKey(x => x.IdEspacoDeConhecimento).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Projeto>().WithMany().HasForeignKey(x => new { x.IdProjeto, x.IdEspacoDeConhecimento }).HasPrincipalKey(x => new { x.Id, x.IdEspacoDeConhecimento }).OnDelete(DeleteBehavior.Restrict);
    }
}
