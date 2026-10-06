using Microsoft.Extensions.DependencyInjection;
namespace Dante.Infrastructure.Banco;

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
                case ["--brain", "source-import", var usuarioImport, var espacoImport, var projetoImport, var caminhoImport, var revisaoImport]:
                    var acessoImport = new Dante.Application.SegurancaDoBrain.AcessoAoBrain(Guid.Parse(usuarioImport), Guid.Parse(espacoImport), projetoImport == "-" ? null : Guid.Parse(projetoImport));
                    scope.ServiceProvider.GetRequiredService<Dante.Application.SegurancaDoBrain.AutorizacaoDoBrain>().Estabelecer(new(scope.ServiceProvider.GetRequiredService<Dante.Infrastructure.SegurancaDoBrain.IdentidadeTelegramDoBrain>().IdTenantConfigurado, acessoImport.IdUsuario), acessoImport);
                    var fonteImportada = await scope.ServiceProvider.GetRequiredService<Dante.Application.DocumentosFonte.DocumentoFonteAppService>().ImportarArquivoAsync(acessoImport, caminhoImport, Dante.Domain.Conhecimentos.Sensibilidade.Pessoal, revisaoImport == "-" ? null : int.Parse(revisaoImport, System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
                    Console.WriteLine($"Fonte importada; revisão {fonteImportada.Revisao}, hash {fonteImportada.Hash}."); break;
                case ["--brain", "export", var usuarioExport, var espacoExport, var projetoExport, var prefixo]:
                    var acessoExport = new Dante.Application.SegurancaDoBrain.AcessoAoBrain(Guid.Parse(usuarioExport), Guid.Parse(espacoExport), projetoExport == "-" ? null : Guid.Parse(projetoExport));
                    scope.ServiceProvider.GetRequiredService<Dante.Application.SegurancaDoBrain.AutorizacaoDoBrain>().Estabelecer(new(scope.ServiceProvider.GetRequiredService<Dante.Infrastructure.SegurancaDoBrain.IdentidadeTelegramDoBrain>().IdTenantConfigurado, acessoExport.IdUsuario), acessoExport);
                    var exportacao = await scope.ServiceProvider.GetRequiredService<Dante.Application.AuditoriaDoBrain.InspecaoDoBrainAppService>().ExportarAsync(acessoExport, cancellationToken);
                    await Dante.Infrastructure.AuditoriaDoBrain.GravadorDeExportacao.SalvarAsync(exportacao, prefixo, cancellationToken);
                    Console.WriteLine("Exportação Markdown/JSON concluída."); break;
                case ["--brain", "inspect", var usuarioInspect, var espacoInspect, var projetoInspect]:
                    var acessoInspect = new Dante.Application.SegurancaDoBrain.AcessoAoBrain(Guid.Parse(usuarioInspect), Guid.Parse(espacoInspect), projetoInspect == "-" ? null : Guid.Parse(projetoInspect));
                    scope.ServiceProvider.GetRequiredService<Dante.Application.SegurancaDoBrain.AutorizacaoDoBrain>().Estabelecer(new(scope.ServiceProvider.GetRequiredService<Dante.Infrastructure.SegurancaDoBrain.IdentidadeTelegramDoBrain>().IdTenantConfigurado, acessoInspect.IdUsuario), acessoInspect);
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(await scope.ServiceProvider.GetRequiredService<Dante.Application.AuditoriaDoBrain.InspecaoDoBrainAppService>().InspecionarAsync(acessoInspect, cancellationToken: cancellationToken))); break;
                case ["--brain", "search-rebuild-lexical"]:
                    await scope.ServiceProvider.GetRequiredService<Dante.Infrastructure.BuscaDoBrain.IndiceDeBuscaPostgreSql>().ReconstruirLexicalAsync(cancellationToken); break;
                case ["--brain", "search-enable-vector"]:
                    await scope.ServiceProvider.GetRequiredService<Dante.Infrastructure.BuscaDoBrain.IndiceDeBuscaPostgreSql>().PrepararVetoresAsync(cancellationToken); break;
                case ["--brain", "search-reindex", var usuario, var espaco, var projeto]:
                    var acesso = new Dante.Application.SegurancaDoBrain.AcessoAoBrain(Guid.Parse(usuario), Guid.Parse(espaco), projeto == "-" ? null : Guid.Parse(projeto));
                    scope.ServiceProvider.GetRequiredService<Dante.Application.SegurancaDoBrain.AutorizacaoDoBrain>().Estabelecer(new(scope.ServiceProvider.GetRequiredService<Dante.Infrastructure.SegurancaDoBrain.IdentidadeTelegramDoBrain>().IdTenantConfigurado, acesso.IdUsuario), acesso);
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
