using Microsoft.AspNetCore.Diagnostics;

namespace Dante.WebApi;

internal sealed class TratamentoDeErros : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            return false;
        // Não serializar Exception, stack trace, SQL ou conteúdo da requisição.
        await Results.Problem(statusCode: StatusCodes.Status500InternalServerError,
            title: "Falha interna ao processar a requisição.").ExecuteAsync(context);
        return true;
    }
}
