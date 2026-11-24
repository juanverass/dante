namespace Dante.Application.IntegracaoDeCi;

// Log de um job, separado do estado do job: log ausente, expirado ou negado nunca muda nem esconde o estado observado.
// Trecho existe exatamente quando o log está disponível, e só é criado pela SanitizacaoDeTextoDeCi.
public sealed record LogDeJobDeCiDto
{
    public LogDeJobDeCiDto(IdentidadeDoJobDeCi job, DisponibilidadeDoLogDeCi disponibilidade, TrechoDeLogDeCi? trecho = null)
    {
        Job = job ?? throw new ArgumentNullException(nameof(job));
        if ((disponibilidade == DisponibilidadeDoLogDeCi.Disponivel) != (trecho is not null))
            throw new ArgumentException("Trecho existe só quando o log está disponível.", nameof(trecho));
        Disponibilidade = disponibilidade;
        Trecho = trecho;
    }

    public IdentidadeDoJobDeCi Job { get; }
    public DisponibilidadeDoLogDeCi Disponibilidade { get; }
    public TrechoDeLogDeCi? Trecho { get; }
}
