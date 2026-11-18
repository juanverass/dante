namespace Dante.Domain.Execucoes;

// Veredito estruturado do revisor de um Roteiro (#308, decisão D), registrado pelo caso de uso confiável, nunca lido
// de texto livre. Aprovado: segue para a aprovação humana. RequerCorrecoes: abre a próxima rodada de correção, se
// houver. Bloqueado: o revisor não pode concluir; a execução fica bloqueada para decisão humana.
public enum VereditoDeRevisao
{
    Aprovado,
    RequerCorrecoes,
    Bloqueado
}
