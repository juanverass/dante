namespace Dante.Application.Comum;

// Checks estruturais de entrada reutilizados pelas features (#205): só inspecionam valores recebidos, sem
// repository, estado persistido ou policy. Regra que depende de estado continua no AppService ou no Domain.
internal static class ValidacaoDeEntrada
{
    // Faixa inclusiva de limites, deslocamentos e profundidades.
    internal static void ExigirFaixa(int valor, int minimo, int maximo, string parametro)
    {
        if (valor < minimo || valor > maximo) throw new ArgumentOutOfRangeException(parametro);
    }

    internal static void ExigirId(Guid id, string mensagem, string? parametro = null)
    {
        if (id == Guid.Empty) throw new ArgumentException(mensagem, parametro);
    }

    // Espaço obrigatório; projeto opcional, mas nunca Guid.Empty.
    internal static void ExigirEscopo(Guid idEspaco, Guid? idProjeto)
    {
        if (idEspaco == Guid.Empty || idProjeto == Guid.Empty) throw new ArgumentException("Escopo inválido.");
    }
}
