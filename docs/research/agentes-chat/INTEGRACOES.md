# Integrações: MCP, skills, APIs, computadores

## MCP como cliente (conectores)

- **Camada 1, organização**: `connector_connections` (conector do catálogo, URL https, tipo de auth, credencial
  OAuth/API key cifrada); uma conexão ativa por conector **[D]** ADR 0016.
- **Camada 2, agente**: concessões em `agent_scopes` com `connection_id`; só escopos do catálogo do conector
  **[C][D]**.
- **Catálogo próprio** (`CONNECTOR_CATALOG`): para cada conector, escopos com rótulo e o mapa ferramenta MCP →
  escopo, com `sensitive` e template do título da aprovação. Ferramenta que o servidor lista e o catálogo não
  conhece **nunca** é exposta **[D]**. **[O]** 79 conectores no catálogo (GitHub, Linear, Sentry, Notion, Slack,
  Vercel, Supabase…).
- Ferramentas expostas como `<conector>_<ferramenta>`; lista em cache de 1 min; chamadas pelo Tool Gateway
  **[D]**. Servidor arbitrário por URL é permitido com cabeçalhos configurados (ADR 0186).
- OAuth com PKCE, estado de uso único (só o SHA-256 guardado), 10 min **[D]**.
- Muitas ferramentas: só um núcleo vai no prompt; o resto por `find_tools`/`use_tool` **[O]** ADR 0043.

## MCP como servidor

- `PUBLIC_URL/mcp`, HTTP streamable sem sessão, OAuth 2.1 próprio (CIMD, discovery RFC 8414/9728) **[D]** ADR 0119.
- Uso: o Claude/ChatGPT/Cursor de uma pessoa pede a um agente do workspace ("mande isso ao agente de marketing") e
  a resposta volta **[D]**.
- Identidade do chamador por cabeçalhos assinados (ADR 0181) **[D]**.
- **[I]** É a via de integração externa mais limpa com o agentes.chat, se um dia fizer sentido: o D.A.N.T.E. (ou
  um agente dele) seria apenas cliente MCP, sem tocar no código AGPL.

## Bridge MCP para CLIs

- Na sessão Claude Code/Codex do computador, as ferramentas do servidor chegam por um servidor MCP local
  (`agentes`), que repassa ao servidor; o servidor decide tudo (protocolo 8) **[C]** runner `mcp.ts`
  (`McpBridge`).
- O pedido de permissão da própria CLI vira uma ferramenta MCP (`--permission-prompt-tool
  mcp__agentes__permission_prompt`) **[C]** runner `brain.ts`.
- **[I]** O D.A.N.T.E. resolveu o mesmo problema de outro jeito: `--permission-prompt-tool stdio` e
  `control_request can_use_tool` no próprio stream (AD-15, `ClaudeSessionDriver.cs`), e servidores MCP stdio por
  sessão para Brain e planilhas (AD-55/56, `AgentToolServer.cs`). O nosso é mais simples porque o Worker é dono
  do processo; o deles existe porque o processo roda em outro computador.

## Skills

- Formato Agent Skills (`SKILL.md` com `name`, `description`); tabelas `skills`, `skill_files`, `agent_skills`
  com versão **[C][D]** ADR 0020. **[O]** cadastro manual devolveu `version: 1`, `source: manual`.
- Importação por zip (limites antes de inflar, sem path traversal) ou link GitHub (arquivo zip do repositório,
  lista várias skills para escolher); scripts são **ignorados** (sem execução) **[D]**.
- Agente carrega sob demanda (`load_skill`) **[O]**; agentes podem propor melhorias e aprender skills de
  procedimentos repetidos, com escolha entre a versão do link e a dos agentes **[D]** ADR 0020 emendas, 0120.

## APIs

- API privada da web em `/api/app/*`, com OpenAPI em `/api/openapi.json` (570 caminhos na v0.1.2) **[O]**.
- API de operador separada (`OPERATOR_API_PORT`, bearer), só fatos, nunca conteúdo **[D]** ADR 0056.
- Webhooks de entrada para rotinas (`POST /api/hooks/{token}`, assinado) **[D]** ADR 0106.
- GitHub App como identidade do workspace; proxy Git para agentes **[D]** ADR 0024/0033/0156.

## Computadores (runner)

- Computador = ciclo de vida + canal; o canal é o runner via WebSocket de saída; provedores de VM entram como
  adaptador de ciclo de vida **[D]** ADR 0025.
- Instalação Linux: script do próprio servidor, como root, com `--system` (usuário por agente) **[D]** SELF_HOSTING.
  **Não instalado no laboratório**: exigiria root, serviço systemd global e Chrome/Xvnc no host compartilhado.
- Arquivos voltam ao servidor por URL de upload de uso único; o modelo nunca vê os bytes (`pushFile`) **[D]**.

## O que importa para o D.A.N.T.E. [R]

1. Conectores em duas camadas (conexão global + subconjunto por agente) é o modelo certo quando o D.A.N.T.E. tiver
   cadastro de agentes; hoje cada sessão recebe um conjunto fixo de servidores (Brain, planilhas).
2. Catálogo próprio que mapeia ferramenta → escopo → sensibilidade, e **nega o desconhecido**.
3. Expor as ferramentas de coordenação (`delegar`, `consultar`, `pedir aprovação`) por um servidor MCP do próprio
   D.A.N.T.E., no mesmo padrão do `dante_brain` (AD-56): é o caminho de menor atrito para Claude Code e Codex.
4. Skills: o Claude Code já carrega skills do repositório; um registro de skills da plataforma só vale quando houver
   agentes sem repositório. Não executar scripts de skills importadas.
5. Integração com o agentes.chat, se desejada, só por API pública ou MCP, como cliente (categoria D).
