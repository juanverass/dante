# Contexto de trabalho (#156)

`ContextoDeTrabalhoAppService.SubstituirAsync` recebe objetivo, tarefa, progresso,
IDs de decisões confirmadas, referências, pendências, próximos passos e último resultado.
São dados operacionais selecionados pelo usuário/adapter: sem campo de transcript ou
raciocínio privado. Marcadores explícitos de chain-of-thought e padrões de credencial são
rejeitados. Não há observador nem armazenamento automático da saída interna das CLIs;
um texto arbitrário disfarçado de resultado não pode ser identificado por heurística.

Há um snapshot por espaço/projeto, inclusive projeto nulo. Tamanho total de texto 8.000,
campos de 2.000, listas de até 20 entradas de 500; decisões são IDs, verificadas como
confirmadas, válidas e no mesmo escopo/classificação. Revisão zero cria, revisão esperada
substitui; restrição única e xmin tratam concorrência. Cada atualização guarda apenas
revisão, responsável, origem e timestamp anteriores, sem duplicar conteúdo antigo.

`RetomarAsync` valida proprietário/projeto ativo e omite expirado/Secreto/Confidencial
sem permissão. Pode compor uma futura retomada junto do Context Pack (#141); não transforma
snapshot em Conhecimento. /clear e /compact atuam no histórico upstream existente e não
chamam este serviço. Nova scope/sessão recupera snapshot no banco sem transcript bruto.
