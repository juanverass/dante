# Planilhas: Google Sheets como ferramenta genérica (#224, AD-55)

O D.A.N.T.E. oferece aos agentes uma capacidade **genérica** de planilhas: ler, localizar,
interpretar e editar qualquer planilha que o usuário cadastrou, sem conhecer o domínio dos
dados (finanças, treino, faculdade...). Google Sheets é o primeiro provedor.

```text
Domínio do usuário ──► Claude / Codex (interpreta intenção e estrutura)
                            │ ferramentas MCP dante_planilhas
                            ▼
                  PlanilhasAppService (contratos genéricos)
                            │ IPlanilhaService / IConexaoDePlanilha
                            ▼
              GoogleSheetsAdapter + GoogleOAuthService ──► Google Sheets API v4
```

O adapter conhece planilhas, abas, intervalos A1, células, fórmulas, mesclagens e
metadados. Entender "a linha da AWS" ou "o item da segunda-feira" é do agente, a partir da
estrutura realmente lida. Nenhum tipo ou regra de domínio existe no núcleo, no adapter ou no
servidor MCP; `PlanilhaDeTreinoCenarioTests.IntegracaoNaoConheceConceitosDeDominio` recusa
esse vocabulário no código de produção.

## Preparar o cliente OAuth (uma vez)

1. No [Google Cloud Console](https://console.cloud.google.com/), crie um projeto e ative a
   **Google Sheets API**.
2. Em *Google Auth Platform*, configure a tela de consentimento (tipo **Externo**) e adicione
   o escopo `https://www.googleapis.com/auth/spreadsheets`. Enquanto o app estiver em
   **Teste**, adicione sua conta como usuário de teste — e saiba que, nesse estado, o Google
   expira o refresh token em 7 dias para esse escopo. Para não reconectar toda semana,
   publique o app (status **Em produção**); para uso pessoal, a tela "app não verificado"
   continua aparecendo no consentimento e pode ser aceita.
3. Crie um **ID do cliente OAuth** do tipo **App para computador**.
4. Defina no ambiente do serviço (por exemplo `~/.config/dante/dante.env`), nunca no
   repositório:

   ```bash
   Google__ClientId=<id>.apps.googleusercontent.com
   Google__ClientSecret=<segredo do cliente>
   # opcional: porta fixa do callback local (padrão: porta livre escolhida a cada conexão)
   # Google__CallbackPort=8765
   ```

5. Reinicie o D.A.N.T.E.

## Conectar, cadastrar e usar

```text
/google connect          link de autorização; abra no navegador DESTE computador
/google status           conta conectada (e-mail), sem token
/google disconnect       revoga no Google e apaga a credencial local
/planilha add financas https://docs.google.com/spreadsheets/d/<id>/edit gastos da casa
/planilha show financas  abas, área usada e mesclagens
/planilha remove financas
/planilhas               conexão e planilhas cadastradas
```

`/google connect` abre um callback em `127.0.0.1:<porta>` no WSL, com PKCE S256 e `state`
aleatório; o WSL2 encaminha o `localhost` do Windows, então o link funciona no navegador do
Windows (não no celular). O resultado chega como nova mensagem. Uma conexão nova invalida o
link anterior; o link expira em 5 minutos.

Depois, converse normalmente — "leia a aba Financeiro e me diga quanto gastei em setembro",
"atualize o valor da internet para 119,90", "adicione uma linha nessa tabela". Sessões
iniciadas **com a conta conectada** recebem o servidor MCP `dante_planilhas`; sessões já
abertas antes de conectar precisam ser reiniciadas (`/session stop` e nova mensagem).

## Ferramentas do agente

| Ferramenta | Tipo | O que faz |
| --- | --- | --- |
| `listar_planilhas` | leitura | conexão e cadastro (descrições e regiões anotadas) |
| `descrever_planilha` | leitura | título, localidade, abas, grade, área usada (a partir de A1) e mesclagens |
| `ler_intervalo` | leitura | retângulo A1 com aba (ou região anotada): só células com conteúdo, com endereço, exibido, bruto, fórmula e mesclagem; 500 células por padrão, 5000 no máximo |
| `buscar_na_planilha` | leitura | texto nos valores exibidos, sem diferenciar caixa/acentos; exatas primeiro; devolve a linha de contexto |
| `atualizar_celulas` | escrita | retângulos exatos, tudo ou nada, até 200 células, com `valores_esperados` |
| `atualizar_por_referencia` | escrita | localiza a célula de referência por texto e escreve na mesma linha (coluna ou deslocamento) |
| `adicionar_linha` | escrita | acrescenta após a tabela do intervalo (`values.append`, `OVERWRITE`), sem inserir linhas na grade |
| `cadastrar_planilha`, `anotar_regiao` | cadastro local | alias/URL e regiões recorrentes (nome, intervalo, descrição) |

A representação mantém as coordenadas (`B12 = "25"`, `E5 = "22" (fórmula =D5*1,1)`,
`A1 = "Título" [mesclagem A1:F1]`), para o agente raciocinar sobre o conteúdo e a escrita
final ser determinística. As instruções do servidor orientam o trabalho progressivo:
metadados → abas → região pequena → busca → região relevante → escrita.

Valores de texto são gravados como digitados por um usuário na localidade da planilha
(`"129,90"` vira número em pt_BR; `"=A1*2"` vira fórmula); números e booleanos JSON são
gravados como tais; `null`/`""` limpa a célula. Só valores mudam: formatação e células fora
do alvo ficam intactas.

## Segurança e aprovação

- **Leitura** só em planilha cadastrada, sem aprovação (Claude: `--allowedTools` das
  ferramentas de leitura; Codex: `readOnlyHint`).
- **Escrita** segue o modo da sessão: `manual` pede aprovação pelo Telegram (Claude por
  `can_use_tool`; Codex por `mcpServer/elicitation/request` com `codex_approval_kind =
  mcp_tool_call`), `auto` deixa a decisão ao revisor automático de cada CLI, e `plan` recusa.
  A aprovação mostra a ferramenta e os argumentos.
- **Ambiguidade** nunca é resolvida em silêncio: `atualizar_por_referencia` com mais de um
  candidato do mesmo nível (exatas, ou parciais sem nenhuma exata) não escreve e devolve os
  candidatos; o agente confirma com o usuário.
- **Proteções do núcleo**, que recusam o lote inteiro: valor esperado divergente; célula com
  fórmula sem `permitir_sobrescrever_formulas` (e nunca mais de 5 fórmulas por escrita);
  célula interna de mesclagem; limpar mais de 20 células preenchidas; mais de 200 células.
- **Não suportado no MVP**: excluir linhas/abas, limpar grandes intervalos, mudar estrutura,
  formatação, gráficos, Apps Script e descoberta pelo Google Drive.

## Armazenamento local

| Arquivo | Conteúdo |
| --- | --- |
| `~/.dante/google/credencial.bin` (0600) | cliente OAuth, refresh token e e-mail, cifrados com AES-256-GCM |
| `~/.dante/google/chave` (0600) | chave da credencial; o diretório é 0700 |
| `~/.dante/planilhas/cadastro.json` (0600) | aliases, IDs, descrições e regiões |
| `~/.dante/planilhas/auditoria.jsonl` (0600) | uma linha por célula escrita |

`DANTE_GOOGLE_DIR` e `DANTE_PLANILHAS_DIR` trocam os diretórios. O refresh token nunca fica em
texto puro em `appsettings.json`, `.env`, logs ou Telegram; o access token vive só em memória.
A proteção efetiva é a do usuário do sistema: quem lê os dois arquivos como esse usuário obtém
o token — o que se evita é o segredo legível em claro, em cópias ou buscas.

A auditoria registra instante, conta (e-mail), planilha (ID e alias), aba, operação, endereço,
valor e fórmula anteriores, valor novo, origem (`mcp:telegram:<id>`) e agente — nunca token.

## Processo do servidor MCP

O servidor é o próprio executável do Worker em modo `--mcp-planilhas`, iniciado pela CLI do
agente. Recebe por argumento só a origem e o agente, e por ambiente só caminhos (`HOME`,
`DOTNET_ROOT`, `PATH`, `DANTE_GOOGLE_DIR`, `DANTE_PLANILHAS_DIR`): a configuração MCP aparece
na linha de comando da CLI e por isso nunca carrega segredos. O cliente OAuth vai cifrado
junto da credencial, para que o processo renove tokens sem herdar o ambiente do Worker.
Compõe Application/Infrastructure sem Telegram nem host e escreve apenas JSON-RPC no stdout.

## Validação

Automática: `PlanilhasNucleoTests`, `PlanilhasAppServiceTests`, `GoogleOAuthServiceTests`
(callback loopback real), `GoogleSheetsAdapterTests`, `ServidorMcpDePlanilhasTests`,
`PlanilhaDeTreinoCenarioTests`, `TelegramPlanilhasTests` e os testes de driver/sessão, com o
`GoogleSheetsFalso` emulando a API e os endpoints OAuth.

Real, opt-in, com a conta conectada e a planilha cadastrada:

```bash
DANTE_LIVE_GOOGLE=1 DANTE_LIVE_GOOGLE_PLANILHA=treino \
DANTE_LIVE_GOOGLE_TERMO="texto que existe na planilha" \
DANTE_LIVE_GOOGLE_CELULA="'Aba'!Z99" \
dotnet test tests/Dante.Tests --filter LivePlanilhasEvidenceTests
```

A célula informada recebe um marcador e volta ao valor anterior.
