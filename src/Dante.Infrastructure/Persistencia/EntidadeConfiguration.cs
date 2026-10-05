using Dante.Domain.Comum;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Persistencia;

// Configurations concretas chamam base.Configure e definem tabela/colunas em snake_case PT-BR.
public abstract class EntidadeConfiguration<TEntity> : IEntityTypeConfiguration<TEntity>
    where TEntity : EntidadeBase
{
    public virtual void Configure(EntityTypeBuilder<TEntity> builder)
    {
        builder.HasKey(entidade => entidade.Id);
        builder.Property(entidade => entidade.Id).HasColumnName("id").ValueGeneratedNever();
        // Token técnico shadow: o domínio não depende de xmin nem do provider.
        builder.Property<uint>("Versao").IsRowVersion();
    }
}
