namespace Dante.Domain.Grupos;

// Quem decide a ordem do trabalho num grupo (AD-69). Roteiro: o orquestrador (código) executa uma sequência fixa e
// nenhum modelo decide a ordem. O coordenador por papel entra na etapa 8 do roadmap, com o seu próprio valor.
public enum ModoDeCoordenacao
{
    Roteiro
}
