using Dante.Application;
using Dante.Infrastructure;

namespace Dante.WebApi;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddApplication().AddInfrastructure(builder.Configuration).AddWebApi();
        var app = builder.Build();
        app.UsarPipelineHttp();
        app.Run();
    }
}
