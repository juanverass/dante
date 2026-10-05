using Dante.Application;
using Dante.Infrastructure;
using Dante.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddApplication().AddInfrastructure(builder.Configuration).AddWorker(builder.Configuration);
var host = builder.Build();
host.Run();
