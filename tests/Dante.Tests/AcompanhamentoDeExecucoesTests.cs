using Dante.Application.SegurancaDoBrain;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;

namespace Dante.Tests;

// #305 sem banco: regras puras do acompanhamento (códigos, proprietário, redaction, classificação da espera) e a
// fronteira neutra de interface dos contratos.
public sealed class AcompanhamentoDeExecucoesTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("J000123", 123, true)]
    [InlineData("j7", 7, true)]
    [InlineData("E42", 42, false)]
    [InlineData("e0042", 42, false)]
    public void CodigoExibidoViraNumeroETipo(string codigo, long numero, bool avulsa)
    {
        Assert.True(Execucao.TentarInterpretarCodigo(codigo, out var lido, out var ehAvulsa));
        Assert.Equal((numero, avulsa), (lido, ehAvulsa));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("E")]
    [InlineData("E0")]
    [InlineData("S000001")]
    [InlineData("E-1")]
    [InlineData("E 1")]
    [InlineData("E1x")]
    [InlineData("E١٢")]
    [InlineData("E999999999999999999999")]
    public void CodigoInvalidoNaoEInterpretado(string? codigo) =>
        Assert.False(Execucao.TentarInterpretarCodigo(codigo, out _, out _));

    [Fact]
    public void ProprietarioComparaCanalEUsuarioERegistraAutor()
    {
        var dono = new ProprietarioDaExecucao("telegram", 1001);
        Assert.True(dono.EDono("telegram", 1001));
        Assert.False(dono.EDono("telegram", 1002));
        Assert.False(dono.EDono("web", 1001));
        Assert.False(dono.EDono("Telegram", 1001));
        Assert.Equal("telegram:1001", dono.Autor);
        Assert.Throws<ArgumentException>(() => new ProprietarioDaExecucao(" ", 1));
        Assert.Throws<ArgumentException>(() => new ProprietarioDaExecucao(new string('c', Execucao.TamanhoMaximoDoCanal + 1), 1));
    }

    [Fact]
    public void MotivoDoPedidoViraUmaLinhaCortadaESemSegredo()
    {
        Assert.Null(PedidoDeCancelamento.MotivoSeguro("  \n "));
        Assert.Equal("não preciso mais", PedidoDeCancelamento.MotivoSeguro(" não\npreciso\t mais "));
        Assert.Equal(PedidoDeCancelamento.TamanhoMaximoDoMotivo, PedidoDeCancelamento.MotivoSeguro(new string('a', 500))!.Length);
        Assert.Equal("[conteúdo protegido]", PedidoDeCancelamento.MotivoSeguro("use o token=abcd1234efgh"));
        var dados = PedidoDeCancelamento.Dados(new ProprietarioDaExecucao("telegram", 7), "motivo");
        Assert.Equal("motivo", PedidoDeCancelamento.Motivo(dados));
        Assert.Null(PedidoDeCancelamento.Motivo("não é json"));
        Assert.Equal("Cancelada pelo dono.", PedidoDeCancelamento.MotivoDaTransicao(null));
    }

    [Fact]
    public void MotivoComCredencialCortadaPeloLimiteNaoGuardaFragmento()
    {
        // A credencial começa antes do limite e termina depois: cortado primeiro, o pedaço "ghp_" + 16 caracteres não
        // casaria com o padrão (exige 20+) e seria guardado. Protegido o texto integral, nenhum fragmento sobra.
        const string credencial = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var prefixo = new string('m', 179) + " ";
        Assert.True(prefixo.Length < PedidoDeCancelamento.TamanhoMaximoDoMotivo);
        Assert.True(prefixo.Length + credencial.Length > PedidoDeCancelamento.TamanhoMaximoDoMotivo);
        Assert.False(ProtecaoDeSegredos.ContemSegredo(
            (prefixo + credencial)[..PedidoDeCancelamento.TamanhoMaximoDoMotivo]));

        var seguro = PedidoDeCancelamento.MotivoSeguro(prefixo + credencial + " depois do corte");

        Assert.Equal("[conteúdo protegido]", seguro);
        Assert.DoesNotContain("ghp_", seguro);
        Assert.DoesNotContain("ABCDEFGH", seguro);
    }

    [Fact]
    public void TextoProtegidoTambemRespeitaOTamanho()
    {
        var protegido = AcompanhamentoDeExecucoesAppService.Seguro("senha=abcd1234 " + new string('z', 300), 12);
        Assert.Equal("[conteúdo p…", protegido);
        Assert.Equal(12, protegido!.Length);
        Assert.Equal("[conteúdo protegido]", AcompanhamentoDeExecucoesAppService.Seguro("senha=abcd1234", 200));
    }

    [Fact]
    public void TextoExibidoESeguroInteiroAntesDoCorte()
    {
        Assert.Null(AcompanhamentoDeExecucoesAppService.Seguro(null, 10));
        Assert.Equal("a b", AcompanhamentoDeExecucoesAppService.Seguro("a\r\nb", 10));
        Assert.Equal("aaaaaaaaa…", AcompanhamentoDeExecucoesAppService.Seguro(new string('a', 50), 10));
        // O segredo está depois do ponto de corte: o texto inteiro é protegido, não só a parte exibida; o texto
        // protegido também respeita o tamanho pedido.
        var protegido = AcompanhamentoDeExecucoesAppService.Seguro(new string('x', 20) + " ghp_abcdefghijklmnopqrstuvwxyz0123", 10);
        Assert.Equal("[conteúdo…", protegido);
        Assert.DoesNotContain("x", protegido);
    }

    [Fact]
    public void ContextoExibidoNuncaTrazODiretorio()
    {
        var (modo, alias, vinculados) = AcompanhamentoDeExecucoesAppService.Contexto(
            """{"modo":"Repository","alias":"@front","diretorio":"/home/u/front","vinculados":[{"alias":"@back","caminho":"/home/u/back"}]}""");
        Assert.Equal(("Repository", "@front"), (modo, alias));
        Assert.Equal(["@back"], vinculados);
        Assert.Equal(("", (string?)null, 0), (AcompanhamentoDeExecucoesAppService.Contexto("{").Modo,
            AcompanhamentoDeExecucoesAppService.Contexto("{").Alias, AcompanhamentoDeExecucoesAppService.Contexto("{").Vinculados.Count));
    }

    [Theory]
    [InlineData(EstadoDaExecucao.Interrompida, MotivoDaEspera.Interrompida)]
    [InlineData(EstadoDaExecucao.Bloqueada, MotivoDaEspera.Bloqueada)]
    [InlineData(EstadoDaExecucao.AguardandoAprovacao, MotivoDaEspera.AguardandoAprovacao)]
    [InlineData(EstadoDaExecucao.AguardandoEntrada, MotivoDaEspera.AguardandoEntrada)]
    [InlineData(EstadoDaExecucao.AguardandoDelegacoes, MotivoDaEspera.AguardandoDelegacoes)]
    [InlineData(EstadoDaExecucao.EmExecucao, null)]
    [InlineData(EstadoDaExecucao.Concluida, null)]
    [InlineData(EstadoDaExecucao.Falhou, null)]
    [InlineData(EstadoDaExecucao.Cancelada, null)]
    public void EsperaSegueOEstadoDaExecucao(EstadoDaExecucao estado, MotivoDaEspera? esperado) =>
        Assert.Equal(esperado, AcompanhamentoDeExecucoesAppService.Espera(Em(estado), [], Agora));

    [Fact]
    public void PendenteDistingueFilaNovaTentativaEReivindicada()
    {
        var execucao = Em(EstadoDaExecucao.Pendente);
        var etapa = new EtapaDaExecucao(execucao, 1, TipoDeEtapa.ProcessoAvulso, false, PoliticaDeRetomada.Automatica, Agora);
        var novo = TrabalhoDaFila.AvancarExecucao(execucao, etapa, "k1", Agora);
        Assert.Equal(MotivoDaEspera.NaFila, AcompanhamentoDeExecucoesAppService.Espera(execucao, [novo], Agora));
        // Sem tentativa anterior, um disponivel_em futuro (timer) ainda é fila, não nova tentativa.
        var agendado = TrabalhoDaFila.AvancarExecucao(execucao, etapa, "k2", Agora, disponivelEm: Agora.AddMinutes(5));
        Assert.Equal(MotivoDaEspera.NaFila, AcompanhamentoDeExecucoesAppService.Espera(execucao, [agendado], Agora));
        // Sem trabalho ativo (registro inconsistente), nunca "rodando".
        Assert.Equal(MotivoDaEspera.NaFila, AcompanhamentoDeExecucoesAppService.Espera(execucao, [], Agora));
    }

    // A fronteira é neutra de interface: nenhum contrato ou caso de uso do acompanhamento conhece o Telegram.
    [Fact]
    public void ContratosECasosDeUsoNaoDependemDoTelegram()
    {
        Type[] tipos =
        [
            typeof(IAcompanhamentoDeExecucoesAppService), typeof(AcompanhamentoDeExecucoesAppService),
            typeof(IControleDeExecucoesAppService), typeof(ControleDeExecucoesAppService), typeof(ProprietarioDaExecucao),
            typeof(ResumoDaExecucaoDto), typeof(DetalheDaExecucaoDto), typeof(EtapaExibidaDto), typeof(PainelDeExecucoesDto),
            typeof(CancelamentoDeExecucaoDto), typeof(MotivoDaEspera), typeof(CanalDeCancelamento)
        ];
        Assert.All(tipos, tipo =>
        {
            Assert.Equal("Dante.Application", tipo.Assembly.GetName().Name);
            Assert.DoesNotContain("Telegram", tipo.FullName!, StringComparison.OrdinalIgnoreCase);
            var membros = tipo.GetMembers().SelectMany(m => m switch
            {
                System.Reflection.PropertyInfo p => [p.PropertyType],
                System.Reflection.MethodInfo mi => mi.GetParameters().Select(x => x.ParameterType).Append(mi.ReturnType),
                System.Reflection.ConstructorInfo c => c.GetParameters().Select(x => x.ParameterType),
                _ => []
            });
            Assert.DoesNotContain(membros, t => t.FullName?.Contains("Telegram", StringComparison.OrdinalIgnoreCase) == true ||
                                                t.Assembly.GetName().Name == "Dante.Worker");
        });
    }

    private static Execucao Em(EstadoDaExecucao estado)
    {
        var execucao = new Execucao(1, TipoDeExecucao.Avulsa, "telegram", 1, "{}", null, Agora);
        switch (estado)
        {
            case EstadoDaExecucao.Pendente: break;
            case EstadoDaExecucao.Cancelada: execucao.Cancelar(null, Agora); break;
            default:
                execucao.Iniciar(Agora);
                switch (estado)
                {
                    case EstadoDaExecucao.Interrompida: execucao.Interromper("m"); break;
                    case EstadoDaExecucao.Bloqueada: execucao.Bloquear("m"); break;
                    case EstadoDaExecucao.AguardandoAprovacao: execucao.AguardarAprovacao(); break;
                    case EstadoDaExecucao.AguardandoEntrada: execucao.AguardarEntrada(); break;
                    case EstadoDaExecucao.AguardandoDelegacoes: execucao.AguardarDelegacoes(); break;
                    case EstadoDaExecucao.Concluida: execucao.Concluir(Agora); break;
                    case EstadoDaExecucao.Falhou: execucao.Falhar("e", Agora); break;
                }
                break;
        }
        Assert.Equal(estado, execucao.Estado);
        return execucao;
    }
}
