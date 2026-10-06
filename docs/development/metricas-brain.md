# Métricas de continuidade do Brain (#148)

Medem se o Brain reduz repetição e contexto enviado sem piorar a continuidade. São
locais, só de acréscimo, e nunca guardam conteúdo: apenas contagens, estimativas,
resultado do turno, agente e IDs de tenant/usuário/espaço/projeto/sessão.

Arquivo: `~/.dante/brain/metricas.jsonl` (0600 no Unix), ou `DANTE_BRAIN_METRICS_FILE`.
Fica fora do PostgreSQL canônico, do backup e da exportação do Brain. Apagar o arquivo
zera a medição sem afetar conhecimento.

## O que é registrado

| Registro | Quando | Campos |
| --- | --- | --- |
| `envio` | mensagem comum aceita por uma sessão com escopo Brain, com ou sem itens | bootstrap, candidatos recuperados, selecionados, injetados, descartados, tokens/caracteres do pacote, tokens do snapshot, tokens do pedido sem o pacote, conhecimentos e caracteres armazenados no escopo |
| `turno` | fim do turno aberto por um envio | resultado, tokens estimados da resposta, entrada/saída/cache informados pela CLI |
| `avaliacao` | `avalie a retomada: ...` | repetições, esclarecimentos, concluída, contexto adicional, incorretos/obsoletos, irrelevantes, relevantes |

O turno é ligado ao envio que o abriu por uma correlação que acompanha a mensagem pela
fila da sessão, e herda o escopo daquele envio: trocar de espaço/projeto com mensagem
enfileirada não move a medida do turno em andamento. Turnos sem envio do Brain (`/steer`,
`/vitrine`, sem escopo) não são medidos.

Tokens estimados usam `ceil(bytes UTF-8 / 3)`, o mesmo critério do Context Builder; não
são o tokenizer dos agentes. Uso informado pela CLI fica como está: Claude soma entrada
nova, escrita e lida de cache (`result.usage`); Codex usa `tokenUsage.last` do turno, em
que a entrada inclui o cache e é dominada pelo prompt fixo da CLI. Sem dado, o resumo diz
"indisponível"; campo de avaliação omitido aparece como `?`, nunca como zero.

## Baseline, retomada e indicação

- **Histórico bruto de referência** de uma sessão: tokens dos pedidos do usuário (sem o
  pacote) mais tokens das respostas. É o que precisaria ser reenviado para continuar sem
  o Brain.
- **Retomada**: sessão cujo primeiro envio injetou itens e que tem sessões anteriores
  medidas no mesmo escopo. A referência é a soma do histórico bruto dessas sessões; o
  custo do Brain é a soma dos pacotes da retomada (bootstrap e refreshes).
- **Indicação**, sobre a soma das retomadas, com limiares fixos: Brain/histórico ≤ 0,50
  é **ganho**; ≤ 1,00, **neutralidade**; acima, **regressão**. Sem retomada, **dados
  insuficientes**. Não é decisão go/no-go: a #147 decide com estes dados e os alertas de
  qualidade.

Alertas de qualidade vêm das avaliações: retomadas sem avaliação, informações essenciais
repetidas, esclarecimentos por contexto ausente, tarefas não concluídas, necessidade de
contexto adicional e itens incorretos/obsoletos injetados. Precisão avaliada =
relevantes / (relevantes + irrelevantes + incorretos).

## Uso

```text
métricas do Brain
avalie a retomada: repetições: 0; esclarecimentos: 0; concluída: sim; contexto adicional: não; incorretos: 0; irrelevantes: 1; relevantes: 3
```

A avaliação vale para a sessão ativa do chat ou, sem ela, para a última sessão medida no
escopo. Campos separados por `;`, `,` ou linha; valores numéricos ou `sim`/`não`.
