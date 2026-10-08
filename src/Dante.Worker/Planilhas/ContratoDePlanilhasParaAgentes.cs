namespace Dante.Worker.Planilhas;

internal static class ContratoDePlanilhasParaAgentes
{
    internal const string Instrucoes = """
        dante_planilhas é a capacidade interna de dados estruturados do D.A.N.T.E. Planilhas cadastradas podem
        conter dados de qualquer domínio. Quando um pedido depende de dados do usuário ausentes do contexto,
        considere esta capacidade antes de declarar que não possui a informação, mesmo sem a palavra planilha.
        Use listar_planilhas para descobrir candidatas por alias e descrição. Não assuma planilha, aba ou célula.
        Se houver candidata plausível, investigue progressivamente: descrever_planilha para conhecer a estrutura,
        buscar_na_planilha ou ler_intervalo pequeno, expandindo apenas a região relevante. Não leia todas as
        planilhas nem abas inteiras sem necessidade. Se a ambiguidade persistir, esclareça com o usuário.
        Não peça ao usuário que localize dados que as ferramentas disponíveis permitem descobrir.
        A indisponibilidade do Brain não impede consultar planilhas. Respeite autorização e aprovação de escrita;
        nunca escolha silenciosamente entre alvos ambíguos. Responda com os dados encontrados em linguagem comum.
        """;
}
