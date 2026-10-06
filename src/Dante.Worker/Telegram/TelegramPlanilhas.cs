using System.Text;
using Dante.Application.Planilhas;
using Dante.Worker.Planilhas;

namespace Dante.Worker.Telegram;

// Comandos de conexão e cadastro de planilhas (#224): /google connect|status|disconnect, /planilhas e
// /planilha add|show|remove. Ler, buscar e editar acontecem na conversa, pelo agente com as ferramentas MCP; aqui só
// se administra a conta e o cadastro. Nenhuma resposta contém token, code ou segredo.
public sealed class TelegramPlanilhas(IServiceScopeFactory scopes, ILogger<TelegramPlanilhas> logger)
{
    internal const string SintaxeGoogle = "Uso: /google connect|status|disconnect";
    internal const string SintaxePlanilha = "Uso: /planilha add <alias> <url ou id> [descrição] | /planilha show <alias> | /planilha remove <alias>";

    // notificar recebe o desfecho da autorização, que chega depois da resposta com o link.
    public async Task<string> GoogleAsync(string argumentos, Func<string, Task> notificar, CancellationToken cancellationToken)
    {
        var acao = argumentos.Trim().ToLowerInvariant();
        return await ExecutarAsync(async planilhas =>
        {
            switch (acao)
            {
                case "" or "status":
                    return ServidorMcpDePlanilhas.FormatarConexao(await planilhas.ObterConexaoAsync(cancellationToken));
                case "connect":
                    var autorizacao = await planilhas.IniciarConexaoAsync(cancellationToken);
                    _ = AcompanharAsync(autorizacao, notificar);
                    return "Abra este link no navegador deste computador (o Google volta para um endereço local do D.A.N.T.E.), " +
                           $"entre na conta e autorize o acesso às planilhas. O link vale até {autorizacao.ExpiraEm:HH:mm} UTC.\n" +
                           autorizacao.Url;
                case "disconnect":
                    return await planilhas.DesconectarAsync(cancellationToken)
                        ? "Conta Google desconectada: o acesso foi revogado e a credencial local apagada."
                        : "Nenhuma conta Google estava conectada.";
                default:
                    return SintaxeGoogle;
            }
        });
    }

    public Task<string> ListarAsync(CancellationToken cancellationToken) => ExecutarAsync(async planilhas =>
    {
        var conexao = ServidorMcpDePlanilhas.FormatarConexao(await planilhas.ObterConexaoAsync(cancellationToken));
        var cadastradas = await planilhas.ListarAsync(cancellationToken);
        return cadastradas.Count == 0
            ? conexao + "\nNenhuma planilha cadastrada. Use /planilha add <alias> <url>."
            : conexao + "\n" + string.Join('\n', cadastradas.Select(p => ServidorMcpDePlanilhas.FormatarCadastro(p, null)));
    });

    public Task<string> PlanilhaAsync(string argumentos, CancellationToken cancellationToken)
    {
        var partes = argumentos.Split([' ', '\t', '\r', '\n'], 4, StringSplitOptions.RemoveEmptyEntries);
        var acao = partes.Length == 0 ? string.Empty : partes[0].ToLowerInvariant();
        return ExecutarAsync(async planilhas => (acao, partes.Length) switch
        {
            ("add", >= 3) => ServidorMcpDePlanilhas.FormatarCadastro(
                await planilhas.CadastrarAsync(partes[1], partes[2], partes.Length > 3 ? partes[3] : null, cancellationToken),
                "Planilha cadastrada"),
            ("show", 2) => Descrever(await planilhas.DescreverAsync(partes[1], cancellationToken)),
            ("remove", 2) => await planilhas.RemoverAsync(partes[1], cancellationToken)
                ? $"Planilha {partes[1].TrimStart('@').ToLowerInvariant()} removida do cadastro. A planilha no Google não foi alterada."
                : $"A planilha {partes[1]} não está cadastrada.",
            _ => SintaxePlanilha
        });
    }

    private static string Descrever(DescricaoDaPlanilhaDto descricao)
    {
        var texto = new StringBuilder(ServidorMcpDePlanilhas.FormatarCadastro(descricao.Cadastro, "Planilha"));
        foreach (var aba in descricao.Planilha.Abas)
            texto.Append("\n- ").Append(aba.Titulo).Append(aba.AreaUsada is null ? " (sem dados)" : $" ({aba.AreaUsada})")
                .Append(aba.Mesclagens.Count > 0 ? $", {aba.Mesclagens.Count} mesclagem(ns)" : "");
        return texto.ToString();
    }

    private async Task AcompanharAsync(AutorizacaoDeConexao autorizacao, Func<string, Task> notificar)
    {
        string mensagem;
        try
        {
            var estado = await autorizacao.Conclusao;
            mensagem = "Conta Google conectada" + (estado.Conta is null ? "." : $": {estado.Conta}.") +
                       " Novas sessões passam a ter as ferramentas de planilha.";
        }
        catch (FalhaDePlanilhaException exception)
        {
            mensagem = "Conexão Google não concluída: " + exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Autorização Google falhou ({ErrorType}).", exception.GetType().Name);
            mensagem = "Conexão Google não concluída por uma falha local.";
        }
        try { await notificar(mensagem); }
        catch (Exception exception)
        {
            logger.LogWarning("Não foi possível avisar o desfecho da conexão Google ({ErrorType}).", exception.GetType().Name);
        }
    }

    private async Task<string> ExecutarAsync(Func<PlanilhasAppService, Task<string>> operacao)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            return await operacao(scope.ServiceProvider.GetRequiredService<PlanilhasAppService>());
        }
        catch (FalhaDePlanilhaException exception)
        {
            return exception.Message;
        }
        catch (ArgumentException exception)
        {
            return exception.Message.Split(" (Parameter", 2)[0];
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.LogWarning("Falha local de planilhas ({ErrorType}).", exception.GetType().Name);
            return "Falha local ao acessar o cadastro ou a credencial de planilhas.";
        }
    }
}
