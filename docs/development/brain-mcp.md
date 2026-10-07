# Brain nas sessões de agentes (#226)

O Worker oferece `dante_brain` às sessões Claude/Codex iniciadas com Brain configurado,
storage acessível, identidade Telegram resolvida e espaço/projeto permitido. Sem esses
requisitos a sessão inicia normalmente, sem ferramentas Brain. One-shot não recebe
esse servidor. O harness de ambas as CLIs declara explicitamente o Brain como capacidade
interna, inclusive quando indisponível, sem descoberta de plugin/connector ou solicitação
de URL externa (#228). Claude recebe o contrato por `--append-system-prompt`; Codex por
`developerInstructions` em toda `thread/start`, incluindo `/clear`. O MCP também anuncia
o contrato em `initialize.instructions`. O contrato distingue captura de candidato,
correção com revisão, proposta de consolidação e confirmação no Telegram; atualização
de conhecimento confirmado passa por novo candidato. Fusão de duplicatas não está
exposta pelo MCP: o agente consulta equivalentes e apresenta revisão sem simular fusão.
O escopo autorizado permanece independente do repositório investigado.

`dante_planilhas` continua disponível por um provider independente;
`CompositorDeFerramentas` reúne os providers de `IProvedorDeFerramentas`.

## Canal local e escopo

O processo `--mcp-brain --pipe <endpoint>` implementa JSON-RPC MCP stdio e encaminha
operações por named pipe ao Worker vivo. O Worker conserva conexão e autorização e
chama exclusivamente AppServices. A configuração MCP contém somente executável e
endpoint aleatório, nunca conexão, senha ou identidade escolhida pelo modelo.
O pipe exige o mesmo usuário do sistema, aceita uma conexão, pertence a uma sessão e
é revogado ao encerrar/falhar a sessão ou desconectar o consumidor. Não conectado,
expira em dois minutos. Não existe credencial durável em disco. Como as CLIs locais,
o pipe pressupõe confiança na conta do sistema: não isola processos maliciosos do
mesmo usuário que já possam ler a memória/configuração local da CLI.

A identidade, chat/tópico e escopo vêm da mensagem autenticada que inicia a sessão.
Cada operação resolve novamente o acesso via TelegramBrain e valida storage/permissões.
Trocar espaço/projeto invalida as operações do canal anterior: inicie nova sessão para
receber ferramentas do novo escopo. Enviar a sessão por outra conversa/tópico também
recusa o canal anterior. Reinício do Worker não restaura capacidades.

## Ferramentas

| Ferramenta | Comportamento |
| --- | --- |
| brain_obter_escopo | Nomes do espaço/projeto autorizado, sem seleção livre de IDs |
| brain_buscar_conhecimento | Busca lexical/híbrida com texto, tipo, tags e paginação |
| brain_mostrar_origem | Proveniência e histórico autorizado de candidato/conhecimento |
| brain_capturar_conhecimento | Um candidato por chamada; título, conteúdo, tags, tipo, natureza, sensibilidade e justificativa |
| brain_listar_candidatos | Pendentes com IDs e revisões para o agente, sem pedir GUID ao usuário |
| brain_corrigir_candidato | Correção com revisão esperada, sem alterar natureza ou escopo |
| brain_confirmar_candidato | Prepara proposta de consolidação, sem promovê-la |
| brain_cancelar_candidato | Prepara descarte auditado, sem aplicá-lo |
| brain_criar_relacao | Prepara relação tipada entre alvos autorizados com revisões |
| brain_listar_relacoes | Vizinhança autorizada de um conhecimento |
| brain_obter_contexto_de_trabalho | Snapshot operacional ativo |
| brain_atualizar_contexto_de_trabalho | Snapshot com revisão esperada, sem criar Conhecimento |

Ferramentas de leitura são `readOnlyHint=true`; correção, descarte e substituição do
snapshot são `destructiveHint=true`. Mutações seguem o modo de aprovação da CLI.
Isso não substitui confirmação de negócio: propostas de consolidação, descarte ou
relação devem ser apresentadas ao usuário, que envia `confirmar` ou `cancelar` no
Telegram. Há uma proposta por conversa, válida por cinco minutos e consumida uma vez.
Uma nova mensagem/proposta ou fim de sessão invalida a anterior. Confirmação revalida
escopo e revisão; conflitos não reaplicam a operação automaticamente.

Captura é explícita e individual. Conteúdo classificado como dito pelo usuário precisa
estar literalmente na mensagem de origem; fonte selecionada pode estar na mensagem
citada. Conclusão do agente é classificada como Inferencia pelo Domain e permanece
inferida após consolidação. Título e tags opcionais vivem no histórico JSON existente
do candidato; promoção conserva o título em `DadosEstruturados.titulo` e as tags no
Conhecimento. Não há migration nova nem alteração do conteúdo para embutir metadados.

A captura conserva a deduplicação atual por conteúdo/classificação/escopo. Repetição
retorna o candidato existente e sinaliza possível duplicidade; origem equivalente
é auditada. Equivalência semântica requer busca e análise do agente: os resultados
são possibilidades para apresentar, nunca autorização para consolidar silenciosamente.
Proveniência registra MCP/agente, sessão, referência da mensagem e trecho selecionado.
Não registra transcript completo ou raciocínio privado.

Confidential/Secret não recebem grants adicionais. Falta de autorização falha fechado;
conteúdo/histórico passam pelas policies de sensibilidade e proteção de segredos atuais.

## Limites e validação

Uma captura/relação por chamada; conteúdo até 10 mil caracteres, título até 300,
até 50 tags de 100 caracteres. Listagens até 25 itens/página, deslocamento até 10 mil.
Entrada até 32 mil caracteres, resposta MCP até 60 mil, mil chamadas por sessão e
30 segundos por operação. Resposta excessiva retorna orientação para reduzir a consulta.
Operações com erro/resultado incerto não são repetidas automaticamente.

```bash
dotnet test Dante.sln --filter 'FullyQualifiedName~BrainMcpTests|FullyQualifiedName~SessionDriverTests'
```

`BrainMcpTests` usa `DANTE_TEST_POSTGRES` em instância de teste com CREATE DATABASE;
cria e remove bancos isolados. Testa 24 capturas por IPC, deduplicação, título/tags,
confirmação, recuperação em nova sessão, isolamento e revisão.
E2E opt-in com CLIs reais:

```bash
DANTE_LIVE_BRAIN_MCP=1 dotnet test Dante.sln --filter FullyQualifiedName~LiveBrainMcpEvidenceTests
```

Com `DANTE_TEST_POSTGRES` definido, cada agente recebe os nomes Desenvolvimento e
D.A.N.T.E. em banco isolado, captura três itens separadamente pelo MCP stdio,
confirma um pelo fluxo natural e recupera conhecimento/proveniência em outra sessão.
A conexão de teste nunca vai à CLI. Não habilite esse teste com banco produtivo.

O MCP usa a mensagem autenticada do turno ativo, preservada junto à entrada na fila.
Mensagens recebidas/enfileiradas não substituem essa evidência; entradas sem contexto
autenticado revogam o acesso. Captura e correção factual exigem conteúdo na origem.
Relações são projetadas somente quando ambos os alvos permitem leitura; sua prova
bruta não é exposta no canal MCP. Sessão ausente/revogada falha fechado.

Steer nativo aceito pelo driver atualiza a evidência autenticada do Brain; steer
recusado mantém a origem anterior. Respostas textuais a perguntas do agente não
alimentam evidência factual no MCP neste MVP. A listagem de relações percorre
páginas internas de até 100 itens até preencher o limite de relações permitidas
ou esgotar a vizinhança; o indicador de limite conta apenas relações autorizadas.
O prazo por operação continua limitando o percurso, sem retornar lista parcial
como se a vizinhança tivesse sido esgotada.
