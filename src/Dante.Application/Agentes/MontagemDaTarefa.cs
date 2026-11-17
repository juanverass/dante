using Dante.Application.Contextos;
using Dante.Application.Execucoes;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;

namespace Dante.Application.Agentes;

// Monta a execução Tarefa de /tarefa <handle> [@alias] <texto> (AD-68). Até existirem worktrees reservadas, toda
// tarefa é somente leitura: perfil efetivo plan mesmo que a versão permita escrita, e uma única etapa de turno sem
// Escrita, retomável automaticamente (leitura é idempotente). Pode ler o checkout principal; nunca escreve nele.
public static class MontagemDaTarefa
{
    public const AgentPermissionProfile PerfilEfetivo = AgentPermissionProfile.Plan;

    public const string AvisoDeLeitura =
        "Esta tarefa roda em modo somente leitura (plan): o agente lê e analisa, mas não altera arquivos nem executa " +
        "ações de escrita. Tarefas com escrita dependem de worktree reservada e ainda não estão disponíveis.";

    public static PlanoDaTarefa Montar(string handle, VersaoDoAgenteDto versao, JobExecutionContext contexto,
        string pedido)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        ArgumentNullException.ThrowIfNull(versao);
        ArgumentNullException.ThrowIfNull(contexto);
        if (string.IsNullOrWhiteSpace(pedido)) throw new ArgumentException("A tarefa precisa de um pedido.");
        if (AgentesEmbutidos.TryObter(handle, out _))
            throw new ArgumentException("Tarefas usam um agente registrado; para pedidos avulsos use /claude ou /codex.");
        var configuracao = versao.Configuracao;
        if (contexto.Mode == JobExecutionMode.Repository && !ReposPermitidos(configuracao, contexto))
            throw new ArgumentException(RecusaDeRepositorio(handle, configuracao, contexto));
        var objetivo = pedido.Trim();
        objetivo = ProtecaoDeSegredos.ContemSegredo(objetivo) ? "[conteúdo protegido]"
            : objetivo.Length <= Execucao.TamanhoMaximoDoObjetivo ? objetivo : objetivo[..Execucao.TamanhoMaximoDoObjetivo];
        return new PlanoDaTarefa(handle, versao, contexto, pedido.Trim(), objetivo, PerfilEfetivo,
            [new EtapaPlanejada(1, TipoDeEtapa.TurnoDeAgente, Escrita: false, PoliticaDeRetomada.Automatica)]);
    }

    // As entidades que a fila durável (#300) grava, na mesma transação do trabalho que as despacha. A execução guarda a
    // versão exata do agente; a etapa, driver, modelo e esforço dessa versão. Escrita aqui seria recusada pelo domínio.
    public static (Execucao Execucao, IReadOnlyList<EtapaDaExecucao> Etapas) CriarExecucao(PlanoDaTarefa plano,
        long numero, string canal, long idDoUsuarioNoCanal, DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(plano);
        if (!plano.SomenteLeitura) throw new EscritaSemWorktreeException();
        var execucao = new Execucao(numero, TipoDeExecucao.Tarefa, canal, idDoUsuarioNoCanal,
            LedgerDeExecucoesAppService.SerializarContexto(plano.Contexto), plano.ObjetivoRegistrado, agora,
            plano.Versao.Id);
        var configuracao = plano.Versao.Configuracao;
        var etapas = plano.Etapas.Select(etapa => new EtapaDaExecucao(execucao, etapa.Numero, etapa.Tipo, etapa.Escrita,
            etapa.PoliticaDeRetomada, agora, configuracao.Driver, configuracao.Modelo, configuracao.Esforco)).ToArray();
        return (execucao, etapas);
    }

    // Repositórios que a versão concede: o principal e todos os vinculados (AD-58), ou qualquer um com "*".
    public static bool ReposPermitidos(ConfiguracaoDoAgente configuracao, JobExecutionContext contexto) =>
        contexto.Mode != JobExecutionMode.Repository ||
        contexto.RepositoriosVinculados.Select(vinculado => vinculado.Alias).Prepend(contexto.RepositoryAlias!)
            .All(configuracao.PermiteRepositorio);

    public static string RecusaDeRepositorio(string handle, ConfiguracaoDoAgente configuracao, JobExecutionContext contexto)
    {
        var negados = contexto.RepositoriosVinculados.Select(vinculado => vinculado.Alias).Prepend(contexto.RepositoryAlias!)
            .Where(alias => !configuracao.PermiteRepositorio(alias)).ToArray();
        return $"O agente {handle} não tem concessão para {string.Join(", ", negados)}. " +
            (configuracao.Repositorios.Count == 0 ? "Ele só trabalha no workspace geral."
                : $"Repositórios concedidos: {string.Join(", ", configuracao.Repositorios)}.");
    }
}
