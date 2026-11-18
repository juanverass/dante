using Dante.Domain.Grupos;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Grupos;

// Grupos de agentes (AD-69) no schema da orquestração. O nome é único entre todos, arquivados inclusive; triggers da
// migration recusam trocá-lo, remover o grupo e apontar a versão ativa para algo que não tenha uma decisão de ativação
// gravada com o hash daquela versão.
public sealed class GrupoDeAgentesDbMapping : EntidadeConfiguration<GrupoDeAgentes>
{
    public override void Configure(EntityTypeBuilder<GrupoDeAgentes> b)
    {
        base.Configure(b);
        b.ToTable("grupos_de_agentes", t =>
        {
            t.HasCheckConstraint("ck_grupos_de_agentes_nome", "nome ~ '^[a-z][a-z0-9-]{1,30}$'");
            t.HasCheckConstraint("ck_grupos_de_agentes_versao_atual", "numero_da_versao_atual >= 0");
            t.HasCheckConstraint("ck_grupos_de_agentes_versao_ativa",
                "(id_versao_ativa IS NULL) = (numero_da_versao_ativa IS NULL) AND " +
                "(numero_da_versao_ativa IS NULL OR numero_da_versao_ativa BETWEEN 1 AND numero_da_versao_atual)");
        });
        b.Property(x => x.Nome).HasColumnName("nome").HasMaxLength(GrupoDeAgentes.TamanhoMaximoDoNome);
        b.Property(x => x.Descricao).HasColumnName("descricao").HasMaxLength(GrupoDeAgentes.TamanhoMaximoDaDescricao);
        b.Property(x => x.Estado).HasColumnName("estado");
        b.Property(x => x.IdVersaoAtual).HasColumnName("id_versao_atual");
        b.Property(x => x.NumeroDaVersaoAtual).HasColumnName("numero_da_versao_atual");
        b.Property(x => x.IdVersaoAtiva).HasColumnName("id_versao_ativa");
        b.Property(x => x.NumeroDaVersaoAtiva).HasColumnName("numero_da_versao_ativa");
        b.Property(x => x.IdDoCriador).HasColumnName("id_do_criador");
        b.Property(x => x.CriadoEm).HasColumnName("criado_em");
        b.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em");
        b.HasIndex(x => x.Nome).IsUnique();
        b.HasIndex(x => x.Estado);
    }
}
