using Dante.Infrastructure.Banco;
using Dante.Infrastructure.Brain;
using Dante.Application;
using Dante.Infrastructure;

namespace Dante.WebApi;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        var administrativo = args.FirstOrDefault() is "--brain" or ComandosDoBanco.Orquestracao;
        var builder = WebApplication.CreateBuilder(administrativo ? [] : args);
        builder.Services.AddApplication().AddInfrastructure(builder.Configuration).AddWebApi();
        var app = builder.Build();
        if (args.FirstOrDefault() == "--brain")
        {
            Environment.ExitCode = await ComandosDoBrain.ExecutarAsync(app.Services, args);
            return;
        }
        if (args.FirstOrDefault() == ComandosDoBanco.Orquestracao)
        {
            Environment.ExitCode = await ComandosDoBanco.ExecutarAsync(app.Services, args);
            return;
        }
        app.UsarPipelineHttp();
        app.Run();
    }
}
