using Dante.Domain.Execucoes;
using Dante.Domain.Grupos;

namespace Dante.Application.Execucoes;

// O que a conclusão de um passo gravou: o próximo passo enfileirado, o gate aberto (com o hash a confirmar), o bloqueio
// (com o motivo) ou o encerramento da execução (falha ou cancelamento).
public sealed record AvancoDoRoteiroDto(EstadoDaExecucao Estado, EstadoDaEtapa EstadoDaEtapa,
    TipoDePassoDoRoteiro? ProximoPasso = null, int? Rodada = null, string? HashDoArtefato = null, string? Motivo = null)
{
    public bool GateAberto => Estado == EstadoDaExecucao.AguardandoAprovacao;
}
