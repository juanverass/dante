namespace Dante.Application.Execucoes;

// Executar: a etapa está pronta para rodar. Interromper: a tentativa anterior estava em execução quando a posse se
// perdeu (reinício do Worker, lease vencido) e a política de retomada decide o que fazer. Descartar: não há o que
// avançar (execução encerrada, esperando humano, ou trabalho sem alvo).
public enum AcaoDoTrabalho
{
    Executar,
    Interromper,
    Descartar
}

// Origem: "host/boot" de quem enfileirou, quando o trabalho a registra.
public sealed record AvaliacaoDoTrabalhoDto(AcaoDoTrabalho Acao, AlvoDoTrabalhoDto? Alvo, string? Motivo,
    string? Origem = null);
