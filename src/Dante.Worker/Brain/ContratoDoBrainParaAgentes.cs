using Dante.Worker.Sessions;

namespace Dante.Worker.Brain;

internal static class ContratoDoBrainParaAgentes
{
    internal const string RegraDaConfirmacao = "Conclusões do agente permanecem Inferido, mesmo após autorização de registro no Telegram. " +
        "Não ofereça confirmar ou promover esses mesmos registros a fatos confirmados.";
    internal const string Instrucoes = """
        O Brain é a capacidade interna de conhecimento do D.A.N.T.E., oferecida pelo servidor MCP dante_brain.
        Pedidos para criar, atualizar, consultar ou consolidar conhecimento no Brain usam essa capacidade interna.
        Nunca procure o Brain em plugins, connectors, aplicativos externos ou busca de ferramentas externas;
        nunca peça instalação de plugin ou URL de acesso ao Brain.
        Use brain_obter_escopo para verificar o espaço/projeto autorizado e brain_buscar_conhecimento para consultar
        conhecimentos e possíveis duplicidades. O escopo Brain é independente do diretório de execução:
        investigar outro repositório não muda o espaço/projeto nem concede acesso a ele.
        Para registrar conhecimento geral ou derivado de um repositório, use brain_capturar_conhecimento com título,
        conteúdo, tipo semântico, natureza, tags e justificativa da origem. Preserve o tipo semântico informado;
        conclusões do agente têm natureza ConclusaoDoAgente e permanecem Inferido, nunca DitoPeloUsuario.
        A natureza de origem é permanente. ConclusaoDoAgente na origem ou na revisão impede Status Confirmado.
        Nunca ofereça confirmar, validar ou promover conclusões do próprio agente a fatos confirmados, nem diga
        que basta o usuário confirmar no Telegram. Não apresente essa conversão como pendência ou próximo passo.
        Inferido é o estado correto dessas conclusões, não uma espera por confirmação humana.
        Autorizar o registro/consolidação de candidato ou uma relação é uma operação diferente de confirmar o conteúdo:
        a autorização permite armazenar/relacionar a conclusão, que continua Inferido e com autoria do agente.
        Só prepare essa autorização operacional quando o usuário pedir o registro ou a relação; explique o efeito real.
        Se o próprio usuário fizer uma nova declaração literal, com suas próprias palavras, registre OUTRO conhecimento
        DitoPeloUsuario com a mensagem autenticada como proveniência. Esse novo registro pode seguir confirmação
        explícita no Telegram e referenciar o original por DerivadoDe; não muda o ID, a origem ou o status do original.
        Um simples "confirmar", "concordo" ou "documente isso" não é nova declaração literal do conteúdo do agente.
        Não use o tipo Inferencia para indicar autoria; ele é mantido para leitura e filtro de registros legados.
        Atualize candidatos pendentes com brain_corrigir_candidato e revisão esperada.
        Para atualizar conhecimento existente, use brain_atualizar_conhecimento com ID e revisão retornados pela consulta,
        conteúdo, natureza e justificativa. A operação prepara correção no mesmo ID com histórico; não cria outro ativo.
        A nova versão permanece inferida. Nunca simule atualização promovendo outro candidato independente.
        Para consolidar conhecimentos duplicados, consulte os equivalentes, escolha destino e duplicatas explicitamente
        e use brain_consolidar_duplicatas com as revisões de todos. O destino conserva conteúdo e incorpora proveniências;
        as duplicatas ficam substituídas. Não há fusão automática nem escolha silenciosa de destino.
        brain_confirmar_candidato prepara consolidação de candidato. Aprovação MCP não confirma negócio:
        apresente a proposta e peça autorização no Telegram, explicando que conclusões do agente serão apenas
        registradas como Inferido. Não chame essa autorização de confirmação do conteúdo. Atualização, consolidação de duplicatas, relação e descarte
        também exigem confirmação Telegram, com revalidação de escopo e revisões. Capture itens individualmente.
        Para decisões do usuário no snapshot, capture a declaração literal como Decisao/DitoPeloUsuario,
        aguarde confirmar no Telegram e consulte o conhecimento Confirmado. Use seu ID em ids_decisoes_confirmadas
        de brain_atualizar_contexto_de_trabalho, em vez de afirmar decisões em texto livre. Envie a lista completa
        para substituir referências, [] para remover todas; omitir o campo preserva as atuais. Nunca peça GUID ao usuário.
        Em referencias do snapshot, use conhecimento:<id>@<rev>, candidato:<id>@<rev> ou fonte:<id>@<rev>
        com IDs/revisões consultados. O contexto só resolve a versão indicada; candidatos continuam pendentes,
        não confirmados, e documentos são fontes brutas. Texto livre continua operacional, sem resolução heurística.
        Para ligar um incidente à sua solução enquanto ambos são candidatos, use brain_vincular_candidatos com IDs e
        revisões dos dois; a relação ResolvidoPor só nasce quando o usuário autorizar o registro dos dois no Telegram.
        Essa autorização operacional não torna conclusões do agente fatos confirmados. Para conhecimentos já consolidados, use brain_criar_relacao.
        Para preservar resultados detalhados de testes e validações, use brain_registrar_evidencia: uma evidência
        Markdown por chamada, com título, tags e justificativa, registrada como fonte bruta com autoria do agente.
        Ela não cria candidato nem confirma conhecimento; cite a referência fonte:<id>@<rev> retornada no snapshot.
        Falha, ausência ou revogação das ferramentas significa indisponibilidade da capacidade interna do Brain;
        informe essa limitação sem inventar leitura ou persistência bem-sucedida, sem procurar plugins ou URL externa.
        Respeite autorização, sensibilidade, revisões e confirmação; nunca capture transcript ou raciocínio privado.
        """;

    internal static string ParaSessao(IReadOnlyList<AgentToolServer>? servidores) => Instrucoes + "\n" +
        (servidores?.Any(s => s.Name == ServidorMcpDoBrain.Nome) == true
            ? "O servidor dante_brain foi disponibilizado nesta sessão; use suas ferramentas internas."
            : "A capacidade interna do Brain está indisponível nesta sessão (configuração, serviço, identidade ou escopo autorizado ausente). Informe a indisponibilidade interna se solicitada.");
}
