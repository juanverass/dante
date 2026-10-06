using System.Text;
using Dante.Application.AuditoriaDoBrain;
namespace Dante.Infrastructure.AuditoriaDoBrain;

public static class GravadorDeExportacao
{
    public static async Task SalvarAsync(ExportacaoDoBrainDto exportacao,string prefixo,CancellationToken cancellationToken=default)
    {
        var caminhos=new[]{Path.GetFullPath(prefixo)+".json",Path.GetFullPath(prefixo)+".md"};
        var criados=new List<string>();var streams=new List<FileStream>();
        try
        {
            foreach(var caminho in caminhos)
            {
                var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None};
                if(!OperatingSystem.IsWindows()) options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
                streams.Add(new FileStream(caminho,options));criados.Add(caminho);
            }
            await streams[0].WriteAsync(Encoding.UTF8.GetBytes(exportacao.Json),cancellationToken);
            await streams[1].WriteAsync(Encoding.UTF8.GetBytes(exportacao.Markdown),cancellationToken);
            foreach(var stream in streams) await stream.DisposeAsync();
        }
        catch
        {
            foreach(var stream in streams) await stream.DisposeAsync();
            foreach(var caminho in criados) File.Delete(caminho);
            throw;
        }
    }
}
