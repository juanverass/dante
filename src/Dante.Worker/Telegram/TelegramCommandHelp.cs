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
            "/session start codex manual\n/session start revisor @exemplo\n/session list\n/session select S000003\n/session select none\n/session stop\n/session close S000003",
            "start [claude|codex] [@alias] [manual|auto|plan] [model=nome|default] [effort=nível|default] abre e seleciona uma sessão. " +
            "start <handle> abre com um agente registrado (veja /agentes): congela a versão atual dele, com modo padrão, " +
            "modelo e esforço da versão; um modo explícito só pode restringir. " +
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
        new("/agentes", "AGENTE E CONTEXTO", "Liste os agentes embutidos (claude, codex) e os registrados.",
            "/agentes\n/agentes todos", "todos inclui os arquivados. Agentes registrados exigem o banco do D.A.N.T.E. habilitado."),
        new("/agente", "AGENTE E CONTEXTO", "Crie, consulte, edite (nova versão), desative, reative ou arquive um agente registrado.",
            "/agente criar revisor driver=codex capacidades=revisar repos=@exemplo\n/agente ver revisor\n" +
            "/agente editar revisor modo=plan effort=high\n/agente desativar revisor\n/agente arquivar revisor",
            "criar <handle> driver=claude|codex [model=] [effort=] [modo=plan|manual|auto] [maximo=] [escrita=sim|nao] " +
            "[capacidades=implementar,revisar,pesquisar,planejar] [ferramentas=brain,planilhas] [repos=@alias,…|*] " +
            "[simultaneas=N] [tokens=N] [turnos=N] [minutos=N]; as instruções do papel vão nas linhas seguintes. " +
            "Sem opções, o agente só lê (plan), não recebe ferramentas e só trabalha no workspace geral. Handle: minúsculo, " +
            "sem @ (que é repositório), único e nunca reutilizado. editar cria uma versão nova; sessões abertas mantêm a " +
            "delas. nome|descricao <handle> <texto> mudam a apresentação sem versão. desativar recusa novas sessões e " +
            "tarefas sem interromper as ativas; arquivar exige desativado e sem sessão ativa. Credenciais nunca entram no cadastro."),
        new("/grupos", "AGENTE E CONTEXTO", "Liste os grupos de agentes registrados.",
            "/grupos\n/grupos todos", "todos inclui os arquivados. Grupos exigem o banco do D.A.N.T.E. habilitado."),
        new("/grupo", "AGENTE E CONTEXTO", "Crie, consulte, edite (nova versão), ative, desative ou arquive um grupo de agentes.",
            "/grupo criar engenharia roteiro=implementar:dev,revisar:rev,corrigir:dev,aprovar rodadas=2 repos=@exemplo\n" +
            "/grupo ver engenharia\n/grupo ativar engenharia v1 <hash>\n/grupo editar engenharia rodadas=3\n" +
            "/grupo engenharia @exemplo crie o endpoint de saúde\n" +
            "/grupo criar pesquisa coordenador=chefe repos=@exemplo",
            "criar <nome> roteiro=implementar:<papel>,revisar:<papel>[,corrigir:<papel>],aprovar [rodadas=N] " +
            "[repos=@alias,…] [duracao=minutos]; participantes nas linhas seguintes, um por linha: <papel>=<handle> " +
            "[escrita] [: responsabilidades] (por exemplo dev=codex-dev escrita: implementa a tarefa e " +
            "rev=codex-rev: revisa o diff). Nesta etapa os participantes são agentes Codex registrados: quem implementa " +
            "e corrige tem escrita e a capacidade implementar; quem revisa não tem escrita e tem a capacidade revisar. " +
            "editar cria uma versão nova, que só vale depois de ativar <nome> v<N> <hash> (o hash exibido, ao menos 12 " +
            "caracteres): a ativação é a decisão humana e não concede nada além das permissões de cada agente. " +
            "<nome> @alias <tarefa> executa o roteiro da versão ativa num repositório: cada passo numa sessão Codex " +
            "própria, o revisor só lê e registra o veredito pela ferramenta, e a decisão final é sua no /execucao. " +
            "criar <nome> coordenador=<papel> cria um grupo coordenado: sem roteiro e sem escrita (que depende do " +
            "gateway de aprovação); a cada <nome> @alias <tarefa>, o coordenador propõe um plano somente leitura de " +
            "consultas aos outros papéis, você o aprova pelo hash no /execucao antes de qualquer delegação, as unidades " +
            "rodam uma por vez e o coordenador entrega uma síntese que o servidor confere só na estrutura. " +
            "desativar recusa execuções novas sem interromper as iniciadas; arquivar exige desativado. Nome minúsculo, " +
            "único e nunca reutilizado. Credenciais nunca entram no cadastro."),
        new("/tarefa", "AGENTE E CONTEXTO", "Peça uma tarefa somente leitura a um agente registrado.",
            "/tarefa revisor @exemplo revise o README",
            "/tarefa <handle> [@alias] pedido. Roda sempre em modo plan, mesmo que o agente permita escrita: lê e analisa, " +
            "sem alterar arquivos. Entra na fila durável (resposta \"Tarefa E… enfileirada\") e o resultado chega quando a " +
            "etapa termina; depois de um reinício do Worker, a tarefa pendente é cancelada com aviso."),
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
        new("/cancel", "EXECUÇÃO AVULSA", "Cancele um job seu ou uma tarefa; para um turno interativo, use /session stop.",
            "/cancel J000001\n/cancel E42 não preciso mais",
            "/cancel <J…|E…> [motivo]. Só alcança execuções suas. Uma tarefa ainda na fila é cancelada na hora, sem iniciar " +
            "processo; uma em andamento recebe o pedido, que fica registrado, e o D.A.N.T.E. avisa quando ela for encerrada. " +
            "Execução encerrada não muda. Não há pausa nem retomada."),

        new("/execucoes", "ACOMPANHAMENTO", "Liste suas execuções: as ativas e as últimas encerradas.",
            "/execucoes\n/execucoes todas", "Mostra código, tipo, agente, estado, etapa e idade. todas inclui mais encerradas. " +
            "Exige o ledger de execuções habilitado no Worker."),
        new("/execucao", "ACOMPANHAMENTO", "Veja o detalhe de uma execução sua: etapas, sessão, motivo e erro.",
            "/execucao E42\n/execucao J000001\n/execucao aprovar E42 <hash>\n/execucao corrigir E42 <hash> trate o caso vazio\n" +
            "/execucao recusar E42\n/execucao consolidacao E42", "Mostra etapas (estado, tentativa, agente, modelo, sessão), contexto pelo " +
            "@alias e erro com proteção de segredos. A saída do agente não é guardada e não aparece aqui. Num roteiro de " +
            "grupo, mostra também participantes, passos e, na aprovação humana, o hash do resultado: aprovar e corrigir " +
            "exigem esse hash (ao menos 12 caracteres), recusar não, e só valem se a worktree ainda tiver exatamente o " +
            "conteúdo revisado (senão, revisar E42 pede uma nova revisão); a decisão vale uma vez só e não faz commit, push, PR " +
            "nem merge. objetivo E42 <tarefa> reenvia uma tarefa que a redação protegeu. Numa coordenação de grupo, " +
            "mostra o plano e as unidades; aprovar, corrigir e recusar decidem o plano pelo hash dele (aprovar autoriza só " +
            "esse plano somente leitura), e consolidacao E42 mostra a trilha das unidades e, à parte, a síntese do " +
            "coordenador, validada só na estrutura: divergências aparecem, não são resolvidas."),
        new("/aguardando", "ACOMPANHAMENTO", "Veja quais execuções suas estão esperando e por quê.", "/aguardando",
            "Na fila, aguardando nova tentativa, interrompida ou bloqueada. Não há posição numérica: a fila não garante ordem estável."),
        new("/acoes", "ACOMPANHAMENTO", "Veja e decida os pedidos de ação protegida que aguardam você.",
            "/acoes\n/acoes aprovar 1a2b3c4d <hash>\n/acoes recusar 1a2b3c4d não agora",
            "Cada pedido mostra a ação, o alvo, os parâmetros com proteção de segredos, o hash curto e o prazo (24 h por " +
            "padrão), com botões Aprovar e Recusar. Aprovar exige o hash exibido e autoriza uma única execução daquele " +
            "conteúdo exato; qualquer mudança invalida a aprovação. Pedido vencido expira sem executar. Exige o ledger de " +
            "execuções habilitado no Worker."),

        new("/ci", "ACOMPANHAMENTO", "Veja como está o CI de um PR, no commit atual dele.",
            "/ci 12\n/ci @exemplo 12",
            "/ci [@alias] <número do PR>. Também em linguagem natural: como está o CI do PR 12 do @exemplo?. Sem alias, usa o repositório da sessão ativa e, sem ela, o repositório ativo " +
            "(/use). Lê o PR agora e mostra só as execuções do commit atual dele: as de commits anteriores não contam. " +
            "Mostra cada workflow (passou, falhou, em andamento, na fila, aguardando runner, cancelado), o que falhou ou " +
            "falta, e os links do PR e das execuções. Sem permissão ou provedor fora do ar aparecem como tal, nunca como " +
            "falha. Não avalia se o PR pode ser mesclado (proteção da branch, checks obrigatórios e revisões), não " +
            "reexecuta nada e não acompanha sozinho: consulte de novo quando quiser. Exige uma conexão de CI no host que " +
            "libere o repositório; repositórios vinculados não herdam a do principal."),

        new("/repos", "REPOSITÓRIOS", "Liste os repositórios cadastrados.", "/repos"),
        new("/repo", "REPOSITÓRIOS", "Cadastre, consulte, vincule ou remova repositórios e configurações persistentes de ambiente.",
            "/repo add @exemplo /caminho/do/repositorio\n/repo show @exemplo\n/repo remove @exemplo\n" +
            "/repo link @exemplo_front @exemplo\n/repo unlink @exemplo_front @exemplo\n/repo links @exemplo_front\n" +
            "/repo env set @exemplo APP_ENV development\n/repo env bind @exemplo API_TOKEN TOKEN_DO_HOST\n" +
            "/repo env list @exemplo\n/repo env remove @exemplo APP_ENV",
            "add exige caminho absoluto de uma raiz Git; pode receber owner/repo após o caminho. Use aspas se houver espaços. " +
            "Substitua o caminho fictício pelo seu. link dá às execuções do primeiro repositório acesso de leitura e escrita " +
            "ao segundo, na mesma tarefa; só nessa direção, sem seguir os vínculos do vinculado, e só com as variáveis do " +
            "primeiro. env set é só para valores não sensíveis; " +
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
