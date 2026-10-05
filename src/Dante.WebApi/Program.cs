using Dante.Infrastructure.Persistencia;
using Dante.Application;
using Dante.Infrastructure;

namespace Dante.WebApi;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddApplication().AddInfrastructure(builder.Configuration).AddWebApi();
        var app = builder.Build();
        if (args.FirstOrDefault() == "--brain")
        {
            Environment.ExitCode = await ComandosDoBanco.ExecutarAsync(app.Services, args);
            return;
        }
        app.UsarPipelineHttp();
        app.Run();
    }
}
