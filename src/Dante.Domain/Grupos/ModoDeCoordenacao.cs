namespace Dante.Domain.Grupos;

// Quem decide a ordem do trabalho num grupo. Roteiro (AD-69): o orquestrador (código) executa uma sequência fixa e
// nenhum modelo decide a ordem. Coordenador (AD-75, #310): um participante com o papel de coordenador propõe um plano
// estruturado; o orquestrador o valida, uma pessoa o aprova pelo hash e o código despacha cada unidade como delegação
// somente leitura. O coordenador não recebe nenhuma permissão por ser coordenador.
public enum ModoDeCoordenacao
{
    Roteiro,
    Coordenador
}
