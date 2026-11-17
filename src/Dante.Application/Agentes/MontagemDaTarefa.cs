using Dante.Application.Contextos;
using Dante.Application.Execucoes;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;

namespace Dante.Application.Agentes;

// Monta a execução Tarefa de /tarefa <handle> [@alias] <texto> (AD-68). Sem o marcador explícito de escrita, a tarefa é
// somente leitura: perfil efetivo plan mesmo que a versão permita escrita, e uma única etapa de turno sem Escrita,
// retomável automaticamente (leitura é idempotente). Pode ler o checkout principal; nunca escreve nele.
// Com /tarefa <handle> @alias escrever <texto> (#307), MontarEscrita exige versão com implementar e escrita, perfil padrão
// acima de plan (o perfil efetivo é ele, sem escalonamento), modo repositório e driver com confinamento de filesystem
// comprovado (ConfinamentoDeEscrita). A etapa é de escrita, retomada só com confirmação, e roda numa worktree por
// repositório envolvido (principal e vinculados), nunca no checkout principal.
public static class MontagemDaTarefa
{
    public const AgentPermissionProfile PerfilEfetivo = AgentPermissionProfile.Plan;

    // Palavra que pede escrita, logo depois do @alias.
    public const string MarcadorDeEscrita = "escrever";

    public const string AvisoDeLeitura =
        "Esta tarefa roda em modo somente leitura (plan): o agente lê e analisa, mas não altera arquivos nem executa " +
        "ações de escrita. Para alterar arquivos, peça explicitamente: /tarefa <handle> @alias escrever <pedido>.";

    public const string AvisoDeEscrita =
        "Esta tarefa escreve numa worktree Git exclusiva desta execução, criada pelo D.A.N.T.E. a partir do checkout " +
        "principal. O sandbox da CLI permite gravar só nas worktrees listadas; o diretório Git comum é somente leitura, " +
        "então commit, push e alteração de branches não estão disponíveis: deixe as alterações na worktree.";

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

    // Pedido explícito de escrita (#307). Recusa com ArgumentException e mensagem exibível; nada é enfileirado.
    public static PlanoDaTarefa MontarEscrita(string handle, VersaoDoAgenteDto versao, JobExecutionContext contexto,
        string pedido)
    {
        ArgumentNullException.ThrowIfNull(versao);
        ArgumentNullException.ThrowIfNull(contexto);
        var configuracao = versao.Configuracao;
        if (contexto.Mode != JobExecutionMode.Repository)
            throw new ArgumentException("Tarefa de escrita exige um repositório: /tarefa <handle> @alias escrever <pedido>.");
        if (!configuracao.PodeEscrever || !configuracao.Capacidades.Contains(CapacidadeDoAgente.Implementar))
            throw new ArgumentException($"O agente {handle} não tem a capacidade implementar com escrita; a tarefa não foi criada.");
        if (configuracao.PerfilPadrao == AgentPermissionProfile.Plan)
            throw new ArgumentException($"O perfil padrão do agente {handle} é plan; uma tarefa de escrita não escala o perfil.");
        if (ConfinamentoDeEscrita.Recusa(configuracao.Driver) is { } recusa) throw new ArgumentException(recusa);
        var leitura = Montar(handle, versao, contexto, pedido);
        return leitura with
        {
            Perfil = configuracao.PerfilPadrao,
            Escrita = true,
            Etapas = [new EtapaPlanejada(1, TipoDeEtapa.TurnoDeAgente, Escrita: true, PoliticaDeRetomada.ComConfirmacao)]
        };
    }

    // As entidades que a fila durável (#300) grava, na mesma transação do trabalho que as despacha. A execução guarda a
    // versão exata do agente; a etapa, driver, modelo e esforço dessa versão. Só leitura ou a escrita exata de MontarEscrita.
    public static (Execucao Execucao, IReadOnlyList<EtapaDaExecucao> Etapas) CriarExecucao(PlanoDaTarefa plano,
        long numero, string canal, long idDoUsuarioNoCanal, DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(plano);
        if (!plano.SomenteLeitura && !plano.EscritaValida) throw new EscritaSemWorktreeException("plano de escrita inválido");
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
