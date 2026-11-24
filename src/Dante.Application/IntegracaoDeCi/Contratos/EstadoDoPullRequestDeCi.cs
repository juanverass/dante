namespace Dante.Application.IntegracaoDeCi;

// Situação do PR no provedor quando foi lido. Fechado ou mesclado não muda a correlação: o CI exibido continua sendo o
// do head SHA lido agora.
public enum EstadoDoPullRequestDeCi
{
    Aberto,
    Fechado,
    Mesclado
}
