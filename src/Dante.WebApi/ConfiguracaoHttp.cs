using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dante.WebApi;

public static class ConfiguracaoHttp
{
    public static IServiceCollection AddWebApi(this IServiceCollection services)
    {
        services.AddHealthChecks();
        services.AddOpenApi();
        services.AddExceptionHandler<TratamentoDeErros>();
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var status = context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode;
            context.ProblemDetails.Type = "about:blank";
            context.ProblemDetails.Title = status switch
            {
                400 => "Requisição inválida.",
                404 => "Recurso não encontrado.",
                405 => "Método HTTP não permitido.",
                _ => status >= 500 ? "Falha interna ao processar a requisição." : "Requisição recusada."
            };
            context.ProblemDetails.Detail = null;
            context.ProblemDetails.Instance = context.HttpContext.Request.Path;
            context.ProblemDetails.Extensions.Clear();
            context.ProblemDetails.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
        });
        return services;
    }

    public static WebApplication UsarPipelineHttp(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        if (app.Environment.IsDevelopment()) app.MapOpenApi();
        app.MapGet("/health", async (HealthCheckService health, CancellationToken ct) =>
        {
            var report = await health.CheckHealthAsync(ct);
            var dto = new SaudeDto(report.Status switch
            {
                HealthStatus.Healthy => "saudavel",
                HealthStatus.Degraded => "degradado",
                _ => "indisponivel"
            });
            return Results.Json(dto, statusCode: report.Status == HealthStatus.Unhealthy ? 503 : 200);
        }).WithName("Health").WithSummary("Saúde operacional do host HTTP.")
            .Produces<SaudeDto>(StatusCodes.Status200OK)
            .Produces<SaudeDto>(StatusCodes.Status503ServiceUnavailable);
        return app;
    }
}
