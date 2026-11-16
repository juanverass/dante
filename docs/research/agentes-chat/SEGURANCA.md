# Segurança: permissões, isolamento, credenciais, aprovações

## Camadas de autorização

| Camada | Mecanismo | Fonte |
| --- | --- | --- |
| Pessoa no workspace | papéis e chaves (`can(membership, key)`), `organization_members`, `org_roles` | `permissions.ts`, `roles.ts`, ADR 0085 **[C]** |
| Acesso a canal | `requireChannelAccess` (HTTP e WebSocket); admin lê canais, nunca DMs | ADR 0151 **[D]** |
| Pessoa ↔ agente | audiência do agente (`canTalk`) | ADR 0085 **[C]** |
| Agente ↔ ferramenta | `agent_scopes` lido a cada chamada | ADR 0015 **[C]** |
| Tipo de run | `tools/run-policy.ts`: o que pessoa, rotina e run sem ninguém podem | ADR 0053 **[C]** |
| Ação sensível | aprovação humana vinculada a hash | ADR 0015/0173/0182 **[C]** |

## Classificação de ação sensível

- Fonte declara `sensitive`; ou nome/escopo contém merge, deploy, rollback (`ALWAYS_SENSITIVE`); conectores têm
  nível `approval`; classificador marca exclusões, envios, pagamentos **[D]** ADR 0015, 0173.
- Ferramentas "abertas" (SQL, GraphQL, proxy, script, workflow, build, release) nunca recebem "aprovar sempre" e
  sempre pedem no nível `never` **[D]** ADR 0173.
- Rotinas e missões sempre pedem; toda escrita em app conectado vinda de rotina pede **[D]** ADR 0053.

## Aprovação

- `approvals`: ferramenta, escopo, conexão, parâmetros, `action_hash = sha256(JSON canônico)`, título,
  `expires_at` (24 h), dono do agente (snapshot) **[C]**.
- Quem decide: dono do agente e admins; `request_permission` (ampliar acesso) só admins **[D]**.
- `executeApproved` reconfere o hash, reconfere o escopo (pode ter sido revogado), marca `executed_at` (nunca duas
  vezes) e executa exatamente os parâmetros guardados **[C]**.
- Recusa/expiração viram resultado de ferramenta com o motivo; recusa vira lição de memória **[D]** ADR 0017.
- Ordem preservada entre runs do mesmo cartão; ação aprovada depois de uma anterior recusada não executa
  **[D]** ADR 0182.
- **[O]** Cartão via API com `why` obrigatório, `approvers`, `canDecide`, `expiresAt`.

## Segredos

- Chaves de provider, conectores, caixas de e-mail e bancos: cifra por envelope com chave derivada de
  `AGENTES_SECRET`; nunca devolvidas ao navegador **[D]** ADR 0007, SELF_HOSTING.
- Pedido de segredo é ferramenta (`request_secret`), com cartão; segredos vão ao computador do agente fim a fim e
  são redigidos da saída **[D]** ADR 0027.
- Contas Claude/Codex: a CLI guarda o login numa pasta no computador; o código colado é cifrado para uma chave do
  computador e só repassado **[D]** ADR 0041/0059.
- Cofre (logins, chaves) com uso conferido a cada acesso; senha selada para uma chave de uso único na memória do
  runner **[D]** ADR 0144.

## Injeção de prompt

- Texto de fora chega ao modelo só por `untrustedBlock` (`untrusted.ts`) **[C]**; o prompt diz que texto de
  pessoas e agentes é informação, não instrução **[O]**.
- Memória guarda confiança por fonte; item vindo de conteúdo externo fica marcado e vira citação, não regra
  **[D]** ADR 0117/0167.
- Identidade e permissões não são lidas do prompt; o gateway decide **[C]**.

## Isolamento no computador do agente

| Nível | Conteúdo | Fonte |
| --- | --- | --- |
| 1 (padrão) | pasta por agente; `GIT_CONFIG_GLOBAL` por agente; commits como o agente + `Co-authored-by`; token GitHub de curta duração pelo credential helper, push só em `agentes/{handle}/*`; `HOME` não muda | ADR 0030 **[D]** |
| 2 (opcional) | sandbox de SO por agente: Seatbelt no macOS, bubblewrap/landlock no Linux | ADR 0030 **[D]** (proposto) |
| "system mode" | um usuário de SO por agente, supervisor root, lançadores | ADR 0057, `CLAUDE.md` deles **[D]** |
| Claude Code | `--strict-mcp-config`, `--setting-sources ""`, `--tools` restrito, `--allowedTools mcp__agentes`, `--permission-prompt-tool mcp__agentes__permission_prompt`; `init` da CLI conferido contra a lista ("breach") | runner `brain.ts`, `mcp.ts` **[C]** |

## Auditoria

`audit_log` com mudanças de pessoas e toda chamada de ferramenta relevante (`tool.call`, com `run_id`,
`channel_id`, `on_behalf_of_user_id`, `outcome`); entradas mascaradas (`auditInput`) **[C][O]**. Comandos que a CLI
roda sem perguntar ficam no computador (`activity.jsonl`) **[D]**.

## Comparação com o D.A.N.T.E.

| Tema | D.A.N.T.E. hoje | Observação |
| --- | --- | --- |
| Autorização de usuário | allowlist fail-closed do Telegram (AD-05) | suficiente para uso pessoal; multiusuário exige papéis |
| Approvals | `control_request can_use_tool` (Claude) e requests do `app-server` (Codex), correlacionados por sessão/turno/request, uma resposta (AD-15/16/22) | equivalente para ações **da CLI**; falta aprovação de ações **do D.A.N.T.E.** (delegar, publicar, merge) persistida e vinculada a hash |
| Perfis | perfis de permissão por sessão (AD-24), `full` não oferecido | o deles é por agente (`always/risky/never`) |
| Segredos | binding por host, nunca no catálogo nem no Telegram (AD-10); refresh token Google cifrado (AD-55) | equivalente em espírito |
| Isolamento | ambiente filtrado (AD-09/10), contenção cgroup v2 por sessão (AD-63) | **mais forte** que o nível 1 deles no Linux; falta identidade Git por agente e restrição de push |
| Injeção | contrato da conversa, Brain com sensibilidade/fail-closed (AD-33/45) | falta marcar resultados de outros agentes como não confiáveis quando houver delegação |
| Auditoria | Brain com auditoria/exportação (#149); jobs/sessões sem trilha persistente | lacuna para execuções |

## Recomendações [R]

1. Aprovação persistida para ações próprias do D.A.N.T.E. (delegar a outro agente com escrita, publicar, merge),
   com hash dos parâmetros, validade, decisor e execução única — no mesmo modelo de dados das execuções.
2. Política por tipo de execução (pessoa, delegação, rotina) num único ponto, consultado pelos drivers ao montar o
   perfil da sessão.
3. Identidade Git por agente (`GIT_CONFIG_GLOBAL` por sessão) e prefixo de branch por agente antes de permitir dois
   agentes escrevendo.
4. Resultado de outro agente entra no contexto do coordenador como dado não confiável, com autor explícito.
5. Manter a AD-63 como diferencial; não copiar o "system mode" deles (usuário de SO por agente) antes de haver
   necessidade multiusuário.
