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
                case ["--brain", "export", var usuarioExport, var espacoExport, var projetoExport, var prefixo]:
                    var acessoExport = new Dante.Application.SegurancaDoBrain.AcessoAoBrain(Guid.Parse(usuarioExport), Guid.Parse(espacoExport), projetoExport == "-" ? null : Guid.Parse(projetoExport));
                    scope.ServiceProvider.GetRequiredService<Dante.Application.SegurancaDoBrain.AutorizacaoDoBrain>().Estabelecer(new(Dante.Application.SegurancaDoBrain.AutorizacaoDoBrain.TenantLocal, acessoExport.IdUsuario), acessoExport);
                    var exportacao = await scope.ServiceProvider.GetRequiredService<Dante.Application.AuditoriaDoBrain.InspecaoDoBrainAppService>().ExportarAsync(acessoExport, cancellationToken);
                    await Dante.Infrastructure.AuditoriaDoBrain.GravadorDeExportacao.SalvarAsync(exportacao, prefixo, cancellationToken);
                    Console.WriteLine("Exportação Markdown/JSON concluída."); break;
                case ["--brain", "inspect", var usuarioInspect, var espacoInspect, var projetoInspect]:
                    var acessoInspect = new Dante.Application.SegurancaDoBrain.AcessoAoBrain(Guid.Parse(usuarioInspect), Guid.Parse(espacoInspect), projetoInspect == "-" ? null : Guid.Parse(projetoInspect));
                    scope.ServiceProvider.GetRequiredService<Dante.Application.SegurancaDoBrain.AutorizacaoDoBrain>().Estabelecer(new(Dante.Application.SegurancaDoBrain.AutorizacaoDoBrain.TenantLocal, acessoInspect.IdUsuario), acessoInspect);
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(await scope.ServiceProvider.GetRequiredService<Dante.Application.AuditoriaDoBrain.InspecaoDoBrainAppService>().InspecionarAsync(acessoInspect, cancellationToken: cancellationToken))); break;
                case ["--brain", "search-rebuild-lexical"]:
                    await scope.ServiceProvider.GetRequiredService<Dante.Infrastructure.BuscaDoBrain.IndiceDeBuscaPostgreSql>().ReconstruirLexicalAsync(cancellationToken); break;
                case ["--brain", "search-enable-vector"]:
                    await scope.ServiceProvider.GetRequiredService<Dante.Infrastructure.BuscaDoBrain.IndiceDeBuscaPostgreSql>().PrepararVetoresAsync(cancellationToken); break;
                case ["--brain", "search-reindex", var usuario, var espaco, var projeto]:
                    var acesso = new Dante.Application.SegurancaDoBrain.AcessoAoBrain(Guid.Parse(usuario), Guid.Parse(espaco), projeto == "-" ? null : Guid.Parse(projeto));
                    scope.ServiceProvider.GetRequiredService<Dante.Application.SegurancaDoBrain.AutorizacaoDoBrain>().Estabelecer(new(Dante.Application.SegurancaDoBrain.AutorizacaoDoBrain.TenantLocal, acesso.IdUsuario), acesso);
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
