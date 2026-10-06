using Microsoft.Extensions.DependencyInjection;
namespace Dante.Infrastructure.Persistencia;

public static class ComandosDoBanco
{
    public static async Task<int> ExecutarAsync(IServiceProvider services, string[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = services.CreateScope();
            var admin = scope.ServiceProvider.GetService<AdministracaoDoBanco>() ??
                throw new InvalidOperationException("Banco não configurado.");
            switch (args)
            {
                case ["--brain", "search-rebuild-lexical"]:
                    await scope.ServiceProvider.GetRequiredService<Dante.Infrastructure.BuscaDoBrain.IndiceDeBuscaPostgreSql>().ReconstruirLexicalAsync(cancellationToken); break;
                case ["--brain", "search-enable-vector"]:
                    await scope.ServiceProvider.GetRequiredService<Dante.Infrastructure.BuscaDoBrain.IndiceDeBuscaPostgreSql>().PrepararVetoresAsync(cancellationToken); break;
                case ["--brain", "search-reindex", var usuario, var espaco, var projeto]:
                    var acesso = new Dante.Application.SegurancaDoBrain.AcessoAoBrain(Guid.Parse(usuario), Guid.Parse(espaco), projeto == "-" ? null : Guid.Parse(projeto));
                    Console.WriteLine(await scope.ServiceProvider.GetRequiredService<Dante.Application.BuscaDoBrain.BuscaDoBrainAppService>().ReindexarAsync(acesso, cancellationToken: cancellationToken)); break;
                case ["--brain", "migrate"]: await admin.MigrarAsync(cancellationToken); break;
                case ["--brain", "health"]: return await admin.VerificarSaudeAsync(cancellationToken) ? 0 : 1;
                case ["--brain", "backup", var caminho]: await admin.BackupAsync(caminho, cancellationToken); break;
                case ["--brain", "restore", var caminho]: await admin.RestaurarAsync(caminho, cancellationToken); break;
                default: throw new ArgumentException("Comando administrativo inválido.");
            }
            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Exceptions de providers podem conter credenciais ou dados. Não as imprimir.
            Console.Error.WriteLine("Operação Brain falhou; confira configuração, permissões e pré-requisitos.");
            return 1;
        }
    }
}
