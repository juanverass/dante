# Guia de manutenção do D.A.N.T.E.

> Snapshot documentado: `main` após as Issues #80 e #81, em 2026-10-01.

Este conjunto de documentos existe para responder uma pergunta diferente do README principal:

> **"Eu preciso manter, depurar e evoluir o D.A.N.T.E.; como o sistema realmente funciona?"**

O README principal continua sendo a documentação de uso. Aqui o foco é arquitetura, responsabilidades, fluxos internos, estado, integrações, segurança e pontos de extensão.

## Ordem recomendada de leitura

1. [Arquitetura](ARCHITECTURE.md) — qual arquitetura o projeto usa, limites, decisões e visão macro.
2. [Componentes](COMPONENTS.md) — responsabilidade de cada pasta, classe e abstração importante.
3. [Fluxos](FLOWS.md) — caminhos completos de uma mensagem pelo sistema.
4. [Operação e debugging](OPERATIONS_AND_DEBUGGING.md) — configuração, execução, diagnóstico e falhas comuns.
5. [Testes](TESTING.md) — estratégia, ProcessProbe e como localizar testes por comportamento.
6. [Glossário](GLOSSARY.md) — termos e IDs usados pelo projeto.

## Modelo mental em uma frase

O D.A.N.T.E. é um **monólito modular local em .NET**, executado como um único Worker, que usa o Telegram como adaptador de entrada/saída e controla Claude Code e Codex CLI como processos filhos por meio de dois caminhos:

- **sessão interativa** — caminho padrão para mensagens comuns;
- **job one-shot** — caminho explícito para `/claude` e `/codex`.

## Antes de depurar qualquer coisa

Descubra primeiro em qual camada o defeito está:

```text
Telegram não responde nem /ping
    → configuração / polling / autorização

/ping responde, mas mensagem comum não
    → resolução de contexto / SessionRegistry / driver interativo

sessão inicia, mas não há streaming
    → driver / AgentEvent / TelegramDeliveryService

/claude ou /codex falha
    → runner one-shot / AgentProcessExecutor / CLI

agente terminou, mas resposta não chegou
    → TelegramDeliveryService, não necessariamente o agente

repo/alias/env falha
    → RepositoryRegistry / AssistantSettingsStore
```

Essa separação é importante porque o projeto deliberadamente mantém **estado de execução** e **estado de entrega ao Telegram** como conceitos diferentes.

## Fontes de verdade

Para entender uma regra, use esta ordem:

1. código e testes atuais;
2. [decisões arquiteturais](../context/ARCHITECTURE_DECISIONS.md);
3. [contexto do projeto](../context/PROJECT_CONTEXT.md);
4. [estado atual](../context/CURRENT_STATE.md);
5. Issues/PRs para estado de trabalho em andamento.

Não use histórico de conversa como fonte técnica superior ao repositório.

## Estrutura resumida

```text
src/Dante.Worker/
├── Agents/        processos, runners e catálogo de modelos
├── Jobs/          execuções one-shot
├── Repositories/ catálogo de repositórios e ambientes
├── Sessions/      núcleo das sessões interativas
├── Settings/      preferências persistidas do usuário
├── Telegram/      entrada, comandos, formatação e entrega
├── Program.cs     composição/DI
└── Worker.cs      lifecycle básico do host

tests/
├── Dante.Tests/        testes unitários e integração
└── Dante.ProcessProbe/ CLIs simuladas executadas como processos reais
```

## O que esta documentação não substitui

- o README para comandos de usuário;
- `ARCHITECTURE_DECISIONS.md` para decisões formais e seu histórico;
- Issues/PRs para backlog e trabalho em andamento;
- os testes como especificação executável.

Ela funciona como **mapa do sistema** para tornar essas fontes mais fáceis de navegar.
