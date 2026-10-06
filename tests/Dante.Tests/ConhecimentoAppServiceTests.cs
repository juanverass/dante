using Dante.Application;
using Dante.Application.Comum;
using Dante.Application.Conhecimentos;
using Dante.Application.EspacosDeConhecimento;
using Dante.Application.Projetos;
using Dante.Application.Mapeamento;
using Dante.Domain.Comum;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

public sealed class ConhecimentoAppServiceTests : IDisposable
{
    private readonly ServiceProvider provider = new ServiceCollection().AddApplication().BuildServiceProvider();
    private readonly ConhecimentosEmMemoria conhecimentos = new();
    private readonly EspacosEmMemoria espacos = new();
    private readonly ProjetosEmMemoria projetos = new();
    private readonly Uow uow = new();
    private readonly EspacoDeConhecimento espaco = new(Guid.NewGuid(), "espaço");

    public ConhecimentoAppServiceTests() => espacos.Itens[espaco.Id] = espaco;
    private ConhecimentoAppService Servico => new(conhecimentos, espacos, projetos, uow,
        provider.GetRequiredService<IMapsterTypeAdapter>());
    private static ProvenienciaDto Origem => new() { IdResponsavel = Guid.NewGuid(), Origem = "registro humano", ReferenciaDaFonte = "nota:1" };
    private ConhecimentoDto Entrada => new() { IdEspacoDeConhecimento = espaco.Id, Tipo = TipoDeConhecimento.Incidente, Conteudo = "falha", Proveniencia = Origem };

    [Fact]
    public async Task CriacaoIgnoraIdentidadeAutoriaHistoricoETimestampsForjados()
    {
        var dto = Entrada with { Id = Guid.NewGuid(), IdAutor = Guid.NewGuid(), Revisao = 500,
            CriadoEm = DateTimeOffset.MinValue, AtualizadoEm = DateTimeOffset.MaxValue,
            IdConhecimentoSubstituto = Guid.NewGuid(), Historico = [new RevisaoDoConhecimentoDto { Numero = 99 }] };
        var salvo = await Servico.AdicionarAsync(dto);
        Assert.NotEqual(dto.Id, salvo.Id);
        Assert.Equal(dto.Proveniencia.IdResponsavel, salvo.IdAutor);
        Assert.Equal(1, salvo.Revisao);
        Assert.Single(salvo.Historico);
        Assert.Null(salvo.IdConhecimentoSubstituto);
        Assert.NotEqual(dto.CriadoEm, salvo.CriadoEm);
        Assert.Equal(salvo.CriadoEm, salvo.AtualizadoEm);
        Assert.Equal(1, uow.Commits);
        Assert.Equal(StatusDoConhecimento.Inferido, salvo.Status);
        Assert.Equal(Sensibilidade.Pessoal, salvo.Sensibilidade);
    }

    [Fact]
    public async Task CorrecaoPreservaEscopoEIdentidadeERegistraNovaOrigem()
    {
        var salvo = await Servico.AdicionarAsync(Entrada);
        await Servico.ConfirmarAsync(salvo.Id, 1, Origem);
        var dto = Entrada with { Id = Guid.NewGuid(), IdEspacoDeConhecimento = Guid.NewGuid(), IdProjeto = Guid.NewGuid(),
            Tipo = TipoDeConhecimento.Solucao, Conteudo = "resolvido", Revisao = 2, Status = StatusDoConhecimento.Confirmado };
        var corrigido = (await Servico.CorrigirAsync(salvo.Id, dto))!;
        Assert.Equal(salvo.Id, corrigido.Id);
        Assert.Equal(salvo.IdEspacoDeConhecimento, corrigido.IdEspacoDeConhecimento);
        Assert.Null(corrigido.IdProjeto);
        Assert.Equal(salvo.IdAutor, corrigido.IdAutor);
        Assert.Equal(StatusDoConhecimento.Inferido, corrigido.Status);
        Assert.Equal(3, corrigido.Revisao);
        Assert.Equal("falha", corrigido.Historico[0].Conteudo);
        Assert.Equal(dto.Proveniencia.IdResponsavel, corrigido.Proveniencia.IdResponsavel);
        Assert.Equal(3, uow.Commits);
    }

    [Fact]
    public async Task ConfirmacaoNaoAceitaInferenciaNemRevisaoObsoleta()
    {
        var salvo = await Servico.AdicionarAsync(Entrada with { Tipo = TipoDeConhecimento.Inferencia, Confianca = 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Servico.ConfirmarAsync(salvo.Id, 1, Origem));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Servico.InvalidarAsync(salvo.Id, 0, Origem));
        Assert.Equal(1, uow.Commits);
        Assert.Empty(conhecimentos.Atualizados);
    }

    [Fact]
    public async Task CriacaoRecusaConfirmacaoForjadaSemGravar()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Servico.AdicionarAsync(Entrada with { Status = StatusDoConhecimento.Confirmado }));
        Assert.Empty(conhecimentos.Itens);
        Assert.Equal(0, uow.Commits);
    }

    [Fact]
    public async Task EspacoEProjetoDevemExistirSerAtivosEPertencerAoMesmoEscopo()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Servico.AdicionarAsync(Entrada with { IdEspacoDeConhecimento = Guid.NewGuid() }));
        await Assert.ThrowsAsync<ArgumentException>(() => Servico.AdicionarAsync(Entrada with { IdProjeto = Guid.NewGuid() }));
        var projetoOutroEspaco = new Projeto(Guid.NewGuid(), "outro"); projetos.Itens[projetoOutroEspaco.Id] = projetoOutroEspaco;
        await Assert.ThrowsAsync<ArgumentException>(() => Servico.AdicionarAsync(Entrada with { IdProjeto = projetoOutroEspaco.Id }));
        var projeto = new Projeto(espaco.Id, "projeto"); projetos.Itens[projeto.Id] = projeto;
        projeto.Arquivar();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Servico.AdicionarAsync(Entrada with { IdProjeto = projeto.Id }));
        espaco.Arquivar();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Servico.AdicionarAsync(Entrada));
        Assert.Equal(0, uow.Commits);
    }

    [Fact]
    public async Task ConhecimentoPodePertencerDiretoAoEspacoOuAoProjeto()
    {
        var projeto = new Projeto(espaco.Id, "projeto"); projetos.Itens[projeto.Id] = projeto;
        Assert.Null((await Servico.AdicionarAsync(Entrada)).IdProjeto);
        Assert.Equal(projeto.Id, (await Servico.AdicionarAsync(Entrada with { IdProjeto = projeto.Id })).IdProjeto);
    }

    [Fact]
    public async Task ArquivamentoDoEscopoBloqueiaTodasAsEscritasExistentes()
    {
        var salvo = await Servico.AdicionarAsync(Entrada);
        var substituto = await Servico.AdicionarAsync(Entrada with { Tipo = TipoDeConhecimento.Aprendizado });
        espaco.Arquivar();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Servico.CorrigirAsync(salvo.Id, Entrada with { Revisao = 1 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Servico.ConfirmarAsync(salvo.Id, 1, Origem));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Servico.InvalidarAsync(salvo.Id, 1, Origem));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Servico.SubstituirAsync(salvo.Id, substituto.Id, 1, Origem));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Servico.RemoverAsync(salvo.Id));
        Assert.Equal(2, uow.Commits);
        Assert.NotNull(await Servico.ObterPorIdAsync(salvo.Id));
    }

    [Fact]
    public async Task SubstituicaoMantemAnteriorEReferenciaSemAlterarSubstituto()
    {
        var salvo = await Servico.AdicionarAsync(Entrada);
        var substituto = await Servico.AdicionarAsync(Entrada with { Tipo = TipoDeConhecimento.Solucao });
        Assert.True(await Servico.SubstituirAsync(salvo.Id, substituto.Id, 1, Origem));
        var antigo = (await Servico.ObterPorIdAsync(salvo.Id))!;
        Assert.Equal(StatusDoConhecimento.Substituido, antigo.Status);
        Assert.Equal(substituto.Id, antigo.IdConhecimentoSubstituto);
        Assert.Equal(2, antigo.Historico.Count);
        Assert.Equal(1, (await Servico.ObterPorIdAsync(substituto.Id))!.Revisao);
        Assert.Equal(3, uow.Commits);
    }

    [Fact]
    public async Task SubstitutoAusenteOuDeOutroEscopoNaoGrava()
    {
        var salvo = await Servico.AdicionarAsync(Entrada);
        await Assert.ThrowsAsync<ArgumentException>(() => Servico.SubstituirAsync(salvo.Id, Guid.NewGuid(), 1, Origem));
        var outroEspaco = new EspacoDeConhecimento(Guid.NewGuid(), "outro"); espacos.Itens[outroEspaco.Id] = outroEspaco;
        var substituto = await Servico.AdicionarAsync(Entrada with { IdEspacoDeConhecimento = outroEspaco.Id });
        await Assert.ThrowsAsync<ArgumentException>(() => Servico.SubstituirAsync(salvo.Id, substituto.Id, 1, Origem));
        Assert.Equal(2, uow.Commits);
        Assert.Empty(conhecimentos.Atualizados);
    }

    [Fact]
    public async Task InvalidacaoERemocaoSaoDistintas()
    {
        var salvo = await Servico.AdicionarAsync(Entrada);
        Assert.True(await Servico.InvalidarAsync(salvo.Id, 1, Origem));
        Assert.Equal(StatusDoConhecimento.Inativo, (await Servico.ObterPorIdAsync(salvo.Id))!.Status);
        Assert.True(await Servico.RemoverAsync(salvo.Id));
        Assert.Null(await Servico.ObterPorIdAsync(salvo.Id));
        Assert.Equal(3, uow.Commits);
    }

    [Fact]
    public async Task AusentesNaoGravamNemFabricamConhecimento()
    {
        var id = Guid.NewGuid();
        Assert.Null(await Servico.ObterPorIdAsync(id));
        Assert.Null(await Servico.CorrigirAsync(id, Entrada));
        Assert.False(await Servico.ConfirmarAsync(id, 1, Origem));
        Assert.False(await Servico.InvalidarAsync(id, 1, Origem));
        Assert.False(await Servico.SubstituirAsync(id, Guid.NewGuid(), 1, Origem));
        Assert.False(await Servico.RemoverAsync(id));
        Assert.Equal(0, uow.Commits);
    }

    [Fact]
    public async Task PesquisaExigeEscopoEValidaLimitesAntesDeConsultar()
    {
        foreach (var filtro in new[] { new ConhecimentoSearchDto(), new ConhecimentoSearchDto { IdEspacoDeConhecimento = espaco.Id, Limite = 0 },
            new ConhecimentoSearchDto { IdEspacoDeConhecimento = espaco.Id, Limite = 101 },
            new ConhecimentoSearchDto { IdEspacoDeConhecimento = espaco.Id, Tipo = (TipoDeConhecimento)99 },
            new ConhecimentoSearchDto { IdEspacoDeConhecimento = espaco.Id, SomenteSemProjeto = true, IdProjeto = Guid.NewGuid() } })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => Servico.PesquisarAsync(filtro));
        Assert.Equal(0, conhecimentos.Consultas);
    }

    [Fact]
    public async Task PesquisaFiltraStatusValidadeETagDentroDoEspacoAntesDoLimite()
    {
        var ativo = await Servico.AdicionarAsync(Entrada with { Tags = [" brain "] });
        var inativo = await Servico.AdicionarAsync(Entrada); await Servico.InvalidarAsync(inativo.Id, 1, Origem);
        var futuro = await Servico.AdicionarAsync(Entrada with { ValidoDesde = DateTimeOffset.UtcNow.AddDays(1) });
        var filtro = new ConhecimentoSearchDto { IdEspacoDeConhecimento = espaco.Id, ValidoEm = DateTimeOffset.UtcNow, Tag = " brain " };
        Assert.Equal(ativo.Id, Assert.Single(await Servico.PesquisarAsync(filtro)).Id);
        Assert.Equal("brain", conhecimentos.UltimoFiltro!.Tag);
        Assert.Equal(2, (await Servico.PesquisarAsync(filtro with { Tag = null, ValidoEm = null })).Count);
        Assert.Equal(3, (await Servico.PesquisarAsync(filtro with { Tag = null, ValidoEm = null, IncluirInativosOuSubstituidos = true })).Count);
    }

    [Fact]
    public async Task DtoNaoCompartilhaColecoesComDominio()
    {
        var salvo = await Servico.AdicionarAsync(Entrada with { Tags = ["original"] });
        ((string[])salvo.Tags)[0] = "mutado";
        ((string[])salvo.Historico[0].Tags)[0] = "mutado";
        Assert.Equal("original", conhecimentos.Itens[salvo.Id].Tags[0]);
        Assert.Equal("original", conhecimentos.Itens[salvo.Id].Historico[0].Tags[0]);
    }

    [Fact]
    public async Task FalhaNoCommitNaoRetornaSucesso()
    {
        uow.Falhar = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Servico.AdicionarAsync(Entrada));
    }

    [Fact]
    public async Task CancelamentoPropagaSemCommit()
    {
        using var cancelado = new CancellationTokenSource();
        cancelado.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Servico.AdicionarAsync(Entrada, cancelado.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Servico.PesquisarAsync(
            new ConhecimentoSearchDto { IdEspacoDeConhecimento = espaco.Id }, cancelado.Token));
        Assert.Equal(0, uow.Commits);
    }

    [Fact]
    public async Task PesquisaNaoMisturaEspacosNemProjetos()
    {
        var direto = await Servico.AdicionarAsync(Entrada);
        var projeto = new Projeto(espaco.Id, "projeto"); projetos.Itens[projeto.Id] = projeto;
        var vinculado = await Servico.AdicionarAsync(Entrada with { IdProjeto = projeto.Id });
        var outro = new EspacoDeConhecimento(Guid.NewGuid(), "outro"); espacos.Itens[outro.Id] = outro;
        await Servico.AdicionarAsync(Entrada with { IdEspacoDeConhecimento = outro.Id });
        Assert.Equal(direto.Id, Assert.Single(await Servico.PesquisarAsync(new ConhecimentoSearchDto
            { IdEspacoDeConhecimento = espaco.Id, SomenteSemProjeto = true })).Id);
        Assert.Equal(vinculado.Id, Assert.Single(await Servico.PesquisarAsync(new ConhecimentoSearchDto
            { IdEspacoDeConhecimento = espaco.Id, IdProjeto = projeto.Id })).Id);
        Assert.Equal(2, (await Servico.PesquisarAsync(new ConhecimentoSearchDto { IdEspacoDeConhecimento = espaco.Id })).Count);
    }

    [Fact]
    public void AppServiceSemPortaDePersistenciaFalhaNaResolucao()
    {
        using var scope = provider.CreateScope();
        var erro = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IConhecimentoAppService>());
        Assert.Contains(nameof(IConhecimentoRepository), erro.Message);
        Assert.Null(scope.ServiceProvider.GetService<IConhecimentoRepository>());
    }

    public void Dispose() => provider.Dispose();
    private sealed class Uow : IUnitOfWork
    {
        public int Commits { get; private set; }
        public bool Falhar { get; set; }
        public Task SalvarAlteracoesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Falhar) throw new InvalidOperationException("falha simulada no commit");
            Commits++; return Task.CompletedTask;
        }
    }
    private class EmMemoria<TEntity> : IRepository<TEntity> where TEntity : EntidadeBase
    {
        public Dictionary<Guid, TEntity> Itens { get; } = [];
        public List<TEntity> Atualizados { get; } = [];
        public Task<TEntity?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Itens.GetValueOrDefault(id)); }
        public Task AdicionarAsync(TEntity entidade, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Itens[entidade.Id] = entidade; return Task.CompletedTask; }
        public void Atualizar(TEntity entidade) => Atualizados.Add(entidade);
        public void Remover(TEntity entidade) => Itens.Remove(entidade.Id);
    }
    private sealed class EspacosEmMemoria : EmMemoria<EspacoDeConhecimento>, IEspacoDeConhecimentoRepository
    {
        public Task<IReadOnlyList<EspacoDeConhecimento>> ListarDoUsuarioAsync(Guid idUsuario, string? trechoDoNome,
            bool incluirArquivados, int limite, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class ProjetosEmMemoria : EmMemoria<Projeto>, IProjetoRepository
    {
        public Task<IReadOnlyList<Projeto>> ListarDoEspacoAsync(Guid idEspacoDeConhecimento, string? trechoDoNome,
            bool incluirArquivados, int limite, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class ConhecimentosEmMemoria : EmMemoria<Conhecimento>, IConhecimentoRepository
    {
        public int Consultas { get; private set; }
        public ConhecimentoSearchDto? UltimoFiltro { get; private set; }
        public Task<IReadOnlyList<Conhecimento>> ListarDoEspacoAsync(ConhecimentoSearchDto filtro, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Consultas++; UltimoFiltro = filtro;
            return Task.FromResult<IReadOnlyList<Conhecimento>>(Itens.Values.Where(item =>
                item.IdEspacoDeConhecimento == filtro.IdEspacoDeConhecimento &&
                (filtro.IdProjeto is null || item.IdProjeto == filtro.IdProjeto) && (!filtro.SomenteSemProjeto || item.IdProjeto is null) &&
                (filtro.IncluirInativosOuSubstituidos || item.Status is not (StatusDoConhecimento.Inativo or StatusDoConhecimento.Substituido)) &&
                (filtro.Tipo is null || item.Tipo == filtro.Tipo) && (filtro.Status is null || item.Status == filtro.Status) &&
                (filtro.Tag is null || item.Tags.Contains(filtro.Tag, StringComparer.OrdinalIgnoreCase)) &&
                (filtro.ValidoEm is null || item.EstaValidoEm(filtro.ValidoEm.Value))).Take(filtro.Limite).ToArray());
        }
        public Task<(int Quantidade, long Caracteres)> MedirEscopoAsync(Guid idEspaco, Guid? idProjeto, CancellationToken cancellationToken = default)
        {
            var itens = Itens.Values.Where(item => item.IdEspacoDeConhecimento == idEspaco && item.IdProjeto == idProjeto &&
                item.Status is not (StatusDoConhecimento.Inativo or StatusDoConhecimento.Substituido)).ToArray();
            return Task.FromResult((itens.Length, itens.Sum(item => (long)(item.Conteudo ?? "").Length)));
        }
    }
}
