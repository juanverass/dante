namespace Dante.Application.IntegracaoDeCi;

// Texto vindo do CI (log, anotação, comentário de PR) depois da sanitização: sem sequências de terminal, sem caracteres
// invisíveis, com linhas de credencial aparente redigidas e limitado ao final (onde a falha costuma estar). Continua
// sendo dado não confiável: só vai a um agente pelo envelope de ParaContextoDeAgente.
public sealed record TrechoDeLogDeCi
{
    internal TrechoDeLogDeCi(string texto, bool truncado, int linhasRedigidas)
    {
        Texto = texto;
        Truncado = truncado;
        LinhasRedigidas = linhasRedigidas;
    }

    public string Texto { get; }
    public bool Truncado { get; }
    public int LinhasRedigidas { get; }

    public string ParaContextoDeAgente(string origem) => SanitizacaoDeTextoDeCi.DelimitarComoDadoNaoConfiavel(origem, this);
}
