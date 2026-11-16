namespace Dante.Application.Execucoes;

// Quem reivindica trabalhos (AD-67): host, PID e um Guid por boot do processo. Dois boots do mesmo host nunca têm o
// mesmo Boot; a recuperação na inicialização trata qualquer outro boot do mesmo host como morto (o Worker é instância
// única no host, AD-28), e o fencing por geração protege mesmo que não esteja.
public sealed record IdentidadeDoProcessador(string Host, int Pid, Guid Boot)
{
    public string Trabalhador => $"{Host}:{Pid}";

    public static IdentidadeDoProcessador DoProcessoAtual() =>
        new(Environment.MachineName, Environment.ProcessId, Guid.NewGuid());
}
