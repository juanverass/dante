namespace Dante.Application.IntegracaoDeCi;

// Por que não houve repositório para consultar, antes de qualquer chamada ao provedor.
public enum ProblemaDoRepositorioDeCi
{
    // Sem alias no pedido, sem sessão num repositório e sem repositório ativo.
    SemRepositorio,
    AliasInvalido,
    NaoCadastrado,
    // O repositório ativo do usuário saiu do cadastro: pede nova seleção, nunca cai para outro.
    AtivoForaDoCadastro
}
