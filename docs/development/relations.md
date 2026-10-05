# Relações de Conhecimento (#153)

RelacaoDeConhecimento é entidade do Domain, com Guid Id, origem/destino, tipo, escopo,
proveniência e timestamp. Infrastructure persiste via EF Core; não há banco de grafos.
IRelacaoDeConhecimentoAppService oferece criação idempotente e vizinhança limitada; DTOs
usam Mapster explícito. Chamador deve fornecer espaço/projeto e aplicar policy (#150)
antes de publicar essas operações por qualquer host.

| Nome conceitual | Enum canônico | Direção e semântica |
| --- | --- | --- |
| RELATES_TO | RelacionadoA | Simétrica, associação explícita |
| BELONGS_TO | PertenceA | Origem faz parte do destino lógico |
| SUBSTITUI | Substitui | Novo substitui antigo; exige ato canônico |
| DERIVED_FROM | DerivadoDe | Origem derivada do destino |
| RESOLVIDO_POR | ResolvidoPor | Incidente → Solucao |
| PRODUZIU_APRENDIZADO | ProduziuAprendizado | Solucao → Aprendizado |
| DEPENDS_ON | DependeDe | Origem depende do destino |
| REFERENCES | Referencia | Origem referencia destino |
| CONTRADIZ | Contradiz | Simétrica, conflito explícito sem resolver automaticamente |

Itens distintos e mesmo espaço/projeto obrigatório; novas relações exigem itens ativos,
exceto destino já substituído em Substitui. Relações simétricas normalizam endpoints
por Guid; UNIQUE impede duplicação e FKs compostas impedem órfãos/outros espaços.
Tipos semânticos validados na criação; proveniência/timestamp preservam o ato histórico,
não são prova de validade atual após futuras correções dos itens. Context Builder deve
consultar status/validade/histórico dos endpoints antes de usar o vínculo.

SubstituirAsync do ConhecimentoAppService registra novo → antigo com Tipo Substitui
na mesma UnitOfWork do estado/histórico canônico. O port de relações é opcional apenas
para compatibilidade dos consumidores sem persistência anteriores; AddInfrastructure
configurado sempre o fornece. Correções preservam identidade e relações existentes,
assim como o histórico; não redirecionam automaticamente origens ou transformam
contradições em confirmação. Remoção física de endpoint com relações é recusada pela FK.

Vizinhanca faz BFS nas duas direções (DTO preserva a direção semântica), uma consulta
por nível, com profundidade 1–3, limite 1–100, <=101 linhas por consulta e fronteira
<=201 IDs. Visitados evitam loops; limite atingido sinaliza truncamento conservador,
sem buscar grafo inteiro nem retornar conteúdo duplicado dos Conhecimentos. Escopo é
filtrado no SQL antes do limite. Fronteira vazia termina; cancelamento interrompe.
Somente o mesmo projeto, inclusive null (itens diretos do espaço), participa da consulta.

Testes Domain validam direção, origem, escopo e semântica; testes PostgreSQL reais
cobrem cadeia Incidente/Solucao/Aprendizado, ciclos, deduplicação, limites, FK e
substituição/histórico na mesma transação. Policy/busca/Context Builder seguem nas
respectivas issues; o host Telegram não expõe CRUD Brain sem policy.
