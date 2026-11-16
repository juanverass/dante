# Fila durável

## Alternativas comparadas

| Alternativa | Prós | Contras | Veredito |
| --- | --- | --- | --- |
| **A. Só memória** (`Channel<T>`) + estado persistido; na inicialização, varrer execuções `Pendente` | Mínima; o Worker é instância única | Sem timers duráveis (expiração de aprovação, backoff); WebApi não consegue enfileirar; ordem e dedupe ad hoc | Insuficiente assim que a WebApi e as aprovações persistidas existirem |
| **B. Lease direto na linha da execução** (`execucoes.lease_ate`) | Uma tabela a menos | Mistura estado de negócio com mecânica; execução esperando aprovação não tem "trabalho"; timers e entregas não cabem | Rejeitada |
| **C. Tabela de trabalhos no PostgreSQL** (outbox transacional, `FOR UPDATE SKIP LOCKED`, lease, dedupe, `disponivel_em`) | Enfileira na mesma transação da mudança de estado; timers e retries duráveis; produtores múltiplos (Worker, WebApi); sem dependência nova | Polling ou `LISTEN/NOTIFY`; exige cuidado para não segurar transação durante a chamada à CLI | **Recomendada** |
| **D. Biblioteca (Hangfire, Quartz) com storage Postgres** | Pronta | Pacote novo e modelo próprio de jobs que não conhece o estado da execução; opaca para auditoria; o contrato pede não adicionar pacotes sem necessidade | Rejeitada |
| **E. Broker (RabbitMQ, Redis, NATS)** | Escala | Novo serviço para operar num host único; dupla escrita banco + broker; vetado pela #296 sem necessidade comprovada | Rejeitada |
| **F. SQLite local para a orquestração** | Sem servidor; não depende do banco do Brain | Segundo motor de banco para operar e fazer backup; sem `SKIP LOCKED` nem `LISTEN/NOTIFY`; escrita concorrente Worker + WebApi limitada; o PostgreSQL já é operado para o Brain | Rejeitada |

## Modelo recomendado (C)

`trabalhos_da_fila`:

| Coluna | Uso |
| --- | --- |
| `id` | Guid |
| `tipo` | `AvancarExecucao`, `ExpirarAprovacao`, `ExpirarPergunta`, `EntregarEvento`, `RetomarExecucao`, `EnviarMensagemParaSessao` (chat web) |
| `id_execucao` | quando aplicável |
| `dados` | JSON pequeno (ids, nunca conteúdo sensível) |
| `chave_de_deduplicacao` | única; ex.: `avancar:E123:etapa3:t1`, `expirar:aprovacao:<id>` |
| `estado` | `pendente`, `reivindicado`, `concluido`, `falhou`, `descartado` |
| `prioridade` | menor primeiro; interativo antes de rotina |
| `disponivel_em` | timers e backoff |
| `tentativas`, `max_tentativas` | retries |
| `trabalhador`, `boot_do_trabalhador` | quem reivindicou (host + PID + Guid do boot) |
| `lease_ate` | expiração do lease |
| `geracao` | *fencing token*: incrementa a cada reivindicação |
| `ultimo_erro` | tipo do erro, sem conteúdo |

### Diagrama

```mermaid
flowchart LR
  subgraph Produtores
    TG[Telegram<br/>casos de uso]
    WA[WebApi<br/>casos de uso]
    EX[Executor<br/>ao concluir etapa]
    AP[Decisão de aprovação]
  end
  subgraph PG[PostgreSQL]
    T[(trabalhos_da_fila)]
    E[(execucoes / etapas)]
  end
  subgraph Worker
    SIG[Sinal em memória<br/>+ LISTEN dante_fila]
    PF[ProcessadorDaFila<br/>N slots]
    HB[Heartbeat 20 s]
    RC[Recuperação na inicialização]
  end
  TG -- mesma transação --> E & T
  WA -- mesma transação --> E & T
  EX -- mesma transação --> E & T
  AP -- mesma transação --> E & T
  T -- NOTIFY após commit --> SIG
  SIG --> PF
  PF -- "reivindica: FOR UPDATE SKIP LOCKED<br/>lease 60 s, geracao+1" --> T
  PF --> HB
  HB -- "renova WHERE trabalhador=eu AND geracao=g" --> T
  RC -- "devolve leases de boots anteriores" --> T
  PF -- "polling 5 s como rede de segurança" --> T
```

### Reivindicação

```sql
WITH proximo AS (
  SELECT id
  FROM orquestracao.trabalhos_da_fila
  WHERE (estado = 'pendente' AND disponivel_em <= now())
     OR (estado = 'reivindicado' AND lease_ate < now())
  ORDER BY prioridade, disponivel_em
  FOR UPDATE SKIP LOCKED
  LIMIT 1)
UPDATE orquestracao.trabalhos_da_fila t
SET estado = 'reivindicado', trabalhador = @trabalhador, boot_do_trabalhador = @boot,
    lease_ate = now() + interval '60 seconds', geracao = t.geracao + 1, tentativas = t.tentativas + 1
FROM proximo
WHERE t.id = proximo.id
RETURNING t.id, t.tipo, t.id_execucao, t.dados, t.geracao;
```

A transação da reivindicação termina aí. **Nenhuma transação fica aberta durante a chamada à CLI** — a lição da
ADR 0163 do concorrente vale aqui.

## Respostas às perguntas do spike

1. **Como uma execução entra na fila?** O caso de uso grava `execucao` (`Pendente`), suas etapas e um trabalho
   `AvancarExecucao` com chave de deduplicação **na mesma transação** (UoW). Após o commit, sinal em memória
   (mesmo processo) e `NOTIFY dante_fila` (outro processo, ex.: WebApi).
2. **Como o Worker reivindica?** `ProcessadorDaFila` (hosted service) com N slots (limite global) executa o SQL
   acima; antes de executar uma etapa, confere os limites por agente/repositório; se não couber, devolve o trabalho
   com `disponivel_em = now() + 2 s` sem contar tentativa.
3. **Como evitar dois Workers na mesma tarefa?** `SKIP LOCKED` na reivindicação; lease com `geracao`; toda gravação
   de resultado faz CAS em `estado` + `tentativa` da etapa e em `geracao` do trabalho. Um processo que perdeu o
   lease não consegue confirmar nada. Na prática há um Worker por host (systemd), mas a regra também protege
   sobreposição em reinício manual, testes e a WebApi.
4. **Como identificar execução abandonada?** Trabalho `reivindicado` com `lease_ate < now()`; ou, na inicialização,
   reivindicado por `boot_do_trabalhador` diferente do atual no mesmo host — devolvido imediatamente.
5. **Como renovar um lease?** Heartbeat a cada 20 s enquanto a etapa roda:
   `UPDATE … SET lease_ate = now() + 60 s WHERE id = @id AND trabalhador = @eu AND geracao = @g`. Zero linhas →
   lease perdido → cancelar a etapa localmente (o token interrompe o turno/processo) e não gravar resultado.
6. **Processo morre durante a CLI?** Ver [PERSISTENCE_AND_EXECUTIONS](PERSISTENCE_AND_EXECUTIONS.md#o-que-acontece-quando-o-processo-morre-durante-a-chamada-à-cli):
   etapa `Interrompida`, scope parado na recuperação, retomada pela política da etapa.
7. **Evitar executar de novo uma ação concluída?** Chave de idempotência da etapa, CAS de conclusão, dedupe do
   trabalho e `executada_em` das ações aprovadas.
8. **Registrar resultado antes de avançar?** A conclusão da etapa, a transição da execução, o evento e o próximo
   trabalho são uma única transação; o trabalho atual é marcado `concluido` nela também.
9. **Tarefas com aprovação humana?** A execução vai a `AguardandoAprovacao`, **sem lease e sem processo**; o único
   trabalho pendente é `ExpirarAprovacao` com `disponivel_em = expira_em`. A decisão (Telegram/WebApi) grava o
   pedido decidido e enfileira `AvancarExecucao`. Approvals **da CLI** são diferentes: o processo está vivo e
   esperando; seguem a AD-22 (5 min, em memória) e são só auditadas no ledger.
10. **Recuperar o estado depois de um reboot?** Recuperação na inicialização (ver documento de execuções) roda antes
    do processador: para scopes órfãos, marca interrompidos, devolve leases do boot anterior, reaplica timers.

## Retries e erros

| Erro | Exemplo | Tratamento |
| --- | --- | --- |
| Transitório do D.A.N.T.E. | banco indisponível, conflito de concorrência | Backoff 5 s → 10 min, `max_tentativas` padrão 5 |
| Transitório do provider | limite de plano/uso, CLI sem rede | Etapa `Falhou` com `Retentavel`; a execução volta a `Pendente` e o trabalho é reprogramado com espera longa; o usuário é avisado (`/uso` dá o contexto, AD-31) |
| Permanente | versão de agente desativada, alias removido, modelo indisponível (AD-25) | `Bloqueada` com motivo; nunca troca silenciosa de agente, modelo ou contexto |
| Conteúdo | agente não entregou resultado estruturado | uma nova tentativa com instrução; depois `Falhou` |

## Concorrência no processador

- slots globais (padrão 3 processos de agente simultâneos), por versão de agente (`ExecucoesSimultaneas`) e por
  repositório (escritas); contadores calculados no banco no momento da seleção da etapa, não só em memória;
- trabalhos que não precisam de processo (`ExpirarAprovacao`, `EntregarEvento`) não ocupam slot;
- encerramento gracioso do Worker: para de reivindicar, cancela etapas em andamento (que viram `Interrompida`
  pelo próprio processo, sem esperar a recuperação) e devolve os trabalhos reivindicados.

## Retenção

Trabalhos `concluido` apagados após 7 dias; execuções e eventos mantidos (com exportação/limpeza administrativa
futura). Conteúdo textual de eventos segue a política de redaction e omissão por segredos (AD-21).
