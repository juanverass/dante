using Dante.Application.Agentes;
using Dante.Application.Execucoes;
using Dante.Application.Mapeamento;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Domain.Grupos;

namespace Dante.Application.Grupos;

// Cadastro versionado de grupos (AD-69, #308) sobre o banco da orquestração (AD-66): cada operação é uma transação.
// Os participantes são conferidos contra a versão atual de cada agente ao salvar, ao ativar e ao resolver para uma
// execução; o grupo referencia o agente, e a versão dele só é congelada na execução.
public sealed class CadastroDeGruposAppService(
    IGrupoDeAgentesRepository grupos,
    IVersaoDoGrupoRepository versoes,
    IAtivacaoDeVersaoDoGrupoRepository ativacoes,
    IAgenteRepository agentes,
    IVersaoDoAgenteRepository versoesDeAgente,
    IUnitOfWorkDaOrquestracao unidade,
    IMapsterTypeAdapter mapper,
    TimeProvider? relogio = null) : ICadastroDeGruposAppService
{
    private readonly TimeProvider relogio = relogio ?? TimeProvider.System;

    public async Task<GrupoDeAgentesDto> CriarAsync(NovoGrupoDto novo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(novo);
        ArgumentNullException.ThrowIfNull(novo.Configuracao);
        var nome = Nome(novo.Nome);
        SemSegredo(novo.Descricao);
        if (await grupos.ObterPorNomeAsync(nome, cancellationToken) is { } existente)
            throw new InvalidOperationException(existente.Estado == EstadoDoGrupo.Arquivado
                ? $"O nome {nome} pertenceu a um grupo arquivado e não é reutilizado."
                : $"Já existe um grupo {nome}.");
        var entrada = novo.Configuracao;
        var participantes = await ParticipantesAsync(entrada.Participantes, cancellationToken);
        SemSegredo(entrada.PapelDoCoordenador);
        // Com coordenador, o modo Coordenador (AD-75): o domínio recusa roteiro, rodadas e participante com escrita.
        var modo = entrada.PapelDoCoordenador is null ? ModoDeCoordenacao.Roteiro : ModoDeCoordenacao.Coordenador;
        var configuracao = Dominio(() => new ConfiguracaoDoGrupo(modo, participantes.Select(p => p.Participante),
            entrada.Roteiro, new PoliticaDoGrupo(entrada.RodadasDeRevisaoMaximas, 1, entrada.Repositorios,
                entrada.DuracaoMaximaEmMinutos), entrada.PapelDoCoordenador));
        ConferirParticipantes(configuracao, participantes);
        var agora = relogio.GetUtcNow();
        var grupo = Dominio(() => new GrupoDeAgentes(nome, novo.Descricao, novo.IdDoAutor, agora));
        var versao = grupo.NovaVersao(configuracao, novo.IdDoAutor, agora);
        await grupos.AdicionarAsync(grupo, cancellationToken);
        await versoes.AdicionarAsync(versao, cancellationToken);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
        return await DtoAsync(grupo, detalhado: false, cancellationToken);
    }

    public async Task<GrupoDeAgentesDto?> ObterAsync(string nome, CancellationToken cancellationToken = default)
    {
        var grupo = await grupos.ObterPorNomeAsync(Nome(nome), cancellationToken);
        return grupo is null ? null : await DtoAsync(grupo, detalhado: true, cancellationToken);
    }

    public async Task<IReadOnlyList<GrupoDeAgentesDto>> ListarAsync(bool incluirArquivados = false,
        CancellationToken cancellationToken = default)
    {
        var lista = new List<GrupoDeAgentesDto>();
        foreach (var grupo in (await grupos.ListarAsync(incluirArquivados, cancellationToken))
                     .OrderBy(grupo => grupo.Nome, StringComparer.Ordinal))
            lista.Add(await DtoAsync(grupo, detalhado: false, cancellationToken));
        return lista;
    }

    public async Task<GrupoDeAgentesDto> EditarAsync(string nome, AlteracaoDoGrupoDto alteracao, long idDoAutor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alteracao);
        var grupo = await ExistenteAsync(nome, cancellationToken);
        if (grupo.Estado == EstadoDoGrupo.Arquivado)
            throw new InvalidOperationException($"O grupo {grupo.Nome} está arquivado e não recebe versões.");
        var versaoAtual = await versoes.ObterComParticipantesAsync(grupo.IdVersaoAtual!.Value, cancellationToken);
        var atual = versaoAtual?.Configuracao
            ?? throw new InvalidOperationException($"A versão atual do grupo {grupo.Nome} não pôde ser conferida.");
        var participantes = alteracao.Participantes is { } informados
            ? await ParticipantesAsync(informados, cancellationToken)
            : await ParticipantesAtuaisAsync(atual, cancellationToken);
        SemSegredo(alteracao.PapelDoCoordenador);
        // O modo é o da versão atual: um grupo de roteiro não vira coordenado numa edição (nem o contrário).
        if (atual.Modo == ModoDeCoordenacao.Roteiro && alteracao.PapelDoCoordenador is not null)
            throw new ArgumentException($"O grupo {grupo.Nome} segue um roteiro; o modo não muda numa edição. Crie outro " +
                "grupo com coordenador=<papel>.");
        var roteiro = alteracao.Roteiro ?? atual.Roteiro;
        var temCorrecao = roteiro.Any(passo => passo.Tipo == TipoDePassoDoRoteiro.Corrigir);
        // Trocar a forma do roteiro sem informar rodadas: sem correção, 0; com correção vinda de um roteiro sem ela, 1.
        var rodadas = alteracao.RodadasDeRevisaoMaximas ?? (!temCorrecao ? 0
            : atual.Politica.RodadasDeRevisaoMaximas == 0 ? 1 : atual.Politica.RodadasDeRevisaoMaximas);
        var duracao = alteracao.DuracaoMaximaEmMinutos is { } minutos
            ? minutos == 0 ? null : minutos
            : atual.Politica.DuracaoMaximaEmMinutos;
        var coordenador = atual.Modo == ModoDeCoordenacao.Coordenador
            ? alteracao.PapelDoCoordenador ?? atual.PapelDoCoordenador : null;
        var configuracao = Dominio(() => new ConfiguracaoDoGrupo(atual.Modo,
            participantes.Select(p => p.Participante), roteiro,
            new PoliticaDoGrupo(rodadas, 1, alteracao.Repositorios ?? atual.Politica.RepositoriosPermitidos, duracao),
            coordenador));
        if (configuracao.Hash() == atual.Hash())
            throw new InvalidOperationException($"Nada mudou na configuração de {grupo.Nome}; nenhuma versão criada.");
        ConferirParticipantes(configuracao, participantes);
        var versao = grupo.NovaVersao(configuracao, idDoAutor, relogio.GetUtcNow());
        await versoes.AdicionarAsync(versao, cancellationToken);
        grupos.Atualizar(grupo);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
        return await DtoAsync(grupo, detalhado: false, cancellationToken);
    }

    public async Task<GrupoDeAgentesDto> AtivarAsync(string nome, int numeroDaVersao, string hashConfirmado, long idDoAutor,
        CancellationToken cancellationToken = default)
    {
        var grupo = await ExistenteAsync(nome, cancellationToken);
        if (grupo.Estado != EstadoDoGrupo.Ativo)
            throw new InvalidOperationException($"O grupo {grupo.Nome} não está ativo ({Nome(grupo.Estado)}).");
        var versao = (await versoes.ListarDoGrupoAsync(grupo.Id, cancellationToken))
            .SingleOrDefault(item => item.Numero == numeroDaVersao)
            ?? throw new InvalidOperationException($"O grupo {grupo.Nome} não tem a versão {numeroDaVersao}.");
        if (versao.Configuracao is not { } configuracao || !versao.Integra)
            throw new InvalidOperationException(
                $"A versão {numeroDaVersao} do grupo {grupo.Nome} não confere com o hash gravado; nada foi ativado.");
        // Os agentes podem ter mudado desde que a versão foi salva: a ativação confere de novo.
        ConferirParticipantes(configuracao, await ParticipantesAtuaisAsync(configuracao, cancellationToken));
        var ativacao = Dominio(() => grupo.Ativar(versao, hashConfirmado, idDoAutor, relogio.GetUtcNow()));
        await ativacoes.AdicionarAsync(ativacao, cancellationToken);
        grupos.Atualizar(grupo);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
        return await DtoAsync(grupo, detalhado: false, cancellationToken);
    }

    public async Task<GrupoDeAgentesDto> DescreverAsync(string nome, string? descricao, CancellationToken cancellationToken = default)
    {
        SemSegredo(descricao);
        var grupo = await ExistenteAsync(nome, cancellationToken);
        if (grupo.Estado == EstadoDoGrupo.Arquivado)
            throw new InvalidOperationException($"O grupo {grupo.Nome} está arquivado.");
        Dominio(() => grupo.Descrever(descricao, relogio.GetUtcNow()));
        return await GravarAsync(grupo, cancellationToken);
    }

    public async Task<GrupoDeAgentesDto> DesativarAsync(string nome, CancellationToken cancellationToken = default)
    {
        var grupo = await ExistenteAsync(nome, cancellationToken);
        if (grupo.Estado != EstadoDoGrupo.Ativo)
            throw new InvalidOperationException($"O grupo {grupo.Nome} não está ativo ({Nome(grupo.Estado)}).");
        grupo.Desativar(relogio.GetUtcNow());
        return await GravarAsync(grupo, cancellationToken);
    }

    public async Task<GrupoDeAgentesDto> ReativarAsync(string nome, CancellationToken cancellationToken = default)
    {
        var grupo = await ExistenteAsync(nome, cancellationToken);
        if (grupo.Estado != EstadoDoGrupo.Desativado)
            throw new InvalidOperationException(grupo.Estado == EstadoDoGrupo.Arquivado
                ? $"O grupo {grupo.Nome} está arquivado e não volta a ficar ativo."
                : $"O grupo {grupo.Nome} já está ativo.");
        grupo.Reativar(relogio.GetUtcNow());
        return await GravarAsync(grupo, cancellationToken);
    }

    public async Task<GrupoDeAgentesDto> ArquivarAsync(string nome, CancellationToken cancellationToken = default)
    {
        var grupo = await ExistenteAsync(nome, cancellationToken);
        if (grupo.Estado != EstadoDoGrupo.Desativado)
            throw new InvalidOperationException(grupo.Estado == EstadoDoGrupo.Arquivado
                ? $"O grupo {grupo.Nome} já está arquivado."
                : $"Desative o grupo {grupo.Nome} antes de arquivá-lo.");
        if (await grupos.ContarExecucoesAtivasAsync(grupo.Id, cancellationToken) is > 0 and var ativas)
            throw new InvalidOperationException(
                $"O grupo {grupo.Nome} ainda tem {ativas} execução(ões) em andamento; arquive depois que terminarem.");
        grupo.Arquivar(relogio.GetUtcNow());
        return await GravarAsync(grupo, cancellationToken);
    }

    public async Task<ResolucaoDeGrupoDto> ResolverParaExecucaoAsync(string nome, CancellationToken cancellationToken = default)
    {
        var texto = nome?.Trim() ?? "";
        string normalizado;
        try { normalizado = Nome(texto); }
        catch (ArgumentException exception) { return Recusa(FalhaNaResolucaoDeGrupo.NomeInvalido, texto, exception.Message); }
        var grupo = await grupos.ObterPorNomeAsync(normalizado, cancellationToken);
        if (grupo is null)
            return Recusa(FalhaNaResolucaoDeGrupo.Inexistente, normalizado, $"Grupo {normalizado} não cadastrado. Veja /grupos.");
        if (grupo.Estado == EstadoDoGrupo.Desativado)
            return Recusa(FalhaNaResolucaoDeGrupo.Desativado, normalizado,
                $"O grupo {normalizado} está desativado; reative com /grupo reativar {normalizado}.");
        if (grupo.Estado == EstadoDoGrupo.Arquivado)
            return Recusa(FalhaNaResolucaoDeGrupo.Arquivado, normalizado, $"O grupo {normalizado} está arquivado.");
        if (grupo.IdVersaoAtiva is not { } idAtiva)
            return Recusa(FalhaNaResolucaoDeGrupo.SemVersaoAtiva, normalizado,
                $"O grupo {normalizado} não tem versão ativada; ative uma com /grupo ativar {normalizado} v<N> <hash>.");
        var versao = await versoes.ObterComParticipantesAsync(idAtiva, cancellationToken);
        if (versao?.Configuracao is not { } configuracao || !versao.Integra)
            return Recusa(FalhaNaResolucaoDeGrupo.Indisponivel, normalizado,
                $"A versão ativa do grupo {normalizado} não pôde ser conferida; nada foi iniciado.");
        IReadOnlyList<ParticipanteComAgente> participantes;
        try
        {
            participantes = await ParticipantesAtuaisAsync(configuracao, cancellationToken);
            ConferirParticipantes(configuracao, participantes);
        }
        catch (InvalidOperationException exception)
        {
            return Recusa(FalhaNaResolucaoDeGrupo.ParticipanteInvalido, normalizado, exception.Message);
        }
        return new ResolucaoDeGrupoDto
        {
            Nome = normalizado,
            Grupo = await DtoAsync(grupo, detalhado: false, cancellationToken),
            Versao = VersaoDto(versao, participantes),
            Participantes = participantes.Select(item => new ParticipanteResolvidoDto(item.Participante.Papel,
                item.Agente.Handle, item.Participante.PodeEscrever, item.Participante.Responsabilidades,
                mapper.Mapear<VersaoDoAgente, VersaoDoAgenteDto>(item.Versao))).ToArray()
        };
    }

    // Participantes informados pelo handle: só agentes registrados e ativos, com versão atual íntegra.
    private async Task<IReadOnlyList<ParticipanteComAgente>> ParticipantesAsync(
        IReadOnlyList<ParticipanteInformadoDto> informados, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(informados);
        var lista = new List<ParticipanteComAgente>();
        foreach (var informado in informados)
        {
            ArgumentNullException.ThrowIfNull(informado);
            SemSegredo(informado.Papel, informado.Responsabilidades);
            var handle = informado.Handle?.Trim().ToLowerInvariant() ?? "";
            if (AgentesEmbutidos.TryObter(handle, out _))
                throw new ArgumentException($"{handle} é um agente embutido; grupos usam agentes registrados (veja /agentes).");
            if (!Agente.HandleValido(handle)) throw new ArgumentException($"Handle de agente inválido: {informado.Handle}.");
            var agente = await agentes.ObterPorHandleAsync(handle, cancellationToken)
                ?? throw new InvalidOperationException($"Agente {handle} não cadastrado. Veja /agentes.");
            lista.Add(await ComVersaoAsync(new ParticipanteDoGrupo(agente.Id, informado.Papel, informado.Responsabilidades,
                informado.PodeEscrever), agente, cancellationToken));
        }
        return lista;
    }

    private async Task<IReadOnlyList<ParticipanteComAgente>> ParticipantesAtuaisAsync(ConfiguracaoDoGrupo configuracao,
        CancellationToken cancellationToken)
    {
        var lista = new List<ParticipanteComAgente>();
        foreach (var participante in configuracao.Participantes)
        {
            var agente = await agentes.ObterPorIdAsync(participante.IdAgente, cancellationToken)
                ?? throw new InvalidOperationException($"O agente do papel {participante.Papel} não existe mais.");
            lista.Add(await ComVersaoAsync(participante, agente, cancellationToken));
        }
        return lista;
    }

    private async Task<ParticipanteComAgente> ComVersaoAsync(ParticipanteDoGrupo participante, Agente agente,
        CancellationToken cancellationToken)
    {
        if (agente.Estado != EstadoDoAgente.Ativo)
            throw new InvalidOperationException(
                $"O agente {agente.Handle} (papel {participante.Papel}) está {Nome(agente.Estado)}; nenhum participante é " +
                "substituído automaticamente.");
        var versao = agente.IdVersaoAtual is { } id ? await versoesDeAgente.ObterPorIdAsync(id, cancellationToken) : null;
        if (versao is null || !versao.Integra)
            throw new InvalidOperationException($"A versão atual do agente {agente.Handle} não pôde ser conferida.");
        return new ParticipanteComAgente(participante, agente, versao);
    }

    // O domínio já normalizou papéis; a conferência usa a configuração normalizada, pelo papel.
    private static void ConferirParticipantes(ConfiguracaoDoGrupo configuracao, IReadOnlyList<ParticipanteComAgente> lidos)
    {
        foreach (var participante in configuracao.Participantes)
        {
            var lido = lidos.First(item => item.Participante.IdAgente == participante.IdAgente &&
                string.Equals(item.Participante.Papel.Trim(), participante.Papel, StringComparison.OrdinalIgnoreCase));
            if (ParticipacaoNoRoteiro.Recusa(participante, lido.Agente.Handle, lido.Versao.Configuracao,
                    configuracao.Roteiro) is { } recusa)
                throw new InvalidOperationException(recusa);
        }
        // Coordenador (AD-75): cada unidade do plano é uma consulta.v1 a outro participante; sem ninguém capaz de
        // recebê-la, nenhum plano seria válido. As capacidades são as da versão atual de cada agente (a execução congela).
        if (configuracao.Modo == ModoDeCoordenacao.Coordenador &&
            !configuracao.Participantes.Any(participante => participante.Papel != configuracao.PapelDoCoordenador &&
                ContratosDeDelegacao.Consulta.Aceita(lidos.First(item => item.Participante.IdAgente == participante.IdAgente)
                    .Versao.Capacidades)))
            throw new InvalidOperationException(
                $"Nenhum participante além do coordenador tem capacidade para {ContratosDeDelegacao.Consulta.Identificador}; " +
                "o coordenador não teria a quem delegar.");
    }

    private async Task<GrupoDeAgentes> ExistenteAsync(string nome, CancellationToken cancellationToken)
    {
        var normalizado = Nome(nome);
        return await grupos.ObterPorNomeAsync(normalizado, cancellationToken)
            ?? throw new InvalidOperationException($"Grupo {normalizado} não cadastrado. Veja /grupos.");
    }

    private async Task<GrupoDeAgentesDto> GravarAsync(GrupoDeAgentes grupo, CancellationToken cancellationToken)
    {
        grupos.Atualizar(grupo);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
        return await DtoAsync(grupo, detalhado: false, cancellationToken);
    }

    private async Task<GrupoDeAgentesDto> DtoAsync(GrupoDeAgentes grupo, bool detalhado, CancellationToken cancellationToken)
    {
        var dto = mapper.Mapear<GrupoDeAgentes, GrupoDeAgentesDto>(grupo);
        if (detalhado)
        {
            var todas = new List<VersaoDoGrupoDto>();
            foreach (var versao in (await versoes.ListarDoGrupoAsync(grupo.Id, cancellationToken))
                         .OrderByDescending(versao => versao.Numero))
                todas.Add(await VersaoDtoAsync(versao, cancellationToken));
            var decisoes = (await ativacoes.ListarDoGrupoAsync(grupo.Id, cancellationToken))
                .OrderByDescending(ativacao => ativacao.Em)
                .Select(ativacao => new AtivacaoDto(ativacao.NumeroDaVersao, ativacao.HashAprovado, ativacao.IdDoAutor,
                    ativacao.Em)).ToArray();
            return dto with
            {
                VersaoAtual = todas.FirstOrDefault(versao => versao.Id == grupo.IdVersaoAtual),
                VersaoAtiva = todas.FirstOrDefault(versao => versao.Id == grupo.IdVersaoAtiva),
                Versoes = todas,
                Ativacoes = decisoes
            };
        }
        var atual = await VersaoDtoAsync(grupo.IdVersaoAtual, cancellationToken);
        var ativa = grupo.IdVersaoAtiva is null ? null
            : grupo.IdVersaoAtiva == grupo.IdVersaoAtual ? atual
            : await VersaoDtoAsync(grupo.IdVersaoAtiva, cancellationToken);
        return dto with { VersaoAtual = atual, VersaoAtiva = ativa };
    }

    private async Task<VersaoDoGrupoDto?> VersaoDtoAsync(Guid? id, CancellationToken cancellationToken) =>
        id is { } valor && await versoes.ObterComParticipantesAsync(valor, cancellationToken) is { } versao
            ? await VersaoDtoAsync(versao, cancellationToken)
            : null;

    private async Task<VersaoDoGrupoDto> VersaoDtoAsync(VersaoDoGrupo versao, CancellationToken cancellationToken)
    {
        var handles = new List<ParticipanteDto>();
        foreach (var participante in versao.Participantes)
        {
            var agente = await agentes.ObterPorIdAsync(participante.IdAgente, cancellationToken);
            handles.Add(new ParticipanteDto(participante.Papel, participante.IdAgente, agente?.Handle ?? "?",
                participante.PodeEscrever, participante.Responsabilidades));
        }
        return Base(versao) with { Participantes = handles };
    }

    private static VersaoDoGrupoDto VersaoDto(VersaoDoGrupo versao, IReadOnlyList<ParticipanteComAgente> participantes) =>
        Base(versao) with
        {
            Participantes = participantes.Select(item => new ParticipanteDto(item.Participante.Papel, item.Agente.Id,
                item.Agente.Handle, item.Participante.PodeEscrever, item.Participante.Responsabilidades)).ToArray()
        };

    private static VersaoDoGrupoDto Base(VersaoDoGrupo versao) => new()
    {
        Id = versao.Id,
        IdGrupo = versao.IdGrupo,
        Numero = versao.Numero,
        Configuracao = versao.Configuracao,
        HashDaConfiguracao = versao.HashDaConfiguracao,
        Integra = versao.Integra,
        IdDoAutor = versao.IdDoAutor,
        CriadaEm = versao.CriadaEm
    };

    private static ResolucaoDeGrupoDto Recusa(FalhaNaResolucaoDeGrupo falha, string nome, string erro) =>
        new() { Falha = falha, Nome = nome, Erro = erro };

    private static string Nome(string? nome)
    {
        SemSegredo(nome);
        var normalizado = nome?.Trim().ToLowerInvariant();
        if (normalizado is not null && GrupoDeAgentes.NomesReservados.Contains(normalizado))
            throw new ArgumentException($"O nome {normalizado} é reservado.");
        if (!GrupoDeAgentes.NomeValido(normalizado))
            throw new ArgumentException(
                "Nome de grupo inválido: use de 2 a 31 caracteres minúsculos (letras, números e -), começando por letra.");
        return normalizado!;
    }

    private static string Nome(EstadoDoGrupo estado) => estado.ToString().ToLowerInvariant();

    private static string Nome(EstadoDoAgente estado) => estado switch
    {
        EstadoDoAgente.Desativado => "desativado",
        EstadoDoAgente.Arquivado => "arquivado",
        _ => "ativo"
    };

    // Nome, descrição, papéis e responsabilidades vão ao ledger e ao prompt dos agentes: nunca podem parecer credencial.
    private static void SemSegredo(params string?[] textos)
    {
        if (textos.Any(ProtecaoDeSegredos.ContemSegredo))
            throw new ArgumentException(
                "Possível credencial no cadastro do grupo. Credenciais nunca entram em grupos, papéis ou responsabilidades.");
    }

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

    private sealed record ParticipanteComAgente(ParticipanteDoGrupo Participante, Agente Agente, VersaoDoAgente Versao);
}
