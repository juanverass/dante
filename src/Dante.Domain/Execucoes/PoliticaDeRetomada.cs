namespace Dante.Domain.Execucoes;

// Como uma etapa interrompida pode continuar: leitura idempotente volta sozinha, escrita pede confirmação e ação
// externa de resultado incerto nunca é repetida automaticamente.
public enum PoliticaDeRetomada
{
    Automatica,
    ComConfirmacao,
    Nunca
}
