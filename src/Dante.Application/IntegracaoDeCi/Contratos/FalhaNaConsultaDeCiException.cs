namespace Dante.Application.IntegracaoDeCi;

// Falha da consulta ao provedor, com mensagem segura: sem credencial, cabeçalho, URL de API nem corpo de resposta.
// RepetirApos vem do limite de taxa ou da indisponibilidade informada pelo provedor, quando houver.
public sealed class FalhaNaConsultaDeCiException(MotivoDaFalhaNaConsultaDeCi motivo, string message,
    TimeSpan? repetirApos = null, Exception? innerException = null) : Exception(message, innerException)
{
    public MotivoDaFalhaNaConsultaDeCi Motivo { get; } = motivo;
    public TimeSpan? RepetirApos { get; } = repetirApos;
}
