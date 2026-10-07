namespace Dante.Worker.Telegram;

// Static examples never interpolate the user's repositories, settings or pending requests.
internal static class TelegramCommandHelp
{
    internal sealed record Entry(string Command, string Category, string Description, string Examples, string Details = "");

    internal static IReadOnlyList<Entry> Entries { get; } = Array.AsReadOnly<Entry>(
    [
        new("/help", "BÁSICOS", "Descubra os comandos ou veja ajuda específica.", "/help\n/help session"),
        new("/ping", "BÁSICOS", "Verifique se o bot responde.", "/ping"),
        new("/status", "BÁSICOS", "Consulte jobs, suas sessões, pedidos pendentes e entregas recentes.", "/status"),
        new("/uso", "BÁSICOS", "Consulte quanto da cota da assinatura do Claude ou do Codex já foi usado.",
            "/uso codex\n/uso claude", "/uso claude|codex mostra o percentual usado da janela de sessão definida pelo provedor " +
            "(não é a sessão S000001 nem o contexto da conversa) e da semana, e quanto falta para a janela de sessão renovar. " +
            "Os valores são da conta autenticada na CLI do host, inclusive uso fora do D.A.N.T.E. Não abre sessão, não inicia " +
            "turno e não altera preferências; métrica que o provedor não informar aparece como indisponível. " +
            "O Claude Code pode responder com uma leitura própria de até 1 min (até 1 h se o serviço dele falhar).\n" +
            "Resposta fictícia: Janela de sessão (5h): 37% do limite utilizado | Semana: 62% do limite utilizado | " +
            "Janela de sessão renova em: 2h 13min"),

        new("/session", "CONVERSA E SESSÕES", "Gerencie suas sessões e a conversa ativa; efeito apenas na sessão.",
            "/session start codex manual\n/session list\n/session select S000003\n/session select none\n/session stop\n/session close S000003",
            "start [claude|codex] [@alias] [manual|auto|plan] [model=nome|default] [effort=nível|default] abre e seleciona uma sessão. " +
            "Opções omitidas usam suas preferências; overrides valem só para essa sessão. list lista suas sessões; " +
            "select escolhe a sessão; none desmarca. stop [id] interrompe o turno e descarta a fila; close [id] encerra o processo. " +
            "Sem id, stop e close usam a sessão ativa."),
        new("/clear", "CONVERSA E SESSÕES", "Limpe a conversa da sessão ativa e comece do zero no mesmo contexto.",
            "/clear", "Sem argumentos. A sessão ativa precisa estar ociosa (sem turno, fila ou pedido pendente). Mantém agente, " +
            "repositório, modo, modelo e esforço; a conversa upstream é trocada por uma vazia e o histórico anterior não volta. " +
            "Anexos pendentes são descartados. Arquivos e instruções do repositório continuam disponíveis, e limpar não " +
            "renova as cotas de uso (/uso)."),
        new("/compact", "CONVERSA E SESSÕES", "Compacte a conversa da sessão ativa num resumo e continue de onde parou.",
            "/compact", "Sem argumentos. A sessão ativa precisa estar ociosa e ter ao menos uma resposta. O agente resume a " +
            "conversa pelo mecanismo próprio da CLI e segue nela com o resumo, mantendo sessão, agente, repositório, modo, " +
            "modelo e esforço. Um aviso chega no início e outro no fim; até lá, mensagens são recusadas e /session stop " +
            "cancela. O Claude informa os tokens antes e depois; o Codex não informa. Compactar não apaga a conversa " +
            "(use /clear) nem renova as cotas de uso (/uso)."),
        new("/steer", "CONVERSA E SESSÕES", "Oriente o turno da sessão ativa; no Claude, interrompe e prioriza a orientação.",
            "/steer não altere os arquivos de banco"),

        new("/agent", "AGENTE E CONTEXTO", "Consulte ou salve o agente padrão; a sessão ativa mantém seu agente.",
            "/agent\n/agent set claude", "set aceita claude ou codex. Preferência persistente para novas conversas e execuções sem agente explícito."),
        new("/use", "AGENTE E CONTEXTO", "Consulte ou salve o contexto padrão; a sessão ativa mantém seu repositório.",
            "/use\n/use @exemplo\n/use general", "Preferência persistente. @alias deve estar cadastrado; general usa o workspace geral. " +
            "Um @alias explícito no pedido é override só daquela execução e não muda essa preferência."),

        new("/model", "MODELOS E MODO", "Consulte, liste ou salve o modelo por agente para novas sessões e one-shot.",
            "/model\n/model codex\n/model claude default", "/model claude|codex [modelo|default]. Sem modelo, lista os disponíveis; " +
            "default volta ao padrão da CLI. Preferência persistente; a sessão ativa mantém seu modelo."),
        new("/effort", "MODELOS E MODO", "Consulte ou salve o esforço por agente/modelo para novas sessões e one-shot.",
            "/effort\n/effort codex\n/effort codex default", "/effort claude|codex [nível|default]. Escolha um nível anunciado pela CLI; " +
            "default volta ao padrão da CLI. Preferência persistente; a sessão ativa mantém seu esforço."),
        new("/mode", "MODELOS E MODO", "Consulte ou salve o modo padrão; use /mode session para trocar na sessão ativa.",
            "/mode\n/mode manual\n/mode session manual", "manual pede aprovação, auto é automático e plan é planejamento. " +
            "/mode <modo> salva o padrão; /mode session <modo> mantém o padrão e troca na sessão ociosa. " +
            "Claude confirma imediatamente; Codex aplica no próximo turno. Aguarde ou interrompa explicitamente um turno ativo."),
        new("/permissions", "MODELOS E MODO", "Consulte ou salve a mesma preferência persistente de /mode.",
            "/permissions\n/permissions plan", "Aceita manual, auto ou plan; aplica-se apenas às novas sessões."),

        new("/claude", "EXECUÇÃO AVULSA", "Execute um job one-shot com Claude; não altera a conversa nem as preferências.",
            "/claude explique arquitetura hexagonal", "/claude [@alias] pedido. Sem alias, usa o contexto padrão; alias explícito vale só para esse job."),
        new("/codex", "EXECUÇÃO AVULSA", "Execute um job one-shot com Codex; não altera a conversa nem as preferências.",
            "/codex @exemplo revise o README", "/codex [@alias] pedido. Sem alias, usa o contexto padrão; alias explícito vale só para esse job."),
        new("/cancel", "EXECUÇÃO AVULSA", "Solicite cancelamento de um job; para um turno interativo, use /session stop.",
            "/cancel J000001"),

        new("/repos", "REPOSITÓRIOS", "Liste os repositórios cadastrados.", "/repos"),
        new("/repo", "REPOSITÓRIOS", "Cadastre, consulte ou remova repositórios e configurações persistentes de ambiente.",
            "/repo add @exemplo /caminho/do/repositorio\n/repo show @exemplo\n/repo remove @exemplo\n" +
            "/repo env set @exemplo APP_ENV development\n/repo env bind @exemplo API_TOKEN TOKEN_DO_HOST\n" +
            "/repo env list @exemplo\n/repo env remove @exemplo APP_ENV",
            "add exige caminho absoluto de uma raiz Git; pode receber owner/repo após o caminho. Use aspas se houver espaços. " +
            "Substitua o caminho fictício pelo seu. env set é só para valores não sensíveis; " +
            "env bind referencia o nome de uma variável já definida no host, sem enviar o segredo pelo Telegram."),

        new("/google", "PLANILHAS", "Conecte, consulte ou desconecte a conta Google usada pelas planilhas.",
            "/google connect\n/google status\n/google disconnect",
            "connect devolve um link para abrir no navegador deste computador; o Google volta para um endereço local do " +
            "D.A.N.T.E. e o resultado chega aqui. A credencial fica cifrada no host e nunca passa pelo Telegram. disconnect " +
            "revoga o acesso e apaga a credencial. Novas sessões recebem as ferramentas de planilha quando a conta está conectada."),
        new("/planilhas", "PLANILHAS", "Liste a conexão e as planilhas cadastradas.", "/planilhas"),
        new("/planilha", "PLANILHAS", "Cadastre, consulte ou remova uma planilha pela URL ou Spreadsheet ID.",
            "/planilha add financas https://docs.google.com/spreadsheets/d/ID_FICTICIO/edit gastos mensais\n/planilha show financas\n/planilha remove financas",
            "add <alias> <url ou id> [descrição] confirma o acesso e guarda o alias; show mostra abas, área usada e mesclagens; " +
            "remove tira do cadastro sem alterar a planilha. Depois converse normalmente (\"quanto gastei em setembro na " +
            "planilha financas?\"): o agente lê, busca e edita pelas ferramentas de planilha, e escritas seguem o modo de aprovação."),

        new("/approve", "INTERAÇÃO HUMANA", "Fallback contextual: aprove uma ação uma vez, quando o agente pedir.",
            "/approve S000001 T000002 R000003"),
        new("/approve-session", "INTERAÇÃO HUMANA", "Fallback contextual: aprove na sessão, somente quando o agente oferecer essa opção.",
            "/approve-session S000001 T000002 R000003"),
        new("/deny", "INTERAÇÃO HUMANA", "Fallback contextual: negue uma ação, com motivo opcional.",
            "/deny S000001 T000002 R000003 não permitido"),
        new("/input", "INTERAÇÃO HUMANA", "Fallback contextual: responda às perguntas de uma solicitação do agente.",
            "/input S000001 T000002 R000004 primeira resposta | segunda resposta",
            "Use os IDs exibidos no pedido e responda na ordem das perguntas, separando respostas por |. " +
            "Uma só pergunta precisa de uma só resposta. Apenas o dono pode responder a uma solicitação ainda válida."),

        new("/vitrine", "ENTREGA", "Monte uma imagem para LinkedIn com os prints da conversa; o agente escreve os textos.",
            "/vitrine mostre o modo alto contraste do site\n/vitrine título mais curto e destaque no segundo print",
            "Envie os prints antes ou use /vitrine como legenda. O D.A.N.T.E. cola os prints sem alterá-los, com título, " +
            "subtítulo, etiquetas e rodapé, e envia o PNG; cada novo /vitrine na conversa gera outra versão. A sessão deve estar ociosa."),
        new("/send", "ENTREGA", "Envie um arquivo do diretório da sessão ativa; não executa o agente.",
            "/send resultado.png", "Caminho relativo à sessão; até 50 MB. Arquivos de credenciais e sessões com segredos vinculados são recusados."),
        new("/resend", "ENTREGA", "Reenvie partes pendentes de uma entrega recente; não executa o agente novamente.",
            "/resend J000001\n/resend S000001/T000002\n/resend F000001", "Aceita ID de job, sessão, sessão/turno ou arquivo. Só recupera entregas disponíveis nesta execução do Worker.")
    ]);

    private const string ExamplesNotice = "Exemplos fictícios: substitua @exemplo por um alias cadastrado e os IDs pelos exibidos no seu pedido ou /status.";

    internal static IReadOnlyList<string> Messages(string topic)
    {
        if (topic.Length > 0)
        {
            var name = "/" + topic.TrimStart('/');
            var entry = Entries.FirstOrDefault(entry => entry.Command.Equals(name, StringComparison.OrdinalIgnoreCase));
            return entry is null ? ["Comando de ajuda não encontrado. Use /help para ver todos os comandos disponíveis."] :
                [entry.Category + "\n\n" + Format(entry, detailed: true) + "\n\n" + ExamplesNotice];
        }

        return Entries.GroupBy(entry => entry.Category).Select((group, index) =>
            (index == 0 ? "Comandos do D.A.N.T.E.\n" +
                "Mensagem comum conversa com a sessão ativa; sem sessão, abre uma com suas preferências.\n" +
                "Use /help nome para detalhes, por exemplo /help session.\n" + ExamplesNotice + "\n\n" : "") +
            group.Key + (group.Key == "INTERAÇÃO HUMANA" ? "\nUse os botões oferecidos; estes comandos são fallback, não é preciso decorá-los." : "") +
            "\n\n" + string.Join("\n\n", group.Select(entry => Format(entry, detailed: false)))).ToArray();
    }

    private static string Format(Entry entry, bool detailed) =>
        entry.Command + " — " + entry.Description + (detailed && entry.Details.Length > 0 ? "\n" + entry.Details : "") +
        "\nExemplos:\n" + entry.Examples;
}
