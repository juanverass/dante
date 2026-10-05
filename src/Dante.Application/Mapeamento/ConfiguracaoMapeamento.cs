using System.Linq.Expressions;
using Mapster;

namespace Dante.Application.Mapeamento;

// Um contrato por par/direção, sem descoberta automática ou configuração estática global.
public sealed class ConfiguracaoMapeamento
{
    private readonly TypeAdapterConfig configuracao = new() { RequireExplicitMapping = true };
    private readonly HashSet<(Type Origem, Type Destino)> pares = [];
    private bool concluida;

    public void Registrar<TOrigem, TDestino>(Expression<Func<TOrigem, TDestino>> conversao)
    {
        ArgumentNullException.ThrowIfNull(conversao);
        if (concluida) throw new InvalidOperationException("Configuração de mappings já concluída.");
        if (!pares.Add((typeof(TOrigem), typeof(TDestino))))
            throw new InvalidOperationException("Mapping duplicado para o mesmo par/direção.");
        // A expressão define todos os campos/constructors/métodos permitidos.
        // Nenhum membro com nome correspondente é copiado implicitamente.
        configuracao.NewConfig<TOrigem, TDestino>().MapWith(conversao);
    }

    internal IMapsterTypeAdapter Concluir()
    {
        if (concluida) throw new InvalidOperationException("Configuração de mappings já concluída.");
        configuracao.Compile();
        concluida = true;
        return new MapsterTypeAdapter(configuracao, pares.ToHashSet());
    }

    private sealed class MapsterTypeAdapter(TypeAdapterConfig configuracao,
        IReadOnlySet<(Type Origem, Type Destino)> pares) : IMapsterTypeAdapter
    {
        public TDestino Mapear<TOrigem, TDestino>(TOrigem origem) where TOrigem : notnull
        {
            ArgumentNullException.ThrowIfNull(origem);
            if (!pares.Contains((typeof(TOrigem), typeof(TDestino))))
                throw new InvalidOperationException("Mapping não registrado para o par/direção solicitado.");
            return configuracao.GetMapFunction<TOrigem, TDestino>()(origem);
        }
    }
}
