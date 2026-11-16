using Dante.Application.Comum;
using Dante.Application.Execucoes;
using Dante.Application.Mapeamento;
using Dante.Domain.Agentes;

namespace Dante.Application.Agentes;

// Cadastro versionado de agentes (AD-68) sobre o banco da orquestração (AD-66): cada operação é uma transação.
// Modelo e esforço são conferidos no catálogo do driver ao salvar; o host confere de novo ao iniciar a sessão.
public sealed class CadastroDeAgentesAppService(
    IAgenteRepository agentes,
    IVersaoDoAgenteRepository versoes,
    IUnitOfWorkDaOrquestracao unidade,
    IMapsterTypeAdapter mapper,
    IAgentModelCatalog? catalogo = null,
    TimeProvider? relogio = null) : ICadastroDeAgentesAppService
{
    private readonly TimeProvider relogio = relogio ?? TimeProvider.System;

    public async Task<AgenteDto> CriarAsync(NovoAgenteDto novo, CancellationToken cancellationToken = default)
    {
        CadastroDeAgentesValidator.Validar(novo);
        var handle = CadastroDeAgentesValidator.Handle(novo.Handle);
        if (await agentes.ObterPorHandleAsync(handle, cancellationToken) is { } existente)
            throw new InvalidOperationException(existente.Estado == EstadoDoAgente.Arquivado
                ? $"O handle {handle} pertenceu a um agente arquivado e não é reutilizado."
                : $"Já existe um agente {handle}.");
        var entrada = novo.Configuracao;
        var (modelo, esforco) = await ConferirNoCatalogoAsync(entrada.Driver, entrada.Modelo, entrada.Esforco,
            cancellationToken);
        var configuracao = Configuracao(() => new ConfiguracaoDoAgente(entrada.Driver, entrada.PerfilPadrao,
            entrada.PerfilMaximo, entrada.PodeEscrever, modelo, esforco, entrada.Instrucoes, entrada.Capacidades,
            entrada.Ferramentas, entrada.Repositorios, entrada.Limites));
        var agora = relogio.GetUtcNow();
        var agente = Dominio(() => new Agente(handle, novo.Nome, novo.Descricao, novo.IdDoAutor, agora));
        var versao = agente.NovaVersao(configuracao, novo.IdDoAutor, agora);
        await agentes.AdicionarAsync(agente, cancellationToken);
        await versoes.AdicionarAsync(versao, cancellationToken);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
        return Dto(agente, versao);
    }

    public async Task<AgenteDto?> ObterAsync(string handle, CancellationToken cancellationToken = default)
    {
        if (AgentesEmbutidos.TryObter(handle?.Trim(), out var driver)) return AgentesEmbutidos.Descrever(driver);
        var agente = await agentes.ObterPorHandleAsync(CadastroDeAgentesValidator.Handle(handle), cancellationToken);
        if (agente is null) return null;
        var todas = (await versoes.ListarDoAgenteAsync(agente.Id, cancellationToken))
            .OrderByDescending(versao => versao.Numero).Select(mapper.Mapear<VersaoDoAgente, VersaoDoAgenteDto>).ToArray();
        return mapper.Mapear<Agente, AgenteDto>(agente) with
        {
            VersaoAtual = todas.FirstOrDefault(versao => versao.Id == agente.IdVersaoAtual),
            Versoes = todas
        };
    }

    public async Task<IReadOnlyList<AgenteDto>> ListarAsync(bool incluirArquivados = false,
        CancellationToken cancellationToken = default)
    {
        var registrados = await agentes.ListarAsync(incluirArquivados, cancellationToken);
        var lista = AgentesEmbutidos.Todos.Select(AgentesEmbutidos.Descrever).ToList();
        foreach (var agente in registrados.OrderBy(agente => agente.Handle, StringComparer.Ordinal))
            lista.Add(Dto(agente, agente.IdVersaoAtual is { } id ? await versoes.ObterPorIdAsync(id, cancellationToken) : null));
        return lista;
    }

    public async Task<AgenteDto> EditarAsync(string handle, AlteracaoDoAgenteDto alteracao, long idDoAutor,
        CancellationToken cancellationToken = default)
    {
        CadastroDeAgentesValidator.Validar(alteracao);
        var agente = await RegistradoAsync(handle, cancellationToken);
        if (agente.Estado == EstadoDoAgente.Arquivado)
            throw new InvalidOperationException($"O agente {agente.Handle} está arquivado e não recebe versões.");
        var atual = (await versoes.ObterPorIdAsync(agente.IdVersaoAtual!.Value, cancellationToken))!.Configuracao;
        var driver = alteracao.Driver ?? atual.Driver;
        var trocouDriver = driver != atual.Driver;
        // Um modelo ou esforço nunca passa de um driver a outro: sem novos valores, voltam ao padrão.
        var modelo = Escolha(alteracao.Modelo, trocouDriver ? null : atual.Modelo);
        var esforco = Escolha(alteracao.Esforco, trocouDriver || (alteracao.Modelo is not null && alteracao.Esforco is null
            && !CadastroDeAgentesValidator.Eh(modelo, atual.Modelo ?? "")) ? null : atual.Esforco);
        (modelo, esforco) = await ConferirNoCatalogoAsync(driver, modelo, esforco, cancellationToken);
        var perfilMaximo = alteracao.PerfilMaximo ?? atual.PerfilMaximo;
        var podeEscrever = alteracao.PodeEscrever ?? (alteracao.PerfilMaximo is { } maximo
            ? maximo != AgentPermissionProfile.Plan : atual.PodeEscrever);
        var configuracao = Configuracao(() => new ConfiguracaoDoAgente(driver,
            alteracao.PerfilPadrao ?? (alteracao.PerfilMaximo is not null &&
                ConfiguracaoDoAgente.Amplitude(atual.PerfilPadrao) > ConfiguracaoDoAgente.Amplitude(perfilMaximo)
                    ? perfilMaximo : atual.PerfilPadrao),
            perfilMaximo, podeEscrever, modelo, esforco,
            alteracao.LimparInstrucoes ? null : alteracao.Instrucoes ?? atual.Instrucoes,
            alteracao.Capacidades ?? atual.Capacidades, alteracao.Ferramentas ?? atual.Ferramentas,
            alteracao.Repositorios ?? atual.Repositorios,
            new LimitesDoAgente(
                (int?)Limite(alteracao.ExecucoesSimultaneas, atual.Limites.ExecucoesSimultaneas),
                Limite(alteracao.TokensPorExecucao, atual.Limites.TokensPorExecucao),
                (int?)Limite(alteracao.TurnosPorEtapa, atual.Limites.TurnosPorEtapa),
                (int?)Limite(alteracao.MinutosPorEtapa, atual.Limites.MinutosPorEtapa))));
        if (configuracao.Hash() == atual.Hash())
            throw new InvalidOperationException($"Nada mudou na configuração de {agente.Handle}; nenhuma versão criada.");
        var versao = agente.NovaVersao(configuracao, idDoAutor, relogio.GetUtcNow());
        await versoes.AdicionarAsync(versao, cancellationToken);
        agentes.Atualizar(agente);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
        return Dto(agente, versao);
    }

    public async Task<AgenteDto> ApresentarAsync(string handle, string? nome, string? descricao,
        CancellationToken cancellationToken = default)
    {
        CadastroDeAgentesValidator.SemSegredo(nome, descricao);
        var agente = await RegistradoAsync(handle, cancellationToken);
        if (agente.Estado == EstadoDoAgente.Arquivado)
            throw new InvalidOperationException($"O agente {agente.Handle} está arquivado.");
        Dominio(() => agente.Apresentar(nome, descricao, relogio.GetUtcNow()));
        return await GravarAsync(agente, cancellationToken);
    }

    public async Task<AgenteDto> DesativarAsync(string handle, CancellationToken cancellationToken = default)
    {
        var agente = await RegistradoAsync(handle, cancellationToken);
        if (agente.Estado != EstadoDoAgente.Ativo)
            throw new InvalidOperationException($"O agente {agente.Handle} não está ativo ({Nome(agente.Estado)}).");
        agente.Desativar(relogio.GetUtcNow());
        return await GravarAsync(agente, cancellationToken);
    }

    public async Task<AgenteDto> ReativarAsync(string handle, CancellationToken cancellationToken = default)
    {
        var agente = await RegistradoAsync(handle, cancellationToken);
        if (agente.Estado != EstadoDoAgente.Desativado)
            throw new InvalidOperationException(agente.Estado == EstadoDoAgente.Arquivado
                ? $"O agente {agente.Handle} está arquivado e não volta a ficar ativo."
                : $"O agente {agente.Handle} já está ativo.");
        agente.Reativar(relogio.GetUtcNow());
        return await GravarAsync(agente, cancellationToken);
    }

    public async Task<AgenteDto> ArquivarAsync(string handle, CancellationToken cancellationToken = default)
    {
        var agente = await RegistradoAsync(handle, cancellationToken);
        if (agente.Estado != EstadoDoAgente.Desativado)
            throw new InvalidOperationException(agente.Estado == EstadoDoAgente.Arquivado
                ? $"O agente {agente.Handle} já está arquivado."
                : $"Desative o agente {agente.Handle} antes de arquivá-lo.");
        if (await agentes.ContarTrabalhoAtivoAsync(agente.Id, cancellationToken) is > 0 and var ativos)
            throw new InvalidOperationException(
                $"O agente {agente.Handle} ainda tem {ativos} execução(ões) ou sessão(ões) ativa(s); arquive depois que terminarem.");
        agente.Arquivar(relogio.GetUtcNow());
        return await GravarAsync(agente, cancellationToken);
    }

    public async Task<ResolucaoDeAgenteDto> ResolverAsync(string handle, CancellationToken cancellationToken = default)
    {
        var texto = handle?.Trim() ?? "";
        if (AgentesEmbutidos.TryObter(texto, out var driver))
            return new ResolucaoDeAgenteDto { Handle = AgentesEmbutidos.Handle(driver), DriverEmbutido = driver };
        string normalizado;
        try { normalizado = CadastroDeAgentesValidator.Handle(texto); }
        catch (ArgumentException exception)
        {
            return Recusa(FalhaNaResolucaoDeAgente.HandleInvalido, texto, exception.Message);
        }
        var agente = await agentes.ObterPorHandleAsync(normalizado, cancellationToken);
        if (agente is null)
            return Recusa(FalhaNaResolucaoDeAgente.Inexistente, normalizado,
                $"Agente {normalizado} não cadastrado. Veja /agentes.");
        if (agente.Estado == EstadoDoAgente.Desativado)
            return Recusa(FalhaNaResolucaoDeAgente.Desativado, normalizado,
                $"O agente {normalizado} está desativado; reative com /agente reativar {normalizado}.");
        if (agente.Estado == EstadoDoAgente.Arquivado)
            return Recusa(FalhaNaResolucaoDeAgente.Arquivado, normalizado, $"O agente {normalizado} está arquivado.");
        var versao = agente.IdVersaoAtual is { } id ? await versoes.ObterPorIdAsync(id, cancellationToken) : null;
        // Versão adulterada (hash não confere com o que está gravado) nunca é usada.
        if (versao is null || !versao.Integra)
            return Recusa(FalhaNaResolucaoDeAgente.Indisponivel, normalizado,
                $"A versão atual do agente {normalizado} não pôde ser conferida; nada foi iniciado.");
        var dto = Dto(agente, versao);
        return new ResolucaoDeAgenteDto { Handle = normalizado, Agente = dto, Versao = dto.VersaoAtual };
    }

    private async Task<Agente> RegistradoAsync(string handle, CancellationToken cancellationToken)
    {
        if (AgentesEmbutidos.TryObter(handle?.Trim(), out var driver))
            throw new InvalidOperationException(
                $"{AgentesEmbutidos.Handle(driver)} é um agente embutido e não é editável; use /model, /effort e /mode.");
        var normalizado = CadastroDeAgentesValidator.Handle(handle);
        return await agentes.ObterPorHandleAsync(normalizado, cancellationToken)
            ?? throw new InvalidOperationException($"Agente {normalizado} não cadastrado. Veja /agentes.");
    }

    private async Task<AgenteDto> GravarAsync(Agente agente, CancellationToken cancellationToken)
    {
        agentes.Atualizar(agente);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
        return Dto(agente, agente.IdVersaoAtual is { } id ? await versoes.ObterPorIdAsync(id, cancellationToken) : null);
    }

    // Confere no catálogo do driver e devolve os nomes como o catálogo os conhece. Sem modelo, o esforço é conferido
    // no modelo padrão do driver. Catálogo indisponível recusa: nunca grava um nome não verificado.
    private async Task<(string? Modelo, string? Esforco)> ConferirNoCatalogoAsync(AgentKind driver, string? modelo,
        string? esforco, CancellationToken cancellationToken)
    {
        if (modelo is null && esforco is null) return (null, null);
        if (catalogo is null)
            throw new InvalidOperationException("Catálogo de modelos indisponível; não é possível validar modelo e esforço.");
        IReadOnlyList<AgentModelInfo> oferecidos;
        try { oferecidos = await catalogo.GetModelsAsync(driver, cancellationToken); }
        catch (AgentModelCatalogException exception)
        {
            throw new InvalidOperationException($"Não foi possível validar o modelo no {driver}: {exception.Message}.");
        }
        AgentModelInfo? escolhido;
        string? nome = null;
        if (modelo is null) escolhido = oferecidos.FirstOrDefault(item => item.IsDefault);
        else
        {
            escolhido = oferecidos.FirstOrDefault(item => CadastroDeAgentesValidator.Eh(item.Id, modelo) ||
                CadastroDeAgentesValidator.Eh(item.ResolvedId, modelo));
            if (escolhido is null) throw new ArgumentException($"O modelo {modelo} não é oferecido pelo {driver}.");
            nome = CadastroDeAgentesValidator.Eh(escolhido.Id, modelo) ? escolhido.Id : escolhido.ResolvedId;
        }
        if (esforco is null) return (nome, null);
        if (escolhido is null)
            throw new ArgumentException($"Não foi possível identificar o modelo padrão do {driver}; informe o modelo.");
        var nivel = escolhido.EffortLevels.FirstOrDefault(item => CadastroDeAgentesValidator.Eh(item, esforco))
            ?? throw new ArgumentException($"Esforço {esforco} incompatível com {driver}, modelo {escolhido.Id}. " +
                $"Opções: {(escolhido.EffortLevels.Count == 0 ? "nenhuma" : string.Join(", ", escolhido.EffortLevels))}.");
        return (nome, nivel);
    }

    private AgenteDto Dto(Agente agente, VersaoDoAgente? versao) => mapper.Mapear<Agente, AgenteDto>(agente) with
    {
        VersaoAtual = versao is null ? null : mapper.Mapear<VersaoDoAgente, VersaoDoAgenteDto>(versao)
    };

    private static ResolucaoDeAgenteDto Recusa(FalhaNaResolucaoDeAgente falha, string handle, string erro) =>
        new() { Falha = falha, Handle = handle, Erro = erro };

    // "default" remove; nulo herda.
    private static string? Escolha(string? informado, string? herdado) =>
        informado is null ? herdado : CadastroDeAgentesValidator.EhPadrao(informado) ? null : informado;

    private static long? Limite(long? informado, long? herdado) => informado is null ? herdado : informado == 0 ? null : informado;

    private static string Nome(EstadoDoAgente estado) => estado.ToString().ToLowerInvariant();

    // Invariantes do domínio chegam ao usuário sem o nome do parâmetro.
    private static ConfiguracaoDoAgente Configuracao(Func<ConfiguracaoDoAgente> criar) => Dominio(criar);

    private static T Dominio<T>(Func<T> criar)
    {
        try { return criar(); }
        catch (ArgumentException exception) { throw new ArgumentException(SemParametro(exception)); }
    }

    private static void Dominio(Action acao) => Dominio(() => { acao(); return 0; });

    private static string SemParametro(ArgumentException exception)
    {
        var mensagem = exception.Message;
        var parametro = exception.ParamName is null ? -1 : mensagem.LastIndexOf(" (", StringComparison.Ordinal);
        return parametro < 0 ? mensagem : mensagem[..parametro];
    }
}
