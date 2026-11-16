using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Agentes;

namespace Dante.Application.Agentes;

// Validação de entrada do cadastro de agentes (AD-52, AD-68): só olha os valores recebidos. Mensagens vão ao usuário
// como estão. O cadastro não tem campo para credencial, e texto com aparência de segredo é recusado antes de ser
// gravado ou chegar ao prompt de um agente.
internal static class CadastroDeAgentesValidator
{
    internal static string Handle(string? handle)
    {
        var normalizado = handle?.Trim().ToLowerInvariant();
        if (normalizado is { Length: > 0 } && normalizado[0] == '@')
            throw new ArgumentException("Handle de agente não usa @: @ é sempre repositório.");
        if (normalizado is not null && Agente.HandlesReservados.Contains(normalizado))
            throw new ArgumentException($"O handle {normalizado} é reservado.");
        if (!Agente.HandleValido(normalizado))
            throw new ArgumentException(
                "Handle inválido: use de 2 a 31 caracteres minúsculos (letras, números e -), começando por letra.");
        return normalizado!;
    }

    internal static void Validar(NovoAgenteDto novo)
    {
        ArgumentNullException.ThrowIfNull(novo);
        ArgumentNullException.ThrowIfNull(novo.Configuracao);
        Handle(novo.Handle);
        SemSegredo(novo.Nome, novo.Descricao);
        Validar(novo.Configuracao.Modelo, novo.Configuracao.Esforco, padraoPermitido: false);
        SemSegredo(novo.Configuracao.Instrucoes);
    }

    internal static void Validar(AlteracaoDoAgenteDto alteracao)
    {
        ArgumentNullException.ThrowIfNull(alteracao);
        Validar(alteracao.Modelo, alteracao.Esforco, padraoPermitido: true);
        if (alteracao.LimparInstrucoes && alteracao.Instrucoes is not null)
            throw new ArgumentException("Informe novas instruções ou a remoção delas, não as duas.");
        SemSegredo(alteracao.Instrucoes);
        foreach (var limite in new long?[] { alteracao.ExecucoesSimultaneas, alteracao.TokensPorExecucao,
                     alteracao.TurnosPorEtapa, alteracao.MinutosPorEtapa })
            if (limite < 0) throw new ArgumentException("Limite inválido: use um número positivo, ou 0 para remover.");
    }

    internal static void SemSegredo(params string?[] textos)
    {
        if (textos.Any(ProtecaoDeSegredos.ContemSegredo))
            throw new ArgumentException(
                "Possível credencial no cadastro do agente. Credenciais nunca entram no cadastro nem nas instruções; " +
                "configure-as no ambiente do repositório (/repo env).");
    }

    private static void Validar(string? modelo, string? esforco, bool padraoPermitido)
    {
        if (modelo is not null && !(padraoPermitido && EhPadrao(modelo)) && !AgentModelSelection.IsValidName(modelo))
            throw new ArgumentException($"Nome de modelo inválido: {modelo}.");
        if (esforco is not null && !(padraoPermitido && EhPadrao(esforco)) && !AgentModelSelection.IsValidEffort(esforco))
            throw new ArgumentException($"Esforço inválido: {esforco}.");
    }

    // "default" volta modelo ou esforço ao padrão do driver, como em /model e /effort.
    internal static bool EhPadrao(string? valor) => Eh(valor, "default");

    internal static bool Eh(string? valor, string esperado) => string.Equals(valor, esperado, StringComparison.OrdinalIgnoreCase);
}
