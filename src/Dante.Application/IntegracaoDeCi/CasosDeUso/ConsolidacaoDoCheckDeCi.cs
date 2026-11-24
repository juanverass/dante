namespace Dante.Application.IntegracaoDeCi;

// Regra pura que transforma a resposta do provedor no check agregado de um head SHA (AD-76):
// 1. toda execução tem de ser do repositório consultado (alias, conexão, nome e um único ID): senão a resposta inteira
//    é recusada como Inconsistente, nunca filtrada em silêncio;
// 2. execução de outro SHA é descartada: um run antigo nunca é diagnosticado como sendo do head atual;
// 3. de cada execução vale só a maior tentativa;
// 4. com número de PR na consulta (#238), só conta a tentativa vigente que o provedor associa a esse PR: associada só a
//    outro PR é descartada; sem associação nenhuma não conta e fica em SemAssociacaoAoPr. A associação é lida na
//    tentativa vigente, nunca numa substituída: o provedor não garante que ela seja a mesma entre tentativas. O mesmo SHA
//    pode estar em dois PRs ou ter execuções de outros gatilhos, e o SHA sozinho não diz que a execução é do PR;
// 5. de cada workflow e gatilho, só a execução mais recente entre as que contam: a escolha vem depois da associação,
//    porque dois PRs com o mesmo SHA têm execuções próprias do mesmo workflow e uma não substitui a do outro;
// 6. o estado agregado segue a precedência documentada; sucesso exige resultado completo e todas as execuções
//    relevantes explicitamente em Sucesso. Sem nenhuma execução associada ao PR, mas com execuções do SHA sem
//    associação, o check é Indisponivel: não se sabe se são do PR, então não é sucesso, falha nem "sem execução".
public static class ConsolidacaoDoCheckDeCi
{
    public static CheckAgregadoDeCiDto Consolidar(ConsultaDeCi consulta, ResultadoDaConsultaDeCi resultado)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        ArgumentNullException.ThrowIfNull(resultado);
        ExigirMesmoRepositorio(consulta, resultado.Execucoes);

        // Primeiro a tentativa vigente de cada execução; só ela é classificada pela associação ao PR.
        var tentativasVigentes = resultado.Execucoes
            .Where(e => e.Identidade.Sha == consulta.ShaDoHead)
            .GroupBy(e => e.Identidade.IdDaExecucao)
            .Select(g => g.MaxBy(e => e.Identidade.Tentativa)!)
            .ToArray();
        var semAssociacao = 0;
        if (consulta.NumeroDoPr is { } numeroDoPr)
        {
            semAssociacao = tentativasVigentes.Count(e => e.NumerosDePr.Count == 0);
            tentativasVigentes = tentativasVigentes.Where(e => e.NumerosDePr.Contains(numeroDoPr)).ToArray();
        }

        var vigentes = tentativasVigentes
            .GroupBy(e => (e.Identidade.IdDoWorkflow, e.Gatilho))
            .Select(g => g.OrderByDescending(e => e.CriadaEm).ThenByDescending(e => e.Identidade.IdDaExecucao).First())
            .OrderBy(e => e.NomeDoWorkflow, StringComparer.Ordinal).ThenBy(e => e.Identidade.IdDaExecucao)
            .ToArray();

        var estado = Agregar(vigentes, resultado, semAssociacao);
        return new CheckAgregadoDeCiDto
        {
            Consulta = consulta,
            Estado = estado,
            ObtidoEm = resultado.ObtidoEm,
            Execucoes = vigentes,
            Descartadas = resultado.Execucoes.Count - vigentes.Length - semAssociacao,
            SemAssociacaoAoPr = semAssociacao,
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
    private static EstadoDoCheckDeCi Agregar(IReadOnlyList<ExecucaoDeCiDto> vigentes, ResultadoDaConsultaDeCi resultado,
        int semAssociacao)
    {
        var relevantes = vigentes.Where(e => e.Estado != EstadoDaExecucao.Ignorada).Select(e => e.Estado).ToArray();
        if (relevantes.Length == 0)
            return !resultado.Completo || semAssociacao > 0 ? EstadoDoCheckDeCi.Indisponivel
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
