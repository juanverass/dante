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
        Para atualizar conhecimento confirmado, capture a nova versão como candidato e apresente a proposta ao usuário;
        não declare o conhecimento anterior substituído nem duplicatas fundidas sem operação suportada e confirmação.
        brain_confirmar_candidato prepara consolidação de candidato. Aprovação MCP não confirma negócio:
        apresente a proposta e peça confirmar no Telegram. Propostas de relação e descarte também exigem
        confirmação no Telegram. Capture itens individualmente e reporte possíveis duplicidades.
        Não há ferramenta MCP de fusão de conhecimentos duplicados;
        consulte os equivalentes e apresente-os para revisão, sem simular uma fusão.
        Falha, ausência ou revogação das ferramentas significa indisponibilidade da capacidade interna do Brain;
        informe essa limitação sem inventar leitura ou persistência bem-sucedida, sem procurar plugins ou URL externa.
        Respeite autorização, sensibilidade, revisões e confirmação; nunca capture transcript ou raciocínio privado.
        """;

    internal static string ParaSessao(IReadOnlyList<AgentToolServer>? servidores) => Instrucoes + "\n" +
        (servidores?.Any(s => s.Name == ServidorMcpDoBrain.Nome) == true
            ? "O servidor dante_brain foi disponibilizado nesta sessão; use suas ferramentas internas."
            : "A capacidade interna do Brain está indisponível nesta sessão (configuração, serviço, identidade ou escopo autorizado ausente). Informe a indisponibilidade interna se solicitada.");
}
