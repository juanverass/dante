# Troca de modo na mesma sessão — Issue #108

Investigação em 2026-10-02, antes de implementar o comportamento. CLIs instaladas:
`Claude Code 2.1.287` e `codex-cli 0.159.3`. Workspaces descartáveis fora dos repositórios;
sem acesso irrestrito, sem edição de preferências globais, sem reinício entre modos.

## Operações oficiais e evidência real

- Claude: o [SDK oficial](https://code.claude.com/docs/en/agent-sdk/python#claudesdkclient)
  define `set_permission_mode` para a sessão corrente. No stream-json, enviamos
  `control_request` com `request: {subtype: "set_permission_mode", mode: ...}`.
  A CLI confirmou `auto`, `plan` e `default` (normalização de `manual`) em
  `control_response.response.response.mode`. Um modo inválido retornou erro
  `invalid_mode`, sem sucesso simulado. Não usamos `permission_suggestions/setMode`
  de um approval como prova de suporte independente.
- Codex: o [app-server oficial](https://developers.openai.com/codex/app-server)
  aceita overrides em `turn/start`. O schema gerado pela CLI instalada confirmou
  os campos de aprovação, revisor, sandbox e colaboração; em seguida os testamos
  **na CLI real**, incluindo confirmação `thread/settings/updated`. O evento
  relata a configuração efetiva, inclusive modelo, esforço e cwd.
- Tentativa real de `thread/resume` na thread efêmera: retornou `-32600`,
  `no rollout found for thread id ...`, para manual, auto e plan. Não implementamos
  resume, persistência nova, fork ou reinício para contornar isso.

| Aspecto | Claude | Codex |
| --- | --- | --- |
| Momento suportado pelo D.A.N.T.E. | sessão ociosa, após resposta do controle | próximo turno, com confirmação das configurações |
| Aprovação | `manual`/`default`, `auto`, `plan` | `on-request` em todos; revisor `user` ou `auto_review` |
| Sandbox | controle não modifica o sandbox/allowed tools iniciais | `workspaceWrite`, rede false, roots extras vazias; plan `readOnly`, rede false |
| Planejamento | modo `plan`; saída explícita via controle | colaboração `plan`; saída explícita `default` |
| Identidade/histórico | mesmo processo e session id | mesmo processo e thread id efêmera |
| Cwd/modelo/esforço | controle contém só modo | overrides não contêm cwd/modelo; colaboração reutiliza modelo e esforço existentes |
| Requests/turno ativo | recusados antes de enviar controle | recusados antes de agendar override |
| Modo aplicado | campo `mode` confirmado | evento com política, revisor, sandbox, colaboração, cwd, modelo e esforço compatíveis |

O probe Codex real fez `auto → manual → plan → manual` na thread
`01a0fd5f-ac93-7462-b1e3-8651115b8506`, modelo `gpt-6.1-sol`: todos os turnos terminaram
`completed` e lembraram `MODE108`, que existia apenas na conversa. As notificações
confirmaram `auto_review → user`, `workspaceWrite → readOnly → workspaceWrite` e
`default → plan → default`. Isso prova aplicação upstream e continuidade, além do schema.

## Semântica escolhida

`/mode <modo>` e `/permissions <modo>` continuam alterando só o padrão persistido.
`/mode session <modo>` altera só a sessão ativa do dono. Claude confirma na operação;
Codex registra uma intenção runtime, sem mudar `Profile`, e a aplica na próxima mensagem.
`PendingProfile` é visível e nunca é apresentado como política já aplicada. Pedir o modo
atual cancela a intenção pendente. Agente sem capacidade explica como abrir nova sessão.

Não habilitamos mudanças no meio do turno: não há evidência suficiente de atomicidade
com approvals/input já pendentes. Recusamos todas essas situações sem invalidar requests.
Também recusamos fila não vazia. Elevação de manual/plan para auto requer o comando
explícito do dono; não surge de mensagem do agente nem inferência. Nenhum modo usa
`danger-full-access` ou `bypassPermissions`.

Recusa explícita upstream mantém sessão/perfil anterior. Após envio, uma confirmação
incompatível, ausente ou incerta não permite continuar anunciando o modo antigo com
processo potencialmente modificado: a sessão é encerrada com erro e exige abertura
explícita. Falta de `thread/settings/updated` em CLI antiga falha assim, sem alegar sucesso.
A troca não resolve uma ação já negada: o usuário pode repetir o pedido no próximo turno.

## Reprodução e cobertura

```bash
codex --version
claude --version
codex app-server generate-ts --out /tmp/dante-protocol
DANTE_LIVE_CLI=1 dotnet test Dante.sln --filter FullyQualifiedName~LiveSessionModeEvidenceTests
```

O teste opt-in usa os drivers de produção, esforço `high`, um token só no histórico e
um único processo por agente. Percorre `manual → auto → manual → plan → auto → plan → manual`,
as seis transições dirigidas, exigindo conclusão e lembrança do token em cada turno.
No Codex exige também `ModeAppliedEvent`, emitido somente após confirmação das políticas.

Testes sem CLIs reais cobrem políticas recebidas pelo processo simulado, recusa e
confirmação incompatível/ausente, snapshot antes/depois da confirmação, serialização com
turno novo, dono, capacidade ausente, turno ativo, approval e input pendentes, cancelamento
de intenção e separação entre preferência persistida e modo runtime no Telegram.

Execução em 2026-10-02: os dois testes reais passaram (Claude 13 s, Codex 21 s),
com esforço `high`, via drivers de produção; as seis transições preservaram o token
em ambos os agentes e o Codex confirmou todas as configurações esperadas.
