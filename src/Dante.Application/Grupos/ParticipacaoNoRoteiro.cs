using Dante.Application.Agentes;
using Dante.Domain.Agentes;
using Dante.Domain.Grupos;

namespace Dante.Application.Grupos;

// O que um agente precisa ter para ocupar um papel do roteiro (AD-69, #308, decisões A e D). Conferido ao salvar uma
// versão, ao ativá-la e ao resolver o grupo para uma execução, sempre contra a versão atual do agente: nada é
// inferido do prompt nem das instruções do agente.
// * Implementar e corrigir: capacidade implementar, escrita na versão do agente e driver com confinamento de escrita
//   comprovado (Codex, AD-71). Claude continua sem escrita.
// * Revisar: capacidade revisar e driver Codex, porque a revisão roda numa sessão Codex com sandbox somente leitura
//   sobre a worktree; o Claude não tem confinamento de filesystem comprovado nem para leitura.
// * PodeEscrever do participante nunca passa do PodeEscrever da versão do agente.
internal static class ParticipacaoNoRoteiro
{
    internal static string? Recusa(ParticipanteDoGrupo participante, string handle, ConfiguracaoDoAgente agente,
        IReadOnlyList<PassoDoRoteiro> roteiro)
    {
        if (participante.PodeEscrever && !agente.PodeEscrever)
            return $"O agente {handle} não tem escrita; o papel {participante.Papel} não pode escrever.";
        foreach (var passo in roteiro.Where(passo => passo.Papel == participante.Papel))
        {
            var (capacidade, acao) = passo.Tipo switch
            {
                TipoDePassoDoRoteiro.Revisar => (CapacidadeDoAgente.Revisar, "revisar"),
                TipoDePassoDoRoteiro.Corrigir => (CapacidadeDoAgente.Implementar, "corrigir"),
                _ => (CapacidadeDoAgente.Implementar, "implementar")
            };
            if (!agente.Capacidades.Contains(capacidade))
                return $"O agente {handle} não declara a capacidade {Nome(capacidade)}, exigida para {acao} " +
                    $"(papel {participante.Papel}).";
            if (passo.Escreve && ConfinamentoDeEscrita.Recusa(agente.Driver) is { } recusa)
                return $"O papel {participante.Papel} ({handle}) escreve: {recusa}";
            if (passo.Tipo == TipoDePassoDoRoteiro.Revisar && agente.Driver != AgentKind.Codex)
                return $"O papel {participante.Papel} ({handle}) revisa numa sessão Codex com sandbox somente leitura; " +
                    $"use um agente Codex.";
        }
        return null;
    }

    private static string Nome(CapacidadeDoAgente capacidade) => capacidade.ToString().ToLowerInvariant();
}
