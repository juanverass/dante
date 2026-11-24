using Dante.Application.Contextos;

namespace Dante.Application.IntegracaoDeCi;

// Andamento do CI de um PR (#238, AD-77), sob demanda e somente leitura:
// 1. o repositório é o alias explícito, senão o da sessão ativa, senão o repositório ativo; só alias cadastrado;
// 2. a identidade de CI vem do cadastro e da única conexão que libera o alias (IRepositoriosDeCi), nunca herdada;
// 3. o PR é lido agora para obter o head SHA atual: o resultado é sempre o desse commit, e execuções de commits
//    anteriores são descartadas pela ConsolidacaoDoCheckDeCi (um commit novo não herda sucesso do antigo);
// 4. falha da consulta vira SemPermissao ou Indisponivel no check, nunca falha do CI.
// Não há polling: cada chamada consulta o provedor uma vez, e o adapter aplica a política de repetição e cache.
public sealed class AcompanhamentoDeCiAppService(
    IProvedorDeCi provedor,
    IRepositoriosDeCi repositorios,
    ICatalogoDeRepositorios catalogo,
    IPreferenciasDoAssistente? preferencias = null,
    TimeProvider? relogio = null) : IAcompanhamentoDeCiAppService
{
    private readonly TimeProvider relogio = relogio ?? TimeProvider.System;

    public async Task<SituacaoDoCiDoPullRequestDto> ConsultarPullRequestAsync(PedidoDeCiDoPullRequestDto pedido,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        var numero = CiValidator.ExigirNumeroDoPr(pedido.NumeroDoPr);
        var (alias, origem) = Vazio(pedido.AliasExplicito) is { } explicito ? (explicito, OrigemDoRepositorioDeCi.Explicito)
            : Vazio(pedido.AliasDaSessao) is { } daSessao ? (daSessao, OrigemDoRepositorioDeCi.Sessao)
            : Vazio(preferencias?.ObterRepositorioAtivo(pedido.IdUsuario)) is { } ativo ? (ativo, OrigemDoRepositorioDeCi.Ativo)
            : ((string?)null, (OrigemDoRepositorioDeCi?)null);
        var situacao = new SituacaoDoCiDoPullRequestDto { NumeroDoPr = numero, Alias = alias, Origem = origem };
        if (alias is null) return situacao with { ProblemaDoRepositorio = ProblemaDoRepositorioDeCi.SemRepositorio };

        RepositorioCadastrado? cadastrado;
        try { cadastrado = catalogo.Obter(alias); }
        catch (ArgumentException) { return situacao with { ProblemaDoRepositorio = ProblemaDoRepositorioDeCi.AliasInvalido }; }
        if (cadastrado is null)
            return situacao with
            {
                ProblemaDoRepositorio = origem == OrigemDoRepositorioDeCi.Ativo
                    ? ProblemaDoRepositorioDeCi.AtivoForaDoCadastro
                    : ProblemaDoRepositorioDeCi.NaoCadastrado
            };
        situacao = situacao with { Alias = cadastrado.Alias };

        PullRequestDeCiDto pullRequest;
        try
        {
            var repositorio = repositorios.Resolver(cadastrado.Alias);
            pullRequest = await provedor.ObterPullRequestAsync(repositorio, numero, cancellationToken);
            // O adapter já confere; aqui a regra não depende dele: outro repositório, conexão ou número não passa.
            if (pullRequest.Numero != numero || !repositorio.Corresponde(pullRequest.Repositorio))
                throw new FalhaNaConsultaDeCiException(MotivoDaFalhaNaConsultaDeCi.Inconsistente,
                    "O provedor devolveu outro PR ou outro repositório; o resultado foi descartado.");
        }
        catch (FalhaNaConsultaDeCiException falha)
        {
            return situacao with { FalhaDoPullRequest = falha.Motivo, RepetirApos = falha.RepetirApos };
        }

        var consulta = new ConsultaDeCi(pullRequest.Repositorio, pullRequest.ShaDoHead, pullRequest.Numero);
        try
        {
            var resultado = await provedor.ConsultarAsync(consulta, cancellationToken);
            return situacao with { PullRequest = pullRequest, Check = ConsolidacaoDoCheckDeCi.Consolidar(consulta, resultado) };
        }
        catch (FalhaNaConsultaDeCiException falha)
        {
            return situacao with
            {
                PullRequest = pullRequest,
                Check = ConsolidacaoDoCheckDeCi.Indisponivel(consulta, falha, relogio.GetUtcNow()),
                RepetirApos = falha.RepetirApos
            };
        }
    }

    private static string? Vazio(string? alias) => string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
}
