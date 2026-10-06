using Dante.Application.CapturaDeConhecimento;
using Dante.Application.Conhecimentos;
using Dante.Application.ConversaDoBrain;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Infrastructure.ConversaDoBrain;

namespace Dante.Tests;

public sealed class CasosDeConversaTests
{
    [Fact]
    public async Task CapturaSelecionadaPreservaCitacaoEPreparaConfirmacaoUnica()
    {
        var contexto = Criar(new("chat", "documente isso", "mensagem:2", "evidência selecionada", "mensagem:1"));
        var captura = new CapturaSimulada();
        var caso = new CapturaDaConversa(captura);
        var resposta = await caso.CapturarAsync(contexto, contexto.Pedido.TrechoSelecionado!, TipoDeConhecimento.Nota, default);
        Assert.Contains("Não é um fato confirmado", resposta);
        Assert.Equal(NaturezaDoConteudo.FonteSelecionada, captura.Recebida!.Natureza);
        Assert.Equal("mensagem:1", captura.Recebida.Proveniencia.ReferenciaDaFonte);
        Assert.Equal(contexto.Acesso.IdUsuario, captura.Recebida.Proveniencia.IdResponsavel);
        Assert.Equal("captura", contexto.Estado.Pendente!.Acao);
        Assert.True(contexto.ConsumirPendente());
        Assert.False(contexto.ConsumirPendente());
    }

    [Fact]
    public async Task CapturaDeInferenciaMantemTipoEOrigemDoUsuario()
    {
        var contexto = Criar(); var captura = new CapturaSimulada();
        await new CapturaDaConversa(captura).CapturarAsync(contexto, "inferência: hipótese a verificar", TipoDeConhecimento.Nota, default);
        Assert.Equal(TipoDeConhecimento.Inferencia, captura.Recebida!.Tipo);
        Assert.Equal("hipótese a verificar", captura.Recebida.Conteudo);
        Assert.Equal(NaturezaDoConteudo.DitoPeloUsuario, captura.Recebida.Natureza);
        Assert.Equal("mensagem:2", captura.Recebida.Proveniencia.ReferenciaDaFonte);
    }

    [Fact]
    public async Task ConsultaInvalidaEOrigemAmbiguaNaoAcessamDependencias()
    {
        var caso = new ConsultaDaConversa(null!, null!, null!);
        await Assert.ThrowsAsync<ArgumentException>(() => caso.ConsultarAsync(Criar(), "", false, default));
        Assert.Contains("inequívoco", await caso.OrigemAsync(Criar(), null, default));
    }

    [Fact]
    public async Task ConfirmacaoDeCandidatoProtegidoFalhaAntesDeMutacao()
    {
        var contexto = Criar(); var alvo = new AlvoDeConversaDto(Guid.NewGuid(), 1, "candidato",
            TipoDeConhecimento.Fato, null, Sensibilidade.Secreto, "protegido");
        var caso = new AlteracoesDaConversa(null!, null!, null!, null!, null!, null!);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caso.ExecutarAsync(contexto, contexto.Pendente("captura", alvo), default));
    }

    [Fact]
    public void CorrecaoPreparaAlvoSemGravarEIsolaConversa()
    {
        var contexto = Criar();
        var alvo = new AlvoDeConversaDto(Guid.NewGuid(), 3, "conhecimento", TipoDeConhecimento.Fato,
            StatusDoConhecimento.Confirmado, Sensibilidade.Pessoal, "anterior");
        var caso = new AlteracoesDaConversa(null!, null!, null!, null!, null!, null!);
        Assert.Contains("Qual é a informação correta", caso.PrepararAlteracao(contexto, "correcao", alvo));
        Assert.Equal("texto_correcao", contexto.Estado.Pendente!.Acao);
        Assert.Contains("confirmar", caso.PrepararAlteracao(contexto, "correcao", alvo, "correto"));
        Assert.Equal(3, contexto.Estado.Pendente!.Alvo!.Revisao);
        Assert.Equal("correto", contexto.Estado.Pendente.Conteudo);
        Assert.Null(Criar().Estado.Pendente);
    }

    [Fact]
    public async Task ContextoEAvaliacaoRejeitamCamposInvalidosSemPersistir()
    {
        Assert.Contains("nada foi alterado", await new ContextoDeTrabalhoDaConversa(null!)
            .AtualizarContextoAsync(Criar(), "campo desconhecido: valor", default));
        Assert.Contains("nada foi registrado", await new AvaliacaoDaConversa(null!)
            .AvaliarAsync(Criar(), "concluída: talvez", default));
        Assert.Contains("não avaliados", await new AvaliacaoDaConversa(null!).AvaliarAsync(Criar(), "", default));
    }

    private static ContextoDaConversa Criar(PedidoDeConversaDto? pedido = null)
    {
        var acesso = new AcessoAoBrain(Guid.NewGuid(), Guid.NewGuid(), null);
        return new(acesso, pedido ?? new("chat", "confirmar", "mensagem:2"),
            new(Guid.NewGuid(), acesso.IdUsuario, acesso.IdEspacoDeConhecimento, null, "chat"),
            new EstadoDeConversaDoBrainEmMemoria());
    }

    private sealed class CapturaSimulada : ICapturaDeConhecimentoAppService
    {
        public CapturaDeConhecimentoDto? Recebida { get; private set; }
        public Task<CandidatoDeConhecimentoDto> CapturarAsync(CapturaDeConhecimentoDto captura, CancellationToken cancellationToken = default)
        {
            Recebida = captura;
            return Task.FromResult(new CandidatoDeConhecimentoDto(Guid.NewGuid(), captura.IdEspacoDeConhecimento,
                captura.IdProjeto, captura.Tipo, captura.Conteudo, captura.Sensibilidade, captura.Natureza,
                captura.Modo, EstadoDoCandidato.Pendente, 1, null, []));
        }
        public Task<CandidatoDeConhecimentoDto> CorrigirAsync(Guid idEspaco, Guid? idProjeto, Guid idCandidato, int revisaoEsperada,
            CapturaDeConhecimentoDto correcao, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid> ConfirmarAsync(Guid idEspaco, Guid? idProjeto, Guid idCandidato, int revisaoEsperada,
            ProvenienciaDto responsavel, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RejeitarAsync(Guid idEspaco, Guid? idProjeto, Guid idCandidato, int revisaoEsperada,
            ProvenienciaDto responsavel, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DescartarAsync(Guid idEspaco, Guid? idProjeto, Guid idCandidato, int revisaoEsperada,
            ProvenienciaDto responsavel, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandidatoDeConhecimentoDto>> ListarPendentesAsync(Guid idEspaco, Guid? idProjeto, int limite = 50,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
