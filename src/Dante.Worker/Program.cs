using Dante.Infrastructure.Banco;
using Dante.Application;
using Dante.Infrastructure;
using Dante.Worker;

var builder = Host.CreateApplicationBuilder(args.FirstOrDefault() == "--brain" ? [] : args);
builder.Services.AddApplication().AddInfrastructure(builder.Configuration).AddWorker(builder.Configuration);
var host = builder.Build();
if (args.FirstOrDefault() == "--brain")
{
    Environment.ExitCode = await ComandosDoBanco.ExecutarAsync(host.Services, args);
    return;
}
host.Run();
