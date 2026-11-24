namespace Dante.Application.IntegracaoDeCi;

// Regra pura que transforma a resposta do provedor no check agregado de um head SHA (AD-76):
// 1. toda execução tem de ser do repositório consultado (alias, conexão, nome e um único ID): senão a resposta inteira
//    é recusada como Inconsistente, nunca filtrada em silêncio;
// 2. execução de outro SHA é descartada: um run antigo nunca é diagnosticado como sendo do head atual;
// 3. de cada execução vale só a maior tentativa; de cada workflow e gatilho, só a execução mais recente;
// 4. o estado agregado segue a precedência documentada; sucesso exige resultado completo e todas as execuções
//    relevantes explicitamente em Sucesso.
public static class ConsolidacaoDoCheckDeCi
{
    public static CheckAgregadoDeCiDto Consolidar(ConsultaDeCi consulta, ResultadoDaConsultaDeCi resultado)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        ArgumentNullException.ThrowIfNull(resultado);
        ExigirMesmoRepositorio(consulta, resultado.Execucoes);

        var doHead = resultado.Execucoes.Where(e => e.Identidade.Sha == consulta.ShaDoHead).ToArray();
        var vigentes = doHead
            .GroupBy(e => e.Identidade.IdDaExecucao)
            .Select(g => g.MaxBy(e => e.Identidade.Tentativa)!)
            .GroupBy(e => (e.Identidade.IdDoWorkflow, e.Gatilho))
            .Select(g => g.OrderByDescending(e => e.CriadaEm).ThenByDescending(e => e.Identidade.IdDaExecucao).First())
            .OrderBy(e => e.NomeDoWorkflow, StringComparer.Ordinal).ThenBy(e => e.Identidade.IdDaExecucao)
            .ToArray();

        var estado = Agregar(vigentes, resultado);
        return new CheckAgregadoDeCiDto
        {
            Consulta = consulta,
            Estado = estado,
            ObtidoEm = resultado.ObtidoEm,
            Execucoes = vigentes,
            Descartadas = resultado.Execucoes.Count - vigentes.Length,
            Completo = resultado.Completo,
            MotivoDaIndisponibilidade = estado == EstadoDoCheckDeCi.Indisponivel ? MotivoDaFalhaNaConsultaDeCi.Indisponivel : null
        };
    }

    // A consulta falhou: o check diz que o D.A.N.T.E. não sabe, nunca que o CI falhou.
    public static CheckAgregadoDeCiDto Indisponivel(ConsultaDeCi consulta, FalhaNaConsultaDeCiException falha,
        DateTimeOffset em)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        ArgumentNullException.ThrowIfNull(falha);
        return new CheckAgregadoDeCiDto
        {
            Consulta = consulta,
            Estado = falha.Motivo is MotivoDaFalhaNaConsultaDeCi.NaoConfigurada or MotivoDaFalhaNaConsultaDeCi.NaoAutorizada
                or MotivoDaFalhaNaConsultaDeCi.SemPermissao or MotivoDaFalhaNaConsultaDeCi.NaoEncontrada
                ? EstadoDoCheckDeCi.SemPermissao
                : EstadoDoCheckDeCi.Indisponivel,
            ObtidoEm = em,
            Completo = false,
            MotivoDaIndisponibilidade = falha.Motivo
        };
    }

    // Precedência: enquanto algo não terminou, o check não terminou; terminado, Falha > Expirado > Cancelado > Sucesso.
    // Ignoradas não contam. Sucesso sobre resultado incompleto vira Indisponivel: uma falha pode estar na página
    // que não foi lida. Falha já observada continua sendo falha mesmo incompleta.
    private static EstadoDoCheckDeCi Agregar(IReadOnlyList<ExecucaoDeCiDto> vigentes, ResultadoDaConsultaDeCi resultado)
    {
        var relevantes = vigentes.Where(e => e.Estado != EstadoDaExecucao.Ignorada).Select(e => e.Estado).ToArray();
        if (relevantes.Length == 0)
            return !resultado.Completo ? EstadoDoCheckDeCi.Indisponivel
                : resultado.PossuiWorkflowsConfigurados || vigentes.Count > 0 ? EstadoDoCheckDeCi.SemExecucao
                : EstadoDoCheckDeCi.SemWorkflowConfigurado;

        if (relevantes.Any(e => !TransicoesDaExecucaoDeCi.Final(e)))
            return relevantes.Contains(EstadoDaExecucao.EmAndamento) ? EstadoDoCheckDeCi.EmAndamento
                : relevantes.Contains(EstadoDaExecucao.AguardandoRunner) ? EstadoDoCheckDeCi.AguardandoRunner
                : EstadoDoCheckDeCi.NaFila;

        if (relevantes.Contains(EstadoDaExecucao.Falha)) return EstadoDoCheckDeCi.Falha;
        if (relevantes.Contains(EstadoDaExecucao.Expirada)) return EstadoDoCheckDeCi.Expirado;
        if (relevantes.Contains(EstadoDaExecucao.Cancelada)) return EstadoDoCheckDeCi.Cancelado;
        // Sucesso só por afirmação explícita de todas as relevantes, nunca por exclusão das outras finais.
        return resultado.Completo && relevantes.All(e => e == EstadoDaExecucao.Sucesso)
            ? EstadoDoCheckDeCi.Sucesso
            : EstadoDoCheckDeCi.Indisponivel;
    }

    private static void ExigirMesmoRepositorio(ConsultaDeCi consulta, IReadOnlyList<ExecucaoDeCiDto> execucoes)
    {
        var ids = new HashSet<long>();
        foreach (var execucao in execucoes)
        {
            var repositorio = execucao.Identidade.Repositorio;
            if (!consulta.Repositorio.Corresponde(repositorio)) throw Inconsistente();
            ids.Add(repositorio.IdNoProvedor!.Value);
            CiValidator.ExigirUrlOficial(execucao.UrlOficial, nameof(execucao.UrlOficial));
            foreach (var job in execucao.Jobs) CiValidator.ExigirUrlOficial(job.UrlOficial, nameof(job.UrlOficial));
        }

        if (ids.Count > 1) throw Inconsistente();
    }

    private static FalhaNaConsultaDeCiException Inconsistente() => new(MotivoDaFalhaNaConsultaDeCi.Inconsistente,
        "O provedor devolveu execução de outro repositório ou conexão; o resultado foi descartado.");
}
