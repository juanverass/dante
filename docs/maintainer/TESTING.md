# Estratégia de testes do D.A.N.T.E.

O projeto usa os testes como especificação executável. Para compreender uma regra, normalmente vale ler:

1. classe de produção;
2. teste correspondente;
3. decisão arquitetural associada.

No snapshot desta documentação, a suíte registrada após #81 possui **360 testes aprovados**.

---

# 1. Estrutura

```text
tests/
├── Dante.Tests/
│   └── testes xUnit do sistema
└── Dante.ProcessProbe/
    └── executáveis simulados usados por testes de processo/protocolo
```

---

# 2. Por que existe `Dante.ProcessProbe`?

Vários comportamentos importantes não podem ser testados adequadamente apenas mockando métodos.

O D.A.N.T.E. precisa verificar coisas como:

- stdin real;
- stdout real;
- stderr real;
- processo filho;
- subprocessos;
- cancelamento;
- EOF;
- framing JSONL;
- protocolo Claude;
- protocolo Codex.

Por isso existe um projeto auxiliar que se comporta como CLIs simuladas.

```text
Dante.Tests
    ↓
inicia processo real
    ↓
Dante.ProcessProbe
    ├─ FakeClaude
    └─ FakeCodex
```

Assim a infraestrutura de processo é real, mas não depende de:

- autenticação;
- rede do modelo;
- cobrança;
- disponibilidade do Claude/Codex reais.

---

# 3. Pirâmide prática do projeto

A suíte mistura três níveis.

## Unitários

Testam lógica isolada.

Exemplos:

- `AgentSessionTests`;
- `JobRegistryTests`;
- `RepositoryRegistryTests`;
- `AssistantSettingsStoreTests`;
- `TelegramMessageFormatterTests`.

## Integração interna

Vários componentes reais com fakes nas bordas.

Exemplos:

- `TelegramPlainMessageTests`;
- `TelegramDeliveryServiceTests`;
- `SessionRegistryTests`.

## Integração de processo/protocolo

Processos reais com ProcessProbe.

Exemplos:

- `InteractiveAgentProcessTests`;
- `ClaudeSessionDriverTests`;
- `CodexSessionDriverTests`;
- `InteractiveSessionEndToEndTests`.

---

# 4. Mapa de testes por módulo

## Agents

### `AgentProcessExecutorTests`

Cobrem o executor one-shot:

- sucesso;
- stderr;
- exit code;
- cancelamento;
- processo;
- árvore.

### `ClaudeRunnerTests`

Verificam argumentos e contrato do runner one-shot Claude.

### `CodexRunnerTests`

Equivalente para Codex.

### `InteractiveAgentProcessTests`

Testam:

- stdin;
- stdout/stderr incremental;
- serialização;
- close;
- kill;
- cancellation;
- descendants;
- ausência de processos órfãos.

É uma das melhores classes para entender a infraestrutura interativa.

### `AgentModelCatalogTests`

Cobrem:

- descoberta dinâmica;
- aliases/model IDs;
- effort levels;
- cache;
- falha de protocolo.

---

# 5. Sessions

## `AgentSessionTests`

É a especificação da máquina de estados.

Leia para entender:

- submit idle;
- queue;
- steer;
- interrupt;
- approval;
- input;
- expiration;
- close;
- failure.

Se você alterar uma regra de conversa, esse é provavelmente o primeiro arquivo de testes que precisa mudar.

---

## `SessionRegistryTests`

Especifica a orquestração ao redor da máquina de estados.

Coberturas importantes:

- ownership;
- sessão ativa;
- driver;
- pump;
- fila;
- requests;
- expiração;
- concorrência;
- failure;
- shutdown.

---

## `ClaudeSessionDriverTests`

Usam o protocolo simulado do Claude.

Cobrem tradução:

```text
stream-json ⇄ IAgentSessionDriver / AgentEvent
```

São úteis quando a versão do Claude Code muda.

---

## `CodexSessionDriverTests`

Fazem o mesmo para:

```text
app-server JSON-RPC
```

São o primeiro lugar para reproduzir mudança de protocolo do Codex.

---

# 6. Telegram

## `TelegramPollingServiceTests`

Cobrem o loop e comportamentos básicos de polling.

---

## `TelegramBotApiTests`

Testam o contrato HTTP gerado.

Exemplos:

- body;
- allowed updates;
- callback;
- markup;
- rate limit.

Não usam a Bot API real.

---

## `TelegramUserAuthorizerTests`

Especificam a allowlist fail-closed.

---

## `TelegramAgentRoutingTests`

Testam resolução entre agentes/contextos no caminho de comandos.

---

## `TelegramPlainMessageTests`

São centrais para a UX session-first.

Cobrem mensagens sem slash command e a integração com sessões.

---

## `TelegramActiveRepositoryTests`

Cobrem:

- `/use`;
- repo ativo;
- stale aliases;
- precedência.

---

## `TelegramAgentCommandTests`

Cobrem comandos relacionados à escolha do agente.

---

## `TelegramModeCommandTests`

Cobrem modos operacionais.

---

## `TelegramModelCommandTests`

Cobrem seleção de modelo e esforço.

---

## `TelegramJobCommandTests`

Cobrem jobs one-shot, status e cancelamento.

---

## `TelegramRepositoryCommandTests`

Cobrem cadastro e ambiente dos repositórios.

---

# 7. Delivery

## `TelegramDeliveryServiceTests`

É uma classe extensa porque delivery possui várias responsabilidades difíceis:

- batching;
- ordem;
- redaction;
- retenção;
- retry;
- falha;
- streaming;
- prefixos;
- secrets.

Leia junto com `TelegramDeliveryService`.

---

## `TelegramFormattedDeliveryTests`

Focados em:

- HTML;
- code blocks;
- fallback plain;
- comandos multiline;
- ordem;
- resend.

---

## `TelegramMessageFormatterTests`

Focados no formatter puro:

- fences;
- languages;
- escaping;
- split;
- Unicode;
- HTML válido.

---

## `TelegramInlineApprovalTests`

Cobrem:

- inline keyboard;
- decisões;
- duplicidade;
- owner;
- session/turn/request;
- expiration;
- corrida entre send e expiry;
- fallback textual.

---

# 8. End-to-end simulado

## `InteractiveSessionEndToEndTests`

É provavelmente o teste mais útil para enxergar o sistema inteiro.

Caminho:

```text
Telegram fake
   ↓
TelegramPollingService
   ↓
SessionRegistry
   ↓
driver real
   ↓
InteractiveAgentProcess real
   ↓
Dante.ProcessProbe
   ↓
eventos
   ↓
TelegramDeliveryService
   ↓
Telegram fake
```

Ele valida integração sem precisar acessar Telegram real ou modelos reais.

---

# 9. O que a suíte NÃO prova

Mesmo com todos os testes passando, ainda podem existir falhas em:

- versão nova da Bot API;
- versão nova de Claude Code;
- versão nova do Codex;
- autenticação local;
- PATH;
- WSL;
- permissões do sistema;
- rede real;
- rate limits reais;
- comportamento não documentado das CLIs.

Por isso há diferença entre:

```text
testes automatizados
        e
dogfooding real
```

---

# 10. Comandos

Toda suíte:

```bash
dotnet test Dante.sln
```

Build:

```bash
dotnet build Dante.sln
```

Uma classe:

```bash
dotnet test Dante.sln --filter FullyQualifiedName~AgentSessionTests
```

Um teste:

```bash
dotnet test Dante.sln --filter FullyQualifiedName~NomeDoTeste
```

Mais detalhes:

```bash
dotnet test Dante.sln --logger "console;verbosity=detailed"
```

---

# 11. Como depurar teste de concorrência

Evite concluir que “é flaky” imediatamente.

Pergunte:

1. há um race legítimo?
2. existe `Task.Delay` usado como sincronização?
3. o teste deveria aguardar uma condição?
4. há Channel/Semaphore/lock envolvido?
5. o processo filho realmente encerrou?

A suíte possui helpers do tipo `Eventually` em cenários assíncronos para esperar estado observável.

---

# 12. O teste de processo órfão

Há um cenário conhecido historicamente sensível em:

`InteractiveAgentProcessTests.GracefulExitDoesNotLeaveOrphanedChildProcess`

Ele existe porque shutdown de árvore de processos é dependente de timing do SO.

Ao mexer em:

- `InteractiveAgentProcess`;
- `ProcessTree`;
- close/kill;

rode esse grupo repetidamente.

---

# 13. Como adicionar teste para uma feature

## Regra de estado

Adicione próximo à máquina de estados.

Exemplo:

```text
AgentSessionTests
```

## Regra de Registry

```text
SessionRegistryTests
```

## Protocolo específico

```text
ClaudeSessionDriverTests
ou
CodexSessionDriverTests
```

## Telegram UX

Teste o handler/comando específico.

## Delivery/formatação

Teste no Delivery e/ou Formatter.

## Mudança transversal

Adicione também um cenário end-to-end simulado.

---

# 14. Como ler a suíte para aprender o sistema

Ordem recomendada:

1. `JobRegistryTests`
2. `RepositoryRegistryTests`
3. `AgentSessionTests`
4. `SessionRegistryTests`
5. `InteractiveAgentProcessTests`
6. `ClaudeSessionDriverTests`
7. `CodexSessionDriverTests`
8. `TelegramPlainMessageTests`
9. `TelegramDeliveryServiceTests`
10. `InteractiveSessionEndToEndTests`

Essa sequência acompanha a arquitetura do núcleo para as bordas.

---

# 15. Definition of Done técnica recomendada

Para alteração de código:

```text
dotnet build Dante.sln
dotnet test Dante.sln
git diff --check
```

Além disso:

- teste novo para regra nova;
- teste de regressão para bug;
- documentação/AD atualizada se houver decisão arquitetural;
- nenhum secret em fixtures/logs;
- diff revisado;
- comportamento real testado quando depende de CLI/Bot API real.
