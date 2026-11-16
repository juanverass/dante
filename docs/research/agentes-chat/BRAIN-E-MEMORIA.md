# Memória e conhecimento

## Modelo do concorrente

- **Uma tabela para contexto derivado e memória**: `context_items` com tipos `thread_summary`, `period_summary`,
  `decision`, `fact`, `preference`, `open_question`, `entity_profile`, `note`, `lesson`; itens nunca sobrescritos
  (nova versão `superseded_by`), só pessoas apagam; cada item com `source_ids`, visibilidade por canal, confiança e
  evidência por fonte **[D]** ADR 0017.
- **Captura**: `context.reflect` quando a thread cresce `REFLECT_EVERY_MESSAGES` (20) ou fica ociosa 120 min; o
  modelo de resumo devolve itens citando referências; **código** decide visibilidade, confiança e evidência
  **[D][O]** (config `REFLECT_*` no log de boot).
- **Consolidação**: inline (mesmo assunto + Jaccard de palavras ≥ 0,8 funde; afirmação diferente substitui com
  histórico) e diária (decaimento ×0,95 após 7 dias, arquivamento abaixo de 0,1 após 30 dias) **[D]**.
- **Aprendizado**: aprovação recusada vira lição verificada; correção de pessoa vira lição; 👍/👎 ajusta relevância;
  no fim do turno o juiz pergunta `learn` e enfileira `context.learn` sem custo extra para turnos comuns
  **[D]** ADR 0017, 0120.
- **"Memória que se prova"**: confiança vem da fonte de cada item, não da run; regra salva de pessoa é citação
  literal; uso contado quando usado, não quando enviado; memória de time em vez de cópias por agente **[D]** ADR 0167.
- **Busca**: `search_documents` (mensagens, itens, issues, arquivos) com tsvector `simple` + vetor opcional
  (1024 dims, HNSW), fusão RRF + bônus de recência, corte por distância 0,75 **[D]** ADR 0017/0167.
- **Ferramentas do agente** **[O]**: `remember`, `update_memory`, `forget`, `save_how_to`, `search_context`,
  `expand`, `get_summary`, `read_wiki_page`, `search_wiki`.
- **Wiki**: páginas com histórico, `[[links]]`, backlinks, grafo, importação Obsidian; agentes leem e escrevem só
  quando pedido **[D]** README, ADR 0023.
- **No laboratório** `ai-status.reflection = false` (sem modelo de resumo): a reflexão não rodou **[O]**.

## Comparação com a Braúna (Brain do D.A.N.T.E.)

| Aspecto | agentes.chat | D.A.N.T.E. (Braúna) |
| --- | --- | --- |
| Limite | workspace + visibilidade por canal | `EspacoDeConhecimento` com proprietário fixo, projeto preso ao espaço (AD-40/42) |
| Unidade | `context_items` tipados | conhecimento canônico com evidência e revisões (AD-44), natureza/tipo semântico separado de autoria e estado epistêmico (AD-59) |
| Fonte | `source_ids` de mensagens/runs | `DocumentoFonte` rastreável, evidências do agente como fonte (AD-60), proveniência por trecho (AD-64) |
| Captura | reflexão automática por modelo + ferramentas | captura em candidato com confirmação no Telegram (#157, AD-61) |
| Consolidação | Jaccard + juízo Jev | consolidação com intenção de vínculo (AD-61); dedup em revisão (#290) |
| Busca | híbrida RRF | híbrida com corte de relevância calibrado, fail-closed (AD-62) |
| Contexto no prompt | bloco de memória por run, prefixo estável | Context Builder/pacote com legenda e omissão por chave (AD-46/64/65) |
| Acesso dos agentes | ferramentas embutidas | servidor MCP `dante_brain` por IPC de sessão (AD-56) |
| Sensibilidade | confiança por fonte, `untrustedBlock` | política de sensibilidade fail-closed, segredo fora de índice (AD-33/45) |

**[I]** A Braúna é mais rigorosa em proveniência, sensibilidade e confirmação humana; o concorrente é mais
agressivo em aprendizado automático e mais integrado ao fluxo de chat (cada correção, recusa e reação alimenta a
memória). Nenhum dos dois coloca a memória no papel de coordenador.

## O que vale aprender [R]

1. **Sinais baratos de aprendizado** já disponíveis no fluxo: aprovação recusada com motivo e correção explícita
   do usuário são as fontes de lição de maior qualidade. Para o D.A.N.T.E., virar **candidato** na Braúna (com a
   confirmação que já existe), nunca conhecimento confirmado automaticamente.
2. **Memória do time vs. do agente**: quando houver vários agentes, decidir se um conhecimento é do espaço (todos)
   ou de um agente — a Braúna já tem o espaço como limite; falta o recorte por agente registrado, se necessário.
3. **"Trabalho repassado aqui"** (lista curta do que o agente delegou e o estado de cada item) é contexto de
   **execução**, não conhecimento: deve vir do ledger de execuções, não da Braúna.
4. Não priorizar wiki, reflexão automática ou embeddings novos: a Epic #296 pede foco em execução multiagente, e o
   Brain já tem fundação suficiente.
5. A #290 (substring ≠ equivalência) confirma uma lição que o concorrente também registrou: deduplicação automática
   precisa de critério conservador; o deles usa Jaccard alto **e** mesmo assunto, e mantém histórico ao substituir.
