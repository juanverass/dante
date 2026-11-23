using System.Text.Json;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Worker.Coordenacao;
using Dante.Worker.Telegram;

namespace Dante.Tests;

// #311 (AD-72), PR 2: gateway das ferramentas de sessão. Identidade só do canal, catálogo fechado, concessão do escopo,
// schema e política, nessa ordem; política Nunca executa uma vez pelo caso de uso da ferramenta, e ação protegida nunca
// executa por chamada direta. As ferramentas atuais do dante_coordenacao estão todas no catálogo com política Nunca.
public sealed class GatewayDeFerramentasTests
{
    private static readonly JsonElement Vazio = JsonSerializer.SerializeToElement(new { });

    private static SessaoDaFerramentaDto Sessao(params string[] concedidas) =>
        new("S1", ServidorMcpDeCoordenacao.Nome, concedidas);

    private static ChamadaDeFerramentaDto Chamada(SessaoDaFerramentaDto sessao, string ferramenta,
        Action<JsonElement>? schema = null) => new(sessao, ferramenta, Vazio, schema ?? (_ => { }));

    [Fact]
    public async Task FerramentaConcedidaComPoliticaNuncaExecutaUmaVezPeloCasoDeUso()
    {
        var gateway = new GatewayDeFerramentas();
        var execucoes = 0;
        var resultado = await gateway.ChamarAsync(Chamada(Sessao("estado_da_execucao"), "estado_da_execucao"), _ =>
        {
            execucoes++;
            return Task.FromResult("ok");
        });
        Assert.Equal(("ok", 1), (resultado, execucoes));
    }

    [Theory]
    // Fora do catálogo, não concedida ao escopo, nome com servidor embutido: a mesma recusa, sem executar.
    [InlineData("apagar_tudo", "estado_da_execucao")]
    [InlineData("consultar_agente", "estado_da_execucao")]
    [InlineData("dante_coordenacao.estado_da_execucao", "estado_da_execucao")]
    [InlineData("", "estado_da_execucao")]
    public async Task DesconhecidaOuNaoConcedidaERecusadaSemExecutar(string ferramenta, string concedida)
    {
        var gateway = new GatewayDeFerramentas();
        var recusa = await Assert.ThrowsAsync<ChamadaRecusadaException>(() =>
            gateway.ChamarAsync(Chamada(Sessao(concedida), ferramenta), _ => Falhar()));
        Assert.Equal(GatewayDeFerramentas.FerramentaDesconhecida, recusa.Message);
    }

    [Fact]
    public async Task SemIdentidadeDoCanalNadaEAvaliado()
    {
        var gateway = new GatewayDeFerramentas();
        foreach (var sessao in new[]
                 {
                     new SessaoDaFerramentaDto("", ServidorMcpDeCoordenacao.Nome, ["estado_da_execucao"]),
                     new SessaoDaFerramentaDto("S1", " ", ["estado_da_execucao"]),
                     new SessaoDaFerramentaDto("S1", ServidorMcpDeCoordenacao.Nome, null!),
                     null!
                 })
        {
            var recusa = await Assert.ThrowsAsync<ChamadaRecusadaException>(() =>
                gateway.ChamarAsync(Chamada(sessao, "estado_da_execucao"), _ => Falhar()));
            Assert.Equal(GatewayDeFerramentas.SessaoNaoIdentificada, recusa.Message);
        }
    }

    [Fact]
    public async Task SchemaEAplicadoAntesDeExecutarESemSchemaNadaExecuta()
    {
        var gateway = new GatewayDeFerramentas();
        var invalido = await Assert.ThrowsAsync<ArgumentException>(() => gateway.ChamarAsync(
            Chamada(Sessao("entregar_resultado"), "entregar_resultado", _ => throw new ArgumentException("Argumento não permitido.")),
            _ => Falhar()));
        Assert.Equal("Argumento não permitido.", invalido.Message);
        await Assert.ThrowsAsync<ChamadaRecusadaException>(() => gateway.ChamarAsync(
            new ChamadaDeFerramentaDto(Sessao("entregar_resultado"), "entregar_resultado", Vazio, null!), _ => Falhar()));
    }

    [Fact]
    public async Task AcaoProtegidaNuncaExecutaPorChamadaDireta()
    {
        // Mesmo concedida e com schema válido: a política Sempre exige o pedido de aprovação, que não passa por aqui.
        var gateway = new GatewayDeFerramentas();
        var recusa = await Assert.ThrowsAsync<ChamadaRecusadaException>(() => gateway.ChamarAsync(
            new ChamadaDeFerramentaDto(new SessaoDaFerramentaDto("S1", "dante", ["entregar_artefato_do_roteiro"]),
                "entregar_artefato_do_roteiro", Vazio, _ => { }), _ => Falhar()));
        Assert.Equal(GatewayDeFerramentas.AcaoProtegida, recusa.Message);
    }

    // O que a CLI vê em cada perfil do dante_coordenacao é exatamente o que o catálogo do gateway conhece, com política
    // Nunca: uma ferramenta nova no servidor sem entrada no catálogo seria recusada pelo gateway (e este teste falha).
    [Fact]
    public void TodaFerramentaDeTodoPerfilDoCoordenacaoEstaNoCatalogoComPoliticaNunca()
    {
        var expostas = Enum.GetValues<PerfilDeCoordenacao>()
            .SelectMany(p => ServidorMcpDeCoordenacao.Catalogo(p).Select(f => f.Nome)).Distinct().Order(StringComparer.Ordinal);
        var catalogo = CatalogoDeAcoes.Todas.Where(a => a.Nome.StartsWith(CatalogoDeAcoes.PrefixoDoCoordenacao, StringComparison.Ordinal))
            .Select(a => a.Nome[CatalogoDeAcoes.PrefixoDoCoordenacao.Length..]).Order(StringComparer.Ordinal);
        Assert.Equal(catalogo, expostas);
        Assert.All(CatalogoDeAcoes.Todas.Where(a => a.Nome.StartsWith(CatalogoDeAcoes.PrefixoDoCoordenacao, StringComparison.Ordinal)),
            a => Assert.Equal(PoliticaDeAprovacao.Nunca, a.Politica));
    }

    // ---- adaptador do Telegram: transporte e apresentação, sem regra ----

    [Fact]
    public void BotaoLevaSoOPedidoEOHashExibidoECabeNoLimiteDoTelegram()
    {
        var id = Guid.NewGuid();
        var aprovar = TelegramAcaoCallback.Aprovacao(id, "0123456789ab");
        var recusar = TelegramAcaoCallback.Recusa(id);
        Assert.InRange(aprovar.Length, 1, 64);
        Assert.True(TelegramAcaoCallback.TryParse(aprovar, out var a));
        Assert.Equal((id, true, "0123456789ab"), (a.Id, a.Aprovar, a.Hash));
        Assert.True(TelegramAcaoCallback.TryParse(recusar, out var r));
        Assert.Equal((id, false, (string?)null), (r.Id, r.Aprovar, r.Hash));
        foreach (var invalido in new[]
                 {
                     null, "", "ac", $"ac:{id:N}", $"ac:{id:N}:0123456789AB:a", $"ac:{id:N}:0123456789a:a",
                     $"ac:{id:N}:0123456789ab:r", $"ac:{id:N}:x", $"ap:{id:N}:r", $"ac:{id}:r", $"ac:{id:N}:0123456789ab:a:x"
                 })
            Assert.False(TelegramAcaoCallback.TryParse(invalido, out _), invalido);
    }

    [Fact]
    public void ResumoMostraHashCurtoPrazoEParametrosJaRedigidosSemPayload()
    {
        var dto = new AprovacaoDeAcaoDto(Guid.NewGuid(), CatalogoDeAcoes.EntregarArtefatoDoRoteiro.Nome, "Commit local",
            ClasseDeRisco.EscritaInterna, "@dante", "E12", "dono", "telegram", 1001, new string('a', 64),
            EstadoDaAprovacaoDeAcao.Pendente, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(24), null, null,
            null, [new ParametroExibidoDto("mensagem", "[conteúdo protegido]")]);
        var texto = TelegramAprovacoesDeAcao.Texto(dto);
        Assert.Contains("Hash: aaaaaaaaaaaa", texto);
        Assert.DoesNotContain(new string('a', 13), texto);
        Assert.Contains("@dante", texto);
        Assert.Contains("E12", texto);
        Assert.Contains("mensagem: [conteúdo protegido]", texto);
        Assert.Contains("02/01 00:00 UTC", texto);
        var teclado = TelegramAprovacoesDeAcao.Teclado(dto);
        Assert.Equal([TelegramAcaoCallback.Aprovacao(dto.Id, dto.HashCurto), TelegramAcaoCallback.Recusa(dto.Id)],
            teclado.Rows.SelectMany(l => l).Select(b => b.CallbackData));
    }

    // O adaptador não decide: não conhece a entidade, o repositório nem o domínio da decisão, só o caso de uso.
    [Fact]
    public void AdaptadorDoTelegramNaoTemRegraDeAutorizacao()
    {
        var tipos = new[] { typeof(TelegramAprovacoesDeAcao), typeof(TelegramAcaoCallback) };
        var proibidos = new[] { typeof(AprovacaoDeAcao), typeof(IAprovacaoDeAcaoRepository), typeof(PayloadDaAcao),
            typeof(IUnitOfWorkDaOrquestracao) };
        foreach (var tipo in tipos)
        {
            var usados = tipo.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
                .Concat(tipo.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                                       System.Reflection.BindingFlags.Public).Select(f => f.FieldType))
                .Concat(tipo.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
                                        System.Reflection.BindingFlags.DeclaredOnly)
                    .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)));
            Assert.DoesNotContain(usados, u => proibidos.Contains(u));
        }
    }

    private static Task<string> Falhar() => throw new InvalidOperationException("Não deveria executar.");
}
