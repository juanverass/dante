namespace Dante.Domain.Comum;

// Base de toda entidade persistente (AD-36): identidade Guid gerada pelo domínio, sem TId. O setter protegido
// impede que DTO, mapping ou host redefinam a identidade de fora da entidade.
public abstract class EntidadeBase
{
    public Guid Id { get; protected set; } = Guid.NewGuid();
}
