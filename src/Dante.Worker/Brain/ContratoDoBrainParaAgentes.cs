using Dante.Worker.Sessions;

namespace Dante.Worker.Brain;

internal static class ContratoDoBrainParaAgentes
{
    internal const string Instrucoes = """
        O Brain é a capacidade interna de conhecimento do D.A.N.T.E., oferecida pelo servidor MCP dante_brain.
        Pedidos para criar, atualizar, consultar ou consolidar conhecimento no Brain usam essa capacidade interna.
        Nunca procure o Brain em plugins, connectors, aplicativos externos ou busca de ferramentas externas;
        nunca peça instalação de plugin ou URL de acesso ao Brain.
        Use brain_obter_escopo para verificar o espaço/projeto autorizado e brain_buscar_conhecimento para consultar
        conhecimentos e possíveis duplicidades. O escopo Brain é independente do diretório de execução:
        investigar outro repositório não muda o espaço/projeto nem concede acesso a ele.
        Para registrar conhecimento geral ou derivado de um repositório, use brain_capturar_conhecimento com título,
        conteúdo, tags e justificativa da origem; conclusões do agente são Inferencia, nunca DitoPeloUsuario.
        Atualize candidatos pendentes com brain_corrigir_candidato e revisão esperada.
        Para atualizar conhecimento existente, use brain_atualizar_conhecimento com ID e revisão retornados pela consulta,
        conteúdo, natureza e justificativa. A operação prepara correção no mesmo ID com histórico; não cria outro ativo.
        A nova versão permanece inferida. Nunca simule atualização promovendo outro candidato independente.
        Para consolidar conhecimentos duplicados, consulte os equivalentes, escolha destino e duplicatas explicitamente
        e use brain_consolidar_duplicatas com as revisões de todos. O destino conserva conteúdo e incorpora proveniências;
        as duplicatas ficam substituídas. Não há fusão automática nem escolha silenciosa de destino.
        brain_confirmar_candidato prepara consolidação de candidato. Aprovação MCP não confirma negócio:
        apresente a proposta e peça confirmar no Telegram. Atualização, consolidação de duplicatas, relação e descarte
        também exigem confirmação Telegram, com revalidação de escopo e revisões. Capture itens individualmente.
        Falha, ausência ou revogação das ferramentas significa indisponibilidade da capacidade interna do Brain;
        informe essa limitação sem inventar leitura ou persistência bem-sucedida, sem procurar plugins ou URL externa.
        Respeite autorização, sensibilidade, revisões e confirmação; nunca capture transcript ou raciocínio privado.
        """;

    internal static string ParaSessao(IReadOnlyList<AgentToolServer>? servidores) => Instrucoes + "\n" +
        (servidores?.Any(s => s.Name == ServidorMcpDoBrain.Nome) == true
            ? "O servidor dante_brain foi disponibilizado nesta sessão; use suas ferramentas internas."
            : "A capacidade interna do Brain está indisponível nesta sessão (configuração, serviço, identidade ou escopo autorizado ausente). Informe a indisponibilidade interna se solicitada.");
}
