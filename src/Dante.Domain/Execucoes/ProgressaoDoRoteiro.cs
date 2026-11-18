using Dante.Domain.Grupos;

namespace Dante.Domain.Execucoes;

// Transições fixas do roteiro (AD-69, #308, decisão G), decididas por código e nunca por um modelo:
// * início → implementar (rodada 0);
// * implementar concluído → revisar (mesma rodada);
// * revisão aprovada → aprovação humana (mesma rodada);
// * revisão pedindo correção → corrigir (rodada + 1) se o roteiro tem o passo e ainda há rodadas; senão, bloqueia;
// * revisão bloqueada → bloqueia;
// * corrigir concluído → revisar (mesma rodada);
// * correção pedida pela pessoa no gate → corrigir (rodada + 1), sob o mesmo limite de rodadas.
// Esgotar as rodadas sem convergência nunca aprova implicitamente: a execução fica bloqueada com o motivo.
public static class ProgressaoDoRoteiro
{
    public static ProximoPassoDoRoteiro Inicio() => ProximoPassoDoRoteiro.Executar(TipoDePassoDoRoteiro.Implementar, 0);

    public static ProximoPassoDoRoteiro AposPasso(TipoDePassoDoRoteiro concluido, int rodada, VereditoDeRevisao? veredito,
        bool temCorrecao, int rodadasMaximas)
    {
        if (rodada < 0 || rodadasMaximas < 0) throw new ArgumentOutOfRangeException(nameof(rodada));
        switch (concluido)
        {
            case TipoDePassoDoRoteiro.Implementar:
            case TipoDePassoDoRoteiro.Corrigir:
                if (veredito is not null) throw new ArgumentException("Só a revisão tem veredito.", nameof(veredito));
                return ProximoPassoDoRoteiro.Executar(TipoDePassoDoRoteiro.Revisar, rodada);
            case TipoDePassoDoRoteiro.Revisar:
                return veredito switch
                {
                    VereditoDeRevisao.Aprovado => ProximoPassoDoRoteiro.Executar(TipoDePassoDoRoteiro.AprovacaoHumana, rodada),
                    VereditoDeRevisao.RequerCorrecoes => Correcao(rodada, temCorrecao, rodadasMaximas,
                        "o revisor pediu correções"),
                    VereditoDeRevisao.Bloqueado => ProximoPassoDoRoteiro.Bloquear(
                        "O revisor bloqueou o resultado; nada é aprovado sem decisão."),
                    _ => throw new ArgumentException("A revisão termina com um veredito estruturado.", nameof(veredito))
                };
            default:
                throw new ArgumentException("A aprovação humana não avança pela progressão automática.", nameof(concluido));
        }
    }

    // Correção pedida pela pessoa no gate: mesma regra de rodadas, mas sem rodada a recusa volta a ela (o gate
    // continua aberto) em vez de bloquear.
    public static ProximoPassoDoRoteiro CorrecaoPedidaNoGate(int rodada, bool temCorrecao, int rodadasMaximas) =>
        Correcao(rodada, temCorrecao, rodadasMaximas, "a pessoa pediu correção");

    private static ProximoPassoDoRoteiro Correcao(int rodada, bool temCorrecao, int rodadasMaximas, string origem)
    {
        if (!temCorrecao)
            return ProximoPassoDoRoteiro.Bloquear($"O roteiro não tem o passo corrigir e {origem}; nada é aprovado sem decisão.");
        if (rodada + 1 > rodadasMaximas)
            return ProximoPassoDoRoteiro.Bloquear(
                $"Limite de {rodadasMaximas} rodada(s) de correção atingido e {origem}; nada é aprovado sem decisão.");
        return ProximoPassoDoRoteiro.Executar(TipoDePassoDoRoteiro.Corrigir, rodada + 1);
    }
}

// Executar um passo numa rodada, ou bloquear com o motivo.
public sealed record ProximoPassoDoRoteiro(TipoDePassoDoRoteiro? Passo, int Rodada, string? MotivoDoBloqueio)
{
    public bool Bloqueia => Passo is null;

    public static ProximoPassoDoRoteiro Executar(TipoDePassoDoRoteiro passo, int rodada) => new(passo, rodada, null);

    public static ProximoPassoDoRoteiro Bloquear(string motivo) => new(null, 0, motivo);
}
