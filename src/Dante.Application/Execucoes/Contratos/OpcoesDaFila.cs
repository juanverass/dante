namespace Dante.Application.Execucoes;

// Parâmetros da fila durável (AD-67), configuráveis em Orquestracao:Fila. Slots = 1 enquanto a contenção estrita da
// AD-63 não estiver ativa no serviço (#300).
public sealed record OpcoesDaFila
{
    public TimeSpan Lease { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan Polling { get; init; } = TimeSpan.FromSeconds(5);
    public int Slots { get; init; } = 1;
    public TimeSpan BackoffInicial { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan BackoffMaximo { get; init; } = TimeSpan.FromMinutes(10);

    public void Validar()
    {
        if (Lease <= TimeSpan.Zero || Heartbeat <= TimeSpan.Zero || Polling <= TimeSpan.Zero)
            throw new ArgumentException("Lease, heartbeat e polling da fila precisam ser positivos.");
        // Ao menos duas renovações cabem num lease: uma falha isolada de heartbeat não perde a posse.
        if (Heartbeat * 2 > Lease) throw new ArgumentException("O heartbeat da fila precisa caber duas vezes no lease.");
        if (Slots < 1) throw new ArgumentException("A fila precisa de ao menos um slot.");
        if (BackoffInicial <= TimeSpan.Zero || BackoffMaximo < BackoffInicial)
            throw new ArgumentException("Backoff da fila inválido.");
    }
}
