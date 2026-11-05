using System.ComponentModel;
using Dante.Infrastructure.Agentes;

namespace Dante.Tests;

// Injeção de falhas no orquestrador usado pelo catch real. Não equivale a teste de kernel/systemd.
public sealed class LimpezaDaInicializacaoTests
{
    [Theory]
    [InlineData("pidfd")]
    [InlineData("espera-bootstrap")]
    [InlineData("stop")]
    [InlineData("populated")]
    [InlineData("observacao")]
    [InlineData("espera-confirmacao")]
    public async Task FalhaDeUmaEtapaNaoImpedeAsDemaisNemMascaraOriginal(string cenario)
    {
        var original = new IOException("falha original de inicialização");
        var sinal = new Win32Exception(1, "pidfd_send_signal recusado");
        var stop = new IOException("systemctl stop recusado");
        var ordem = new List<string>();
        var resultado = await LimpezaDaInicializacao.TentarAsync(original,
            token =>
            {
                ordem.Add("bootstrap");
                if (cenario == "pidfd") throw sinal;
                return cenario == "espera-bootstrap" ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask;
            },
            token =>
            {
                Assert.False(token.IsCancellationRequested);
                ordem.Add("stop");
                if (cenario == "stop") throw stop;
                return Task.CompletedTask;
            },
            token =>
            {
                Assert.False(token.IsCancellationRequested);
                ordem.Add("confirmar");
                if (cenario == "populated") throw new IOException("cgroup ainda populated 1");
                if (cenario == "observacao") throw new UnauthorizedAccessException("cgroup.events inacessível");
                return cenario == "espera-confirmacao" ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(100));
        Assert.Equal(["bootstrap", "stop", "confirmar"], ordem);
        Assert.NotNull(resultado);
        Assert.Equal(2, resultado.InnerExceptions.Count);
        Assert.Same(original, resultado.InnerExceptions[0]);
        var limpeza = resultado.InnerExceptions[1];
        if (cenario is "espera-bootstrap" or "espera-confirmacao") Assert.IsType<TimeoutException>(limpeza);
        else Assert.IsType<InvalidOperationException>(limpeza);
        if (cenario == "pidfd") Assert.Same(sinal, limpeza.InnerException);
        if (cenario == "stop") Assert.Same(stop, limpeza.InnerException);
    }

    [Fact]
    public async Task TodasAsFalhasSaoAgregadasEConfirmacaoSempreETentada()
    {
        var original = new InvalidOperationException("handshake inválido");
        var sinal = new Win32Exception("sinal recusado");
        var stop = new IOException("stop recusado");
        var confirmacao = new IOException("populated 1");
        var chamadas = new List<string>();
        var erro = await LimpezaDaInicializacao.TentarAsync(original,
            _ => { chamadas.Add("bootstrap"); throw sinal; },
            _ => { chamadas.Add("stop"); throw stop; },
            _ => { chamadas.Add("confirmar"); throw confirmacao; });
        Assert.Equal(["bootstrap", "stop", "confirmar"], chamadas);
        Assert.NotNull(erro);
        Assert.Equal(4, erro.InnerExceptions.Count);
        Assert.Same(original, erro.InnerExceptions[0]);
        Assert.Same(sinal, erro.InnerExceptions[1].InnerException);
        Assert.Same(stop, erro.InnerExceptions[2].InnerException);
        Assert.Same(confirmacao, erro.InnerExceptions[3].InnerException);
    }

    [Fact]
    public async Task TimeoutDoStopNaoImpedeConfirmacaoComPrazoNovo()
    {
        var confirmou = false;
        var erro = await LimpezaDaInicializacao.TentarAsync(new IOException("original"),
            _ => Task.CompletedTask,
            token => Task.Delay(Timeout.Infinite, token),
            token => { Assert.False(token.IsCancellationRequested); confirmou = true; return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(100));
        Assert.True(confirmou);
        Assert.NotNull(erro);
        Assert.IsType<TimeoutException>(erro.InnerExceptions[1]);
        Assert.Contains("parar a unidade", erro.InnerExceptions[1].Message);
    }

    [Fact]
    public async Task CancelamentoOriginalNaoCancelaAsEtapasDeResgate()
    {
        using var cancelado = new CancellationTokenSource();
        cancelado.Cancel();
        var original = new OperationCanceledException(cancelado.Token);
        var chamadas = 0;
        Task Executar(CancellationToken token) { Assert.False(token.IsCancellationRequested); chamadas++; return Task.CompletedTask; }
        Assert.Null(await LimpezaDaInicializacao.TentarAsync(original, Executar, Executar, Executar));
        Assert.Equal(3, chamadas);
    }

    [Fact]
    public async Task EsperaQueIgnoraTokenNaoImpedeStop()
    {
        var pendente = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parou = false;
        var confirmou = false;
        var erro = await LimpezaDaInicializacao.TentarAsync(new IOException("original"),
            _ => pendente.Task,
            _ => { parou = true; return Task.CompletedTask; },
            _ => { confirmou = true; return Task.CompletedTask; }, TimeSpan.FromMilliseconds(100));
        Assert.True(parou); Assert.True(confirmou);
        Assert.NotNull(erro);
        Assert.IsType<TimeoutException>(erro.InnerExceptions[1]);
        pendente.SetResult();
    }
}
