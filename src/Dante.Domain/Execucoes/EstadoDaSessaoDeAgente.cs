namespace Dante.Domain.Execucoes;

// Registro persistido da sessão viva (AD-20 continua sendo o runtime). Encerrada, Falhou e Interrompida são terminais.
public enum EstadoDaSessaoDeAgente
{
    Iniciando,
    Ativa,
    Encerrada,
    Falhou,
    Interrompida
}
