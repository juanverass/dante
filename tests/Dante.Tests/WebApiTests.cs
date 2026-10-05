using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dante.WebApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Dante.Tests;

public sealed class WebApiTests
{
    [Fact]
    public async Task HostStartsWithoutWorkerTelegramOrDatabaseAndReturnsOperationalHealthDto()
    {
        using var factory = new WebApplicationFactory<Dante.WebApi.Program>();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new SaudeDto("saudavel"), await response.Content.ReadFromJsonAsync<SaudeDto>());
        Assert.DoesNotContain(factory.Services.GetServices<IHostedService>(), service =>
            service.GetType().Assembly.GetName().Name == "Dante.Worker");
        Assert.DoesNotContain(typeof(Dante.WebApi.Program).Assembly.GetReferencedAssemblies(), a => a.Name == "Dante.Worker");
    }

    [Theory]
    [InlineData("/nao-existe", "GET", 404)]
    [InlineData("/health", "POST", 405)]
    public async Task HttpErrorsUseProblemDetailsWithCorrelation(string path, string method, int status)
    {
        using var factory = new WebApplicationFactory<Dante.WebApi.Program>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Accept.ParseAdd("application/json");
        var response = await client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(status, problem.GetProperty("status").GetInt32());
        Assert.Equal(path, problem.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task UnexpectedExceptionsDoNotExposeSecretsOrStackEvenInDevelopment()
    {
        using var factory = new WebApplicationFactory<Dante.WebApi.Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services => services.Replace(
                ServiceDescriptor.Singleton<HealthCheckService, FalhaDeSaude>()));
        });
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("segredo-sintetico", body);
        Assert.DoesNotContain("InvalidOperationException", body);
        Assert.DoesNotContain("stack", body, StringComparison.OrdinalIgnoreCase);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("Falha interna ao processar a requisição.", problem.RootElement.GetProperty("title").GetString());
        Assert.True(problem.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task UnhealthyOperationalCheckReturns503WithoutItsInternalDetails()
    {
        using var factory = new WebApplicationFactory<Dante.WebApi.Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddHealthChecks().AddCheck("sintetico", () =>
                HealthCheckResult.Unhealthy("detalhe-interno-sintetico"))));
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(new SaudeDto("indisponivel"), await response.Content.ReadFromJsonAsync<SaudeDto>());
        Assert.DoesNotContain("detalhe-interno", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OpenApiIsAvailableInDevelopmentAndDocumentsTheHealthContract()
    {
        using var factory = new WebApplicationFactory<Dante.WebApi.Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Development"));
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(document.TryGetProperty("openapi", out _));
        var operation = document.GetProperty("paths").GetProperty("/health").GetProperty("get");
        Assert.True(operation.GetProperty("responses").TryGetProperty("200", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("503", out _));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task OpenApiIsNotExposedOutsideDevelopment(string environment)
    {
        using var factory = new WebApplicationFactory<Dante.WebApi.Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment(environment));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    private sealed class FalhaDeSaude : HealthCheckService
    {
        public override Task<HealthReport> CheckHealthAsync(Func<HealthCheckRegistration, bool>? predicate,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("segredo-sintetico");
    }
}
