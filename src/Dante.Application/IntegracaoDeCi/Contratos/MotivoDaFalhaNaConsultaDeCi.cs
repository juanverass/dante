namespace Dante.Application.IntegracaoDeCi;

// Por que o provedor não respondeu. Diz respeito à consulta, nunca ao resultado do CI.
public enum MotivoDaFalhaNaConsultaDeCi
{
    NaoConfigurada,
    NaoAutorizada,
    SemPermissao,
    NaoEncontrada,
    LimiteDeTaxa,
    Indisponivel,
    Inconsistente
}
