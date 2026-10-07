using Microsoft.Extensions.Configuration;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dante.Application.Planilhas;
using Dante.Infrastructure.Google;

namespace Dante.Tests;

// #224: OAuth local com callback loopback real, PKCE S256, state validado, credencial cifrada, renovação automática,
// revogação e reinício sem novo login. O GoogleSheetsFalso faz o papel dos endpoints do Google.
public sealed class GoogleOAuthServiceTests
{
    [Fact]
    public void ConfiguracaoComChaveExternaERecusadaSemExporSegredo()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DANTE_GOOGLE_KEY"] = "segredo" }).Build();
        var falha = Assert.Throws<InvalidOperationException>(() => GoogleOptions.DaConfiguracao(configuration));
        Assert.DoesNotContain("segredo", falha.Message);
        Assert.Contains("MCP", falha.Message);
    }

    [Fact]
    public async Task RevogacaoRecusadaApagaCredencialMasNaoConfirmaRevogacao()
    {
        using var ambiente = new AmbienteDePlanilhas();
        ambiente.Google.StatusDeRevogacao = System.Net.HttpStatusCode.ServiceUnavailable;
        var falha = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => ambiente.OAuth.DesconectarAsync());
        Assert.Contains("revogação no Google não foi confirmada", falha.Message);
        Assert.Null(ambiente.Store.Carregar());
    }

    [Fact]
    public async Task SemClienteConfiguradoNaoIniciaAutorizacao()
    {
        using var ambiente = new AmbienteDePlanilhas(conectado: false, opcoes: new GoogleOptions { DiretorioDaCredencial = Path.GetTempPath() });
        var falha = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => ambiente.OAuth.IniciarAsync());
        Assert.Equal(MotivoDaFalhaDePlanilha.NaoConfigurada, falha.Motivo);
        Assert.Contains("Google__ClientId", falha.Message);
        Assert.False((await ambiente.OAuth.ObterEstadoAsync()).Configurada);
    }

    [Fact]
    public async Task FluxoCompletoComPkceStateECredencialCifradaSobreviveAoReinicio()
    {
        using var ambiente = new AmbienteDePlanilhas(conectado: false);
        var autorizacao = await ambiente.OAuth.IniciarAsync();
        var parametros = Parametros(autorizacao.Url);
        Assert.Equal("https://accounts.google.com/o/oauth2/v2/auth", autorizacao.Url.GetLeftPart(UriPartial.Path));
        Assert.Equal(("cliente-de-teste.apps.googleusercontent.com", "code", "S256", "offline"),
            (parametros["client_id"], parametros["response_type"], parametros["code_challenge_method"], parametros["access_type"]));
        Assert.Contains("https://www.googleapis.com/auth/spreadsheets", parametros["scope"].Split(' '));
        Assert.DoesNotContain("drive", parametros["scope"]);
        var redirect = new Uri(parametros["redirect_uri"]);
        Assert.Equal("127.0.0.1", redirect.Host);

        using var navegador = new HttpClient();
        Assert.Equal(HttpStatusCode.NotFound, (await navegador.GetAsync(new Uri(redirect, "/favicon.ico"))).StatusCode);
        var forjada = await navegador.GetAsync($"{redirect}?state=forjado&code=codigo-valido");
        Assert.Equal(HttpStatusCode.BadRequest, forjada.StatusCode);
        Assert.False(autorizacao.Conclusao.IsCompleted);
        Assert.Null(ambiente.Google.UltimoFormularioDeCodigo);

        var resposta = await navegador.GetAsync($"{redirect}?state={Uri.EscapeDataString(parametros["state"])}&code=codigo-valido&scope=x");
        Assert.Contains("Conta Google conectada", await resposta.Content.ReadAsStringAsync());
        var estado = await autorizacao.Conclusao.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((true, "pessoa@example.com"), (estado.Conectada, estado.Conta));

        var troca = ambiente.Google.UltimoFormularioDeCodigo!;
        Assert.Equal(parametros["code_challenge"], Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(troca["code_verifier"]))));
        Assert.Equal((parametros["redirect_uri"], "authorization_code"), (troca["redirect_uri"], troca["grant_type"]));

        var bytes = File.ReadAllBytes(ambiente.Store.Arquivo);
        Assert.DoesNotContain(GoogleSheetsFalso.RefreshToken, Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("segredo-do-cliente", Encoding.UTF8.GetString(bytes));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(ambiente.Store.Arquivo));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(ambiente.Store.Diretorio));
        }

        // Reinício: outro processo, mesmo disco, sem cliente configurado no ambiente — renova sem novo login.
        using var reiniciado = new GoogleOAuthService(new GoogleOptions { DiretorioDaCredencial = ambiente.Opcoes.DiretorioDaCredencial },
            new GoogleCredentialStore(ambiente.Opcoes.DiretorioDaCredencial), ambiente.Http, ambiente.Relogio);
        Assert.True((await reiniciado.ObterEstadoAsync()).Conectada);
        Assert.Equal("at-2", await reiniciado.ObterTokenDeAcessoAsync(false, CancellationToken.None));
        Assert.Equal(1, ambiente.Google.Renovacoes);
    }

    [Theory]
    [InlineData("recusa")]
    [InlineData("sem-escopo")]
    [InlineData("sem-refresh")]
    public async Task AutorizacaoIncompletaNaoGuardaCredencial(string caso)
    {
        using var ambiente = new AmbienteDePlanilhas(conectado: false);
        if (caso == "sem-escopo") ambiente.Google.Escopo = "openid email";
        if (caso == "sem-refresh") ambiente.Google.SemRefreshToken = true;
        var autorizacao = await ambiente.OAuth.IniciarAsync();
        var parametros = Parametros(autorizacao.Url);
        using var navegador = new HttpClient();
        var consulta = caso == "recusa" ? "error=access_denied" : "code=codigo-valido";
        await navegador.GetAsync($"{parametros["redirect_uri"]}?{consulta}&state={Uri.EscapeDataString(parametros["state"])}");
        var falha = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => autorizacao.Conclusao.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(MotivoDaFalhaDePlanilha.SemPermissao, falha.Motivo);
        Assert.False(ambiente.Store.Existe);
        Assert.False((await ambiente.OAuth.ObterEstadoAsync()).Conectada);
    }

    [Fact]
    public async Task NovaAutorizacaoSubstituiAAnteriorEAutorizacaoExpira()
    {
        using var ambiente = new AmbienteDePlanilhas(conectado: false);
        var primeira = await ambiente.OAuth.IniciarAsync();
        var segunda = await ambiente.OAuth.IniciarAsync();
        Assert.NotEqual(Parametros(primeira.Url)["state"], Parametros(segunda.Url)["state"]);
        Assert.Equal(MotivoDaFalhaDePlanilha.NaoConectada,
            (await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => primeira.Conclusao.WaitAsync(TimeSpan.FromSeconds(10)))).Motivo);

        using var curta = new GoogleOAuthService(ambiente.Opcoes with { TempoParaAutorizar = TimeSpan.FromMilliseconds(200) },
            ambiente.Store, ambiente.Http, ambiente.Relogio);
        var expirada = await curta.IniciarAsync();
        Assert.Contains("expirou", (await Assert.ThrowsAsync<FalhaDePlanilhaException>(() =>
            expirada.Conclusao.WaitAsync(TimeSpan.FromSeconds(10)))).Message);
    }

    [Fact]
    public async Task AccessTokenFicaEmMemoriaERenovaAoExpirarOuQuandoForcado()
    {
        using var ambiente = new AmbienteDePlanilhas();
        Assert.Equal("at-1", await ambiente.OAuth.ObterTokenDeAcessoAsync(false, CancellationToken.None));
        Assert.Equal("at-1", await ambiente.OAuth.ObterTokenDeAcessoAsync(false, CancellationToken.None));
        ambiente.Relogio.Agora += TimeSpan.FromHours(1);
        Assert.Equal("at-2", await ambiente.OAuth.ObterTokenDeAcessoAsync(false, CancellationToken.None));
        Assert.Equal("at-3", await ambiente.OAuth.ObterTokenDeAcessoAsync(true, CancellationToken.None));
        Assert.Equal(3, ambiente.Google.Renovacoes);
    }

    [Fact]
    public async Task AutorizacaoRevogadaNoGoogleViraDesconexaoAcionavel()
    {
        using var ambiente = new AmbienteDePlanilhas();
        ambiente.Google.RefreshInvalido = true;
        var falha = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => ambiente.OAuth.ObterTokenDeAcessoAsync(false, CancellationToken.None));
        Assert.Equal(MotivoDaFalhaDePlanilha.NaoConectada, falha.Motivo);
        Assert.Contains("/google connect", falha.Message);
        var estado = await ambiente.OAuth.ObterEstadoAsync();
        Assert.False(estado.Conectada);
        Assert.Contains("revogada", estado.Problema);
    }

    [Fact]
    public async Task DesconectarRevogaNoGoogleEApagaACredencial()
    {
        using var ambiente = new AmbienteDePlanilhas();
        Assert.True(await ambiente.OAuth.DesconectarAsync());
        Assert.Equal([GoogleSheetsFalso.RefreshToken], ambiente.Google.Revogados);
        Assert.False(ambiente.Store.Existe);
        Assert.False(File.Exists(Path.Combine(ambiente.Store.Diretorio, "chave")));
        Assert.False((await ambiente.OAuth.ObterEstadoAsync()).Conectada);
        Assert.False(await ambiente.OAuth.DesconectarAsync());
        Assert.Equal(MotivoDaFalhaDePlanilha.NaoConectada, (await Assert.ThrowsAsync<FalhaDePlanilhaException>(() =>
            ambiente.OAuth.ObterTokenDeAcessoAsync(false, CancellationToken.None))).Motivo);
    }

    [Fact]
    public void CredencialAdulteradaNaoEAceita()
    {
        using var ambiente = new AmbienteDePlanilhas();
        var bytes = File.ReadAllBytes(ambiente.Store.Arquivo);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(ambiente.Store.Arquivo, bytes);
        var falha = Assert.Throws<FalhaDePlanilhaException>(() => ambiente.Store.Carregar());
        Assert.Contains("ilegível", falha.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task XlsxSolicitaDriveSomentePorOpcaoEExigeAPermissaoConcedida(bool concedida)
    {
        using var ambiente = new AmbienteDePlanilhas(conectado: false);
        using var oauth = new GoogleOAuthService(ambiente.Opcoes with { PermitirXlsxNoDrive = true }, ambiente.Store, ambiente.Http);
        ambiente.Google.Escopo = GoogleOptions.Escopos + (concedida ? " " + GoogleOptions.EscopoDeDrive : "");
        var autorizacao = await oauth.IniciarAsync();
        var parametros = Parametros(autorizacao.Url);
        Assert.Contains(GoogleOptions.EscopoDeDrive, parametros["scope"].Split(' '));
        using var navegador = new HttpClient();
        await navegador.GetAsync($"{parametros["redirect_uri"]}?code=codigo-valido&state={Uri.EscapeDataString(parametros["state"])}");
        if (!concedida)
        {
            var falha = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => autorizacao.Conclusao);
            Assert.Equal(MotivoDaFalhaDePlanilha.SemPermissao, falha.Motivo);
            Assert.False(ambiente.Store.Existe);
            return;
        }
        Assert.True((await autorizacao.Conclusao).Conectada);
        Assert.True(oauth.TemPermissaoDeDrive);
        using var reiniciado = new GoogleOAuthService(new GoogleOptions(), ambiente.Store, ambiente.Http);
        Assert.True(reiniciado.TemPermissaoDeDrive);
        Assert.DoesNotContain(GoogleSheetsFalso.RefreshToken, autorizacao.Url.AbsoluteUri);
    }

    private static Dictionary<string, string> Parametros(Uri url) => url.Query.TrimStart('?').Split('&')
        .Select(par => par.Split('=', 2)).ToDictionary(par => par[0], par => Uri.UnescapeDataString(par[1]));

    private static string Base64Url(byte[] dados) => Convert.ToBase64String(dados).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
