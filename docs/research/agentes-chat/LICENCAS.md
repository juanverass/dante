# Licenças e restrições de reutilização

> Levantamento técnico, não parecer jurídico. Qualquer reutilização direta de código, asset ou texto do
> agentes.chat exige avaliação jurídica antes.

## Licença principal

- **AGPL-3.0-only** (SPDX) para todo o repositório, salvo pacote com LICENSE próprio **[C]** `LICENSE`, `NOTICE`,
  `package.json` de cada app/pacote (todos `AGPL-3.0-only`).
- **Termo adicional 7(b), atribuição**: toda cópia ou versão modificada com interface interativa deve manter,
  acessível nos menus/Sobre, "Based on agentes.chat by AGENTESCHAT, LLC (https://agentes.chat)" **[C]** `NOTICE`.
- **Permissão adicional (seção 7)**: distribuir os apps móvel/desktop por lojas de aplicativos **[C]** `NOTICE`.
- **Marcas (7e)**: "agentes", "agentes.chat", logo, símbolo e ícones não são licenciados; fork precisa de nome,
  logo e identidade próprios **[C]** `NOTICE`, `TRADEMARKS.md`.
- **Licença comercial** (dual licensing) para quem modifica sem publicar, revende com marca própria sem atribuição,
  embute em produto fechado ou não pode usar AGPL **[C]** `COMMERCIAL.md`. Possível porque contribuições externas
  entram sob CLA (baseado no ICLA da Apache) **[C]** `CLA.md`, ADR 0180.

## Por pacote

| Pacote | Licença |
| --- | --- |
| `apps/{server,web,mobile,desktop}`, `packages/{engine,db,providers,adapters,client,ui,i18n,config,modules,tokens}` | AGPL-3.0-only |
| `packages/setup` (`@agenteschat/setup`) | **MIT** (instalador interativo; sem valor arquitetural para nós) |

## Terceiros (do `NOTICE`) **[C]**

- Bun (MIT) com JavaScriptCore/WebCore (LGPL-2.0+) e tinycc (LGPL-2.1) no runner do desktop;
- agent-browser (Apache-2.0) com axe-core (MPL-2.0);
- fontes Doto, Funnel Display, Source Sans 3, Onest, Martian Mono (OFL 1.1);
- template NSIS do Tauri (Apache-2.0 OR MIT);
- ícones Simple Icons (CC0) e marcas de terceiros (Claude, OpenAI, Gemini, PostgreSQL, Tailscale…) usadas só
  nominativamente;
- modelos de fala Parakeet (CC BY 4.0) e Whisper tiny (MIT) na imagem `speech`;
- dependências npm/crates com política automática: permitidas MIT, ISC, Apache-2.0, BSD, 0BSD, MIT-0, Unlicense,
  BlueOak, LGPL-*; negadas GPL/AGPL, SSPL, BUSL, Elastic, Commons-Clause, não comerciais **[C]**
  `scripts/check-licenses.ts`. Servidor MCP: `@modelcontextprotocol/server` (Apache-2.0) **[D]** ADR 0119.

## Implicações para o D.A.N.T.E.

| Pergunta | Resposta |
| --- | --- |
| Podemos copiar código, classes, funções, prompts ou textos? | **Não.** Qualquer trecho AGPL incorporado tornaria o D.A.N.T.E. obra derivada, com obrigação de publicar fonte para usuários de rede e manter a atribuição 7(b). Incompatível com a intenção de comercialização. |
| Podemos estudar e reimplementar conceitos? | Sim. Ideias, arquiteturas, fluxos e comportamentos não são protegidos por copyright; a implementação em C#/.NET é nossa. Evitar transcrever estrutura literal (nomes de arquivos/funções, texto de prompts). |
| O prompt de sistema capturado no laboratório? | É texto deles (AGPL). Fica como evidência fora do repositório (`~/labs/agentes-chat/evidence`), usado só para entender a estrutura; **não** reutilizar o texto. |
| Usar a aplicação self-hosted internamente? | Sim, sem custo e sem obrigações enquanto não modificada; modificada e oferecida a outros pela rede, exige publicar as mudanças. |
| Integrar por API ou MCP? | Chamar a API/MCP de uma instância por rede, como cliente, não cria obra derivada do nosso código **[I]**. Requer confirmação jurídica se um dia for oferecido como funcionalidade comercial do D.A.N.T.E. |
| Usar a marca? | Não, nem em nomes de pacote, domínio ou UI. Citação nominativa em documentação técnica é permitida (`TRADEMARKS.md`, "Without asking"). |
| Algum componente vale incorporar diretamente? | Nenhum identificado. Os equivalentes .NET existem: SDK MCP oficial em C#, Npgsql/EF Core, `SKIP LOCKED` em SQL próprio. |

## Classificação F (atenção jurídica)

Itens que só seriam viáveis com reuso de código ou asset e, portanto, ficam fora:

- prompts e textos de sistema do concorrente;
- design system, ícones, retratos pixelados, sons sintetizados;
- protocolo do runner (formato de mensagens) copiado literalmente;
- catálogo de conectores (mapa ferramenta → escopo) como dado: é obra deles; montar o nosso.
