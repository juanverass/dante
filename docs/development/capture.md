# Captura e candidatos de conhecimento (#139)

ICapturaDeConhecimentoAppService é a porta neutra para comandos de conversa já interpretados
pelo adapter. Recebe somente o trecho selecionado, classificação, justificativa, evidência,
espaço/projeto e responsável. Não lê transcripts nem é registrado como observador de turnos.
Nenhuma mensagem comum captura conhecimento por efeito colateral. A UX natural do Telegram,
comandos “guarde isso”/“documente esta solução” e policy de identidade são #157/#150; o adapter
chama esta porta após interpretar a intenção e autorizar o escopo. Não expor CRUD sem policy.

## Fluxo

1. `CapturarAsync(CapturaDeConhecimentoDto)` cria/reutiliza candidato Pendente, com
   Modo Explicita ou SugestaoAutomatica. Tipos incluem Decisao, Preferencia, Incidente,
   Solucao, Aprendizado, Procedimento, Fato, Referencia e Inferencia; outros tipos do
   Conhecimento também são aceitos. Sugestão nunca é confirmação.
2. Natureza DitoPeloUsuario, ConclusaoDoAgente ou FonteSelecionada fica imutável.
   ConclusaoDoAgente nasce classificada como Inferencia, mesmo se o agente sugerir Fato.
3. `ListarPendentesAsync` fornece candidatos no escopo exato, com limite 1–100, para
   apresentar na conversa. Histórico/DTO permite explicar conteúdo, origem e evidências.
4. `CorrigirAsync` permite classificação/conteúdo/sensibilidade/justificativa corrigidos
   pelo usuário, mantendo identidade/escopo/origem e histórico anterior. Não confirma.
5. `ConfirmarAsync` promove só o candidato indicado e sua revisão esperada, com ator,
   referência e trecho de confirmação; cria Conhecimento na mesma transação do estado
   Promovido. Inferencia permanece tipo Inferencia e status Inferido; os outros tipos
   ganham confirmação explícita registrada no histórico do Conhecimento.
6. `RejeitarAsync`/`DescartarAsync` encerram candidatos com auditoria, sem criar qualquer
   Conhecimento. Audit trail dos candidatos permanece separado da canônica.

Todos os atos exigem responsável Guid, referência/trecho e instante; cada versão registra
conteúdo, classificação, sensibilidade, justificativa e proveniência. Revisão desatualizada
ou estado encerrado recusam. Espaço/projeto arquivados recusam captura/correção/promoção,
mas permitem leitura e encerramento de pendências. Autorização continua sendo uma policy
separada; Guid e escopo não são prova de permissão.

## Deduplicação

Equivalência conservadora: escopo, tipo, sensibilidade, natureza, vínculos de consolidação e
conteúdo igual após trim/NFC/normalização CRLF. Mantém case/espaços internos para não juntar
código ou evidências distintos. Não tenta resolver semanticamente fatos conflitantes (#159).
Modo/origem física podem diferir: evidência da captura equivalente é acrescida ao histórico do
mesmo candidato pendente. Candidato promovido retorna sua identidade/IdConhecimento; rejeitado
ou descartado continua encerrado, evitando reapresentação automática do mesmo conteúdo. Um
novo conteúdo/classificação explícito pode gerar outro candidato. Não há deduplicação global
com conhecimento criado por outros canais nesta issue, apenas candidatos equivalentes.

Índice UNIQUE com NULLS NOT DISTINCT (PostgreSQL 15+) protege também conhecimento sem projeto.
Corrida entre capturas equivalentes vira ConflitoDeConcorrenciaException sanitizada; o chamador
recarrega em **novo scope** e reapresenta o candidato existente. Não tentar salvar o contexto
que falhou. Promoção concorrente usa xmin: rollback remove a inserção perdedora de Conhecimento.
Correção colidindo com outro candidato equivalente é recusada, sem commit.

## Consolidar Incidente/Solucao/Aprendizado

Para captura de aprendizado, informe IdIncidente e IdSolucao existentes, ativos, tipados e do
mesmo espaço/projeto. ConfirmarAsync valida novamente os endpoints e grava Conhecimento
Aprendizado + Incidente → ResolvidoPor → Solucao → ProduziuAprendizado → Aprendizado na mesma
UnitOfWork. Reutiliza vínculo ResolvidoPor equivalente; não duplica os conteúdos originais.
Uma conclusão do agente precisa primeiro de correção/classificação explícita como Aprendizado
com evidência. Alteração/remoção posterior dos endpoints exige revisão dos vínculos (#159),
não promoção automática de inferências.

## Persistência e validação

CandidatoDeConhecimento é agregado Domain; ports/DTOs/Mapster/casos de uso na Application;
EF, configuration/repository e migration própria na Infrastructure. Histórico do agregado
em JSONB, campos canônicos explícitos, FKs e xmin. Não há EF/SQL/Telegram no núcleo.
Conexão/configuração/backup seguem [operação local](../brain/LOCAL_STORAGE.md). Dumps incluem
candidatos e sua auditoria. Sensibilidade é preservada; policy de secrets/injeção é da #155.

Testes sem banco cobrem classificação, inferências, evidências/revisão/auditoria e equivalência.
PostgreSQL real cobre confirmação/correção/rejeição/descarte, restart, deduplicação, concorrência
com rollback e consolidação transacional. Sugestões usam exatamente o mesmo fluxo pendente.
