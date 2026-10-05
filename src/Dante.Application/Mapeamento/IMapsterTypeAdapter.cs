namespace Dante.Application.Mapeamento;

// Não oferece adaptação sobre uma entidade existente: atualização passa pelo domínio.
public interface IMapsterTypeAdapter
{
    TDestino Mapear<TOrigem, TDestino>(TOrigem origem) where TOrigem : notnull;
}
