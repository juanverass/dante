using Dante.Domain.Agentes;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Agentes;

// Cadastro de agentes (AD-68) no schema da orquestração. O handle é único entre todos, arquivados inclusive, e um
// trigger da migration recusa trocá-lo; não há remoção física pelo caso de uso.
public sealed class AgenteDbMapping : EntidadeConfiguration<Agente>
{
    public override void Configure(EntityTypeBuilder<Agente> b)
    {
        base.Configure(b);
        b.ToTable("agentes", t =>
        {
            t.HasCheckConstraint("ck_agentes_handle", "handle ~ '^[a-z][a-z0-9-]{1,30}$'");
            t.HasCheckConstraint("ck_agentes_versao_atual", "numero_da_versao_atual >= 0");
        });
        b.Property(x => x.Handle).HasColumnName("handle").HasMaxLength(Agente.TamanhoMaximoDoHandle);
        b.Property(x => x.Nome).HasColumnName("nome").HasMaxLength(Agente.TamanhoMaximoDoNome);
        b.Property(x => x.Descricao).HasColumnName("descricao").HasMaxLength(Agente.TamanhoMaximoDaDescricao);
        b.Property(x => x.Estado).HasColumnName("estado");
        b.Property(x => x.IdVersaoAtual).HasColumnName("id_versao_atual");
        b.Property(x => x.NumeroDaVersaoAtual).HasColumnName("numero_da_versao_atual");
        b.Property(x => x.IdDoCriador).HasColumnName("id_do_criador");
        b.Property(x => x.CriadoEm).HasColumnName("criado_em");
        b.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em");
        b.HasIndex(x => x.Handle).IsUnique();
        b.HasIndex(x => x.Estado);
    }
}
