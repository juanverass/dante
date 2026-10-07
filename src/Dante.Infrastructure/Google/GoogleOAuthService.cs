using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dante.Application.Planilhas;

namespace Dante.Infrastructure.Google;

// OAuth 2.0 para app local (#224): callback loopback em 127.0.0.1 numa porta livre, PKCE S256 e state aleatório
// validado em tempo constante. O WSL2 encaminha o localhost do Windows, então o link abre no navegador do Windows.
// Access token só em memória; refresh token cifrado pelo GoogleCredentialStore. Nenhuma mensagem, exceção ou log
// carrega token, code, verifier ou segredo.
public sealed class GoogleOAuthService(GoogleOptions opcoes, GoogleCredentialStore store, HttpClient http,
    TimeProvider? relogio = null) : IConexaoDePlanilha, IDisposable
{
    internal const string EnderecoDeAutorizacao = "https://accounts.google.com/o/oauth2/v2/auth";
    internal const string EnderecoDeToken = "https://oauth2.googleapis.com/token";
    internal const string EnderecoDeRevogacao = "https://oauth2.googleapis.com/revoke";
    private const string Provedor = "Google Sheets";
    private const int MaximoDoCabecalho = 16 * 1024;

    private readonly TimeProvider relogio = relogio ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object pendenteGate = new();
    private (string Token, DateTimeOffset Expira)? acesso;
    private CancellationTokenSource? pendente;
    private string? problema;

    internal HttpClient Http => http;
    // A permissão concedida fica na credencial cifrada, também disponível no processo MCP.
    internal bool TemPermissaoDeDrive => store.Carregar()?.Escopos.Split(' ').Contains(GoogleOptions.EscopoDeDrive,
        StringComparer.Ordinal) == true;

    public Task<EstadoDaConexaoDto> ObterEstadoAsync(CancellationToken cancellationToken = default)
    {
        CredencialGoogle? credencial;
        string? falha = problema;
        try { credencial = store.Carregar(); }
        catch (FalhaDePlanilhaException exception) { credencial = null; falha = exception.Message; }
        return Task.FromResult(new EstadoDaConexaoDto
        {
            Provedor = Provedor,
            Configurada = opcoes.Configurada || credencial is not null,
            Conectada = credencial is not null && problema is null,
            Conta = credencial?.Conta,
            ConectadaEm = credencial?.ConectadaEm,
            Problema = falha
        });
    }

    public Task<AutorizacaoDeConexao> IniciarAsync(CancellationToken cancellationToken = default)
    {
        if (!opcoes.Configurada)
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.NaoConfigurada,
                "Integração Google não configurada: defina Google__ClientId e Google__ClientSecret (cliente OAuth do tipo " +
                "app para computador) no ambiente do D.A.N.T.E. e reinicie o serviço.");
        var cancelamento = new CancellationTokenSource(opcoes.TempoParaAutorizar);
        lock (pendenteGate)
        {
            // Uma autorização nova invalida a anterior: o state antigo deixa de ser aceito.
            pendente?.Cancel();
            pendente = cancelamento;
        }

        var listener = new TcpListener(IPAddress.Loopback, opcoes.PortaDoCallback);
        listener.Start();
        var porta = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirect = $"http://127.0.0.1:{porta}";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var url = new Uri(EnderecoDeAutorizacao + "?" + Query(new Dictionary<string, string>
        {
            ["client_id"] = opcoes.ClientId!,
            ["redirect_uri"] = redirect,
            ["response_type"] = "code",
            ["scope"] = opcoes.EscoposSolicitados,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["access_type"] = "offline",
            // Garante o refresh token mesmo quando a conta já autorizou este cliente antes.
            ["prompt"] = "consent"
        }));
        var conclusao = AguardarCallbackAsync(listener, state, verifier, redirect, cancelamento);
        return Task.FromResult(new AutorizacaoDeConexao(url, relogio.GetUtcNow() + opcoes.TempoParaAutorizar, conclusao));
    }

    public async Task<bool> DesconectarAsync(CancellationToken cancellationToken = default)
    {
        lock (pendenteGate)
        {
            pendente?.Cancel();
            pendente = null;
        }
        CredencialGoogle? credencial;
        try { credencial = store.Carregar(); }
        catch (FalhaDePlanilhaException) { credencial = null; }
        if (credencial is not null)
        {
            try
            {
                using var resposta = await http.PostAsync(EnderecoDeRevogacao,
                    new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = credencial.RefreshToken }), cancellationToken);
            }
            catch (HttpRequestException)
            {
                // Sem rede a credencial local é apagada mesmo assim; o acesso pode ser revogado em myaccount.google.com.
            }
        }
        await gate.WaitAsync(cancellationToken);
        try
        {
            acesso = null;
            problema = null;
            return store.Apagar() || credencial is not null;
        }
        finally { gate.Release(); }
    }

    // Access token válido para as chamadas da API; renova pelo refresh token quando expira ou quando a API recusou o
    // token atual (forcarRenovacao).
    internal async Task<string> ObterTokenDeAcessoAsync(bool forcarRenovacao, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!forcarRenovacao && acesso is { } atual && atual.Expira > relogio.GetUtcNow()) return atual.Token;
            var credencial = store.Carregar() ?? throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.NaoConectada,
                "Conta Google não conectada. Conecte com /google connect.");
            var (corpo, status) = await PostarTokenAsync(new Dictionary<string, string>
            {
                ["client_id"] = credencial.ClientId,
                ["client_secret"] = credencial.ClientSecret,
                ["refresh_token"] = credencial.RefreshToken,
                ["grant_type"] = "refresh_token"
            }, cancellationToken);
            if (status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized &&
                Texto(corpo, "error") is "invalid_grant" or "invalid_client" or "unauthorized_client")
            {
                problema = "a autorização do Google foi revogada ou expirou; reconecte com /google connect.";
                acesso = null;
                throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.NaoConectada, "A " + problema);
            }
            if ((int)status is < 200 or > 299 || Texto(corpo, "access_token") is not { Length: > 0 } token)
                throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.Indisponivel,
                    "O Google não renovou o acesso agora; tente novamente em instantes.");
            problema = null;
            acesso = (token, Expiracao(corpo));
            return token;
        }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        lock (pendenteGate) pendente?.Cancel();
        gate.Dispose();
    }

    private async Task<EstadoDaConexaoDto> AguardarCallbackAsync(TcpListener listener, string state, string verifier,
        string redirect, CancellationTokenSource cancelamento)
    {
        await Task.Yield();
        var token = cancelamento.Token;
        try
        {
            while (true)
            {
                using var cliente = await AceitarAsync(listener, token);
                await using var fluxo = cliente.GetStream();
                string caminho;
                Dictionary<string, string> parametros;
                try { (caminho, parametros) = await LerRequisicaoAsync(fluxo, token); }
                catch (IOException)
                {
                    // Conexão interrompida pelo navegador: espera a próxima.
                    continue;
                }
                if (caminho != "/")
                {
                    await ResponderAsync(fluxo, HttpStatusCode.NotFound, "Não encontrado.", token);
                    continue;
                }
                // State divergente: outra aba ou um pedido forjado; o fluxo continua esperando o callback legítimo.
                if (!parametros.TryGetValue("state", out var recebido) || !CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(recebido), Encoding.ASCII.GetBytes(state)))
                {
                    await ResponderAsync(fluxo, HttpStatusCode.BadRequest,
                        "Autorização inválida ou antiga. Volte ao D.A.N.T.E. e use o link mais recente.", token);
                    continue;
                }
                if (parametros.TryGetValue("error", out _) || !parametros.TryGetValue("code", out var code) || code.Length == 0)
                {
                    await ResponderAsync(fluxo, HttpStatusCode.OK, "Autorização recusada. Nada foi conectado.", token);
                    throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.SemPermissao, "A autorização foi recusada no Google.");
                }
                try
                {
                    var estado = await TrocarCodigoAsync(code, verifier, redirect, token);
                    await ResponderAsync(fluxo, HttpStatusCode.OK,
                        "Conta Google conectada ao D.A.N.T.E. Você já pode fechar esta aba.", token);
                    return estado;
                }
                catch (FalhaDePlanilhaException exception)
                {
                    await ResponderAsync(fluxo, HttpStatusCode.OK, "Não foi possível conectar: " + exception.Message, token);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.NaoConectada,
                "A autorização expirou ou foi substituída por outra; gere um link novo com /google connect.");
        }
        finally
        {
            listener.Stop();
            lock (pendenteGate)
            {
                if (ReferenceEquals(pendente, cancelamento)) pendente = null;
            }
            cancelamento.Dispose();
        }
    }

    private async Task<EstadoDaConexaoDto> TrocarCodigoAsync(string code, string verifier, string redirect,
        CancellationToken cancellationToken)
    {
        var (corpo, status) = await PostarTokenAsync(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = opcoes.ClientId!,
            ["client_secret"] = opcoes.ClientSecret!,
            ["redirect_uri"] = redirect,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier
        }, cancellationToken);
        if ((int)status is < 200 or > 299 || Texto(corpo, "access_token") is not { Length: > 0 } token)
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.SemPermissao,
                "o Google recusou a troca do código de autorização.");
        if (Texto(corpo, "refresh_token") is not { Length: > 0 } refresh)
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.SemPermissao,
                "o Google não devolveu acesso permanente; remova o acesso do app em myaccount.google.com/permissions e conecte de novo.");
        var escopos = Texto(corpo, "scope") ?? string.Empty;
        if (!escopos.Split(' ').Contains(GoogleOptions.EscopoDePlanilhas, StringComparer.Ordinal))
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.SemPermissao,
                "a permissão de planilhas não foi concedida; conecte de novo marcando o acesso ao Google Sheets.");
        if (opcoes.PermitirXlsxNoDrive && !escopos.Split(' ').Contains(GoogleOptions.EscopoDeDrive, StringComparer.Ordinal))
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.SemPermissao,
                "a permissão do Drive para XLSX não foi concedida; conecte de novo marcando o acesso ao Drive.");
        var conta = Email(Texto(corpo, "id_token"));
        var agora = relogio.GetUtcNow();
        await gate.WaitAsync(cancellationToken);
        try
        {
            store.Salvar(new CredencialGoogle(opcoes.ClientId!, opcoes.ClientSecret!, refresh, conta, agora, escopos));
            acesso = (token, Expiracao(corpo));
            problema = null;
        }
        finally { gate.Release(); }
        return new EstadoDaConexaoDto { Provedor = Provedor, Configurada = true, Conectada = true, Conta = conta, ConectadaEm = agora };
    }

    private async Task<(JsonObject? Corpo, HttpStatusCode Status)> PostarTokenAsync(Dictionary<string, string> formulario,
        CancellationToken cancellationToken)
    {
        try
        {
            using var resposta = await http.PostAsync(EnderecoDeToken, new FormUrlEncodedContent(formulario), cancellationToken);
            var texto = await resposta.Content.ReadAsStringAsync(cancellationToken);
            JsonObject? corpo;
            try { corpo = JsonNode.Parse(texto) as JsonObject; }
            catch (JsonException) { corpo = null; }
            return (corpo, resposta.StatusCode);
        }
        catch (HttpRequestException exception)
        {
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.Indisponivel,
                "Não foi possível falar com o Google agora; verifique a rede.", innerException: exception);
        }
    }

    private DateTimeOffset Expiracao(JsonObject? corpo)
    {
        var segundos = corpo?["expires_in"] is JsonValue valor && valor.TryGetValue<int>(out var s) ? s : 3600;
        // Margem para o token não vencer no meio de uma chamada.
        return relogio.GetUtcNow().AddSeconds(Math.Max(0, segundos - 60));
    }

    // O id_token chega direto do endpoint de token por TLS: só o e-mail do payload é usado, como rótulo da conta.
    private static string? Email(string? idToken)
    {
        var partes = idToken?.Split('.');
        if (partes is not { Length: 3 }) return null;
        try
        {
            var payload = partes[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            return Texto(JsonNode.Parse(Convert.FromBase64String(payload)) as JsonObject, "email");
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    private static async Task<TcpClient> AceitarAsync(TcpListener listener, CancellationToken cancellationToken) =>
        await listener.AcceptTcpClientAsync(cancellationToken);

    private static async Task<(string Caminho, Dictionary<string, string> Parametros)> LerRequisicaoAsync(Stream fluxo,
        CancellationToken cancellationToken)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(TimeSpan.FromSeconds(10));
        var buffer = new byte[MaximoDoCabecalho];
        var lidos = 0;
        while (lidos < buffer.Length)
        {
            int n;
            try { n = await fluxo.ReadAsync(buffer.AsMemory(lidos), limite.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
            if (n == 0) break;
            lidos += n;
            if (Encoding.ASCII.GetString(buffer, 0, lidos).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        var linha = Encoding.ASCII.GetString(buffer, 0, lidos).Split("\r\n")[0].Split(' ');
        if (linha.Length < 2 || linha[0] != "GET") return (string.Empty, []);
        var alvo = linha[1];
        var interrogacao = alvo.IndexOf('?');
        var caminho = interrogacao < 0 ? alvo : alvo[..interrogacao];
        var parametros = new Dictionary<string, string>(StringComparer.Ordinal);
        if (interrogacao >= 0)
            foreach (var par in alvo[(interrogacao + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var igual = par.IndexOf('=');
                var nome = Uri.UnescapeDataString((igual < 0 ? par : par[..igual]).Replace('+', ' '));
                var valor = igual < 0 ? string.Empty : Uri.UnescapeDataString(par[(igual + 1)..].Replace('+', ' '));
                parametros.TryAdd(nome, valor);
            }
        return (caminho, parametros);
    }

    private static async Task ResponderAsync(Stream fluxo, HttpStatusCode status, string mensagem,
        CancellationToken cancellationToken)
    {
        var html = "<!doctype html><html lang=\"pt-BR\"><meta charset=\"utf-8\"><title>D.A.N.T.E.</title>" +
                   $"<body style=\"font-family:sans-serif;margin:3em\"><p>{WebUtility.HtmlEncode(mensagem)}</p></body></html>";
        var corpo = Encoding.UTF8.GetBytes(html);
        var cabecalho = Encoding.ASCII.GetBytes($"HTTP/1.1 {(int)status} {status}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                                                $"Content-Length: {corpo.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        try
        {
            await fluxo.WriteAsync(cabecalho, cancellationToken);
            await fluxo.WriteAsync(corpo, cancellationToken);
        }
        catch (IOException)
        {
            // O navegador fechou a conexão; o resultado do fluxo não depende da página.
        }
    }

    private static string? Texto(JsonObject? corpo, string nome) =>
        corpo?[nome] is JsonValue valor && valor.TryGetValue<string>(out var texto) ? texto : null;

    private static string Query(Dictionary<string, string> parametros) =>
        string.Join('&', parametros.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    private static string Base64Url(byte[] dados) => Convert.ToBase64String(dados).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
