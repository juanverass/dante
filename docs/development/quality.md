# Manutenção da qualidade do Brain (#159)

`ManutencaoDoBrainAppService.RevisarAsync` oferece revisão manual/periódica chamada pelo
adapter autorizado, sem observador ou job automático. Escopo exato e proprietário são
validados pela policy. Lotes de até 100 itens, deslocamento até 10.000, 1.000 arestas e
500 achados; LimiteAtingido informa cobertura parcial. Comparação de duplicatas/contradições
é local ao lote, não auditoria global de pares entre páginas. Repetir lotes ou selecionar
um conjunto para revisão; uma sugestão nunca modifica a fonte de verdade.

Duplicatas potenciais usam igualdade de texto normalizado ou alta sobreposição de termos.
Contradições potenciais usam objetos JSON com mesma chave/valor divergente ou negação em
textos próximos. São heurísticas conservadoras com falsos positivos/negativos, não julgamento
semântico universal. Conflitos explícitos usam CONTRADIZ. Relatório também indica órfãos,
fontes sem referência lógica, estado inativo/substituído, validade e última confirmação
anterior ao corte opcional informado pelo usuário. Não testa existência física/URL de fonte.

`ConsolidarAsync` exige revisões esperadas e decisão do proprietário com referência e trecho.
Mantém conteúdo/status do destino e copia todas as proveniências distintas das revisões das
fontes, registrando decisão explícita. Fontes viram Substituido com relações SUBSTITUI no mesmo
commit. Não reduz sensibilidade (incluindo histórico copiado), nem substitui confirmado por
inferido; conflitos abertos exigem resolução antes. História original continua intacta.

`MarcarContradicaoAsync` registra conflito simétrico idempotente com evidência. `ResolverConflitoAsync`
exige lado escolhido confirmado/válido, revisões esperadas e ação explícita: resolução registra
responsável/evidência/timestamp e Id escolhido, preservando o ato original de CONTRADIZ.
Lado anterior ativo vira Substituido; inativado previamente permanece histórico. Nenhuma
inferência resolve conflito automaticamente. `InvalidarAsync` preserva história e permite
inativar item expirado, sem apagar evidência. Dispose da scope após falha/concurrency;
UoW faz rollback e scope nova recupera estado atual.

`SelecionarParaContextoAsync` é uma capacidade neutra anterior ao Context Pack (#140):
filtra no banco inativos/substituídos, fora da validade, Secreto/Confidencial sem permissão
e qualquer item ligado a CONTRADIZ ainda aberto, mesmo fora da página de revisão. Prioriza
confirmados sobre inferidos e devolve status/projeção protegida; não declara inferência verdade.
Nenhum transcript, export ou integração automática com agentes é acrescentado aqui.
