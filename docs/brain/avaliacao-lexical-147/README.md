# Segunda tentativa lexical: avaliação da #147

Ensaio em PostgreSQL 17.11 descartável, sobre base `7ba7eb1`. Não utiliza modelos,
embeddings ou reranker e não altera índices nem o banco operacional.

A primeira busca é preservada. Quando ela está vazia, a variante implementada
expande apenas famílias linguísticas limitadas de limpeza/encerramento e a
expressão técnica subprocesso(s). Todas as demais âncoras permanecem em cada
alternativa. Isso não interpreta paráfrases gerais nem equipara subordinados a
processos. Consultas com operadores, negações, por ID, derivadas pelo Context
Builder ou com vetor não recebem esse passo. A explicação identifica a expansão.

## Método e reprodução

[AvaliacaoLexical147Tests](../../../tests/Dante.Tests/AvaliacaoLexical147Tests.cs)
semeia os corpora, estabelece autorização, usa AppService/índices reais e compara:

1. Primeira tentativa preservada: `TextoIntegral` impede só a expansão nova, sem
   alterar o SQL lexical. Assim o baseline usa o mesmo pipeline e filtros.
2. OR do extrator já existente do Telegram.
3. Segunda tentativa AND com os termos normalizados, sem expansão.
4. OR com cobertura mínima de 2/3 dos lexemas, calculada no próprio PostgreSQL.
5. Expansão implementada, executada somente se a primeira tentativa estiver vazia.

A cobertura é uma alternativa de laboratório; não integra a busca do produto.
Nenhum rótulo ou resultado esperado é usado pelo algoritmo de produção. Uma
primeira resposta não vazia não é complementada nem reordenada.

O conjunto de 22 consultas é o protocolo anteriormente registrado na #147, sem
alteração: SHA-256 `aaa3279dbade6d97a03277014f16c946a556c177172fbf80815467a920dd8da4`.
A verificação adicional de 16 consultas, em outro corpus, foi fixada antes de
observar os resultados das alternativas: SHA-256
`595516c33f368c426becffba01569959f8ec802afca4e5ec6afe5609ba40f850`.
Essa verificação cobre regras conhecidas, não é um teste cego de entendimento
semântico. Os dois conjuntos históricos de 27 e 12 consultas não foram alterados.
Não se ajustou o vocabulário aos resultados deles.

Cada consulta/variante tem cinco repetições. Elas medem tempo, não multiplicam a
amostra de qualidade. Recall@5 macro = média, nas consultas com resposta esperada,
da fração de relevantes nos cinco primeiros resultados. Precisão agregada =
TP/(TP+FP); TP/FP/FN consideram resultados até limite 10 uma vez por consulta.
Precisão é indefinida quando nenhum resultado é retornado. Negativas são medidas
separadamente. Os relatórios preservam também os resultados por consulta.

Latências p50/p95 usam tempos ordenados, com p95 pelo posto `ceil(0,95*n)`.
Incluem AppService, filtros, SQL e materialização; excluem migração/criação do banco,
CLI, modelo e Telegram. Corpus pequeno, loopback, ordem fixa, um aquecimento inicial
por corpus; efeitos de aquecimento e carga impedem extrapolar para produção.

```sh
# Exemplo apenas para laboratório descartável; nunca usar a conexão canônica.
export DANTE_TEST_POSTGRES='Host=127.0.0.1;Port=55434;Username=dante147_lab;Database=postgres'
export DANTE_147_LEXICAL_EVIDENCIAS=/tmp/avaliacao-147
DOTNET_PROCESSOR_COUNT=4 dotnet test Dante.sln --filter 'FullyQualifiedName~AvaliacaoLexical147Tests|FullyQualifiedName~ExpansaoLexicalTests'
```

`Banco` cria/remove bancos aleatórios nessa instância. O conjunto histórico pode
ser repetido com `ConsultasDeReferenciaDaBuscaTests.LinhaDeBaseLexical` e
`ValidacaoLexical`, usando `DANTE_TEST_BUSCA_RELATORIO` para exportar os JSONs.
Os testes opt-in com CLIs/modelos reais permanecem desligados.

## Limites

A melhoria é lexical e limitada ao vocabulário declarado. A consulta ambígua
“faxina dos subordinados” permanece sem resultado no corpus da #147; não se
inventou equivalência para obter aprovação. As consultas semanticamente distantes
continuam sendo uma limitação, e uma primeira resposta parcial não é complementada.
O OR automático do Context Builder permanece inalterado, incluindo os falsos
positivos já registrados em suas avaliações. Ausência de novos falsos positivos
nestes corpora não garante precisão universal.

Não houve medição de tokens. Os números históricos por bytes não foram usados
como consumo real, e os relatórios de referência que ainda os contêm continuam
rotulados como estimativas. Esta avaliação não comprova eficiência de contexto,
retomada autônoma ou todos os critérios de aceite da #147. O parecer global e a
continuidade da Issue ficam nos comentários da própria Issue.

## Resultados da execução final

Build: 0 erros/0 avisos. Suíte com PostgreSQL descartável: **1792 aprovados,
0 falhas, 42 ignorados**. Execução com `DOTNET_PROCESSOR_COUNT=4`, dentro da suíte
completa; os tempos também refletem sua carga concorrente. Nenhum teste antigo ou
critério de aceite foi relaxado. Os JSONs ligados abaixo contêm todas as consultas,
resultados e amostras de tempo.

| Corpus/variante | Recall@5 | Precisão | TP/FP/FN | Negativas com resultado | p50/p95 ms |
|---|---:|---:|---|---:|---|
| 22 consultas, baseline | 46.875% | 100.00% | 11/0/15 | 0/6 | 7.91/15.41 |
| 22 consultas, or | 81.250% | 40.00% | 20/30/6 | 5/6 | 8.76/14.81 |
| 22 consultas, and-normalizado | 46.875% | 100.00% | 11/0/15 | 0/6 | 9.29/21.81 |
| 22 consultas, or-cobertura | 56.250% | 87.50% | 14/2/12 | 1/6 | 11.63/22.17 |
| 22 consultas, expansao | 59.375% | 100.00% | 15/0/11 | 0/6 | 8.42/22.80 |
| 16 de verificação, baseline | 0.000% | indefinida | 0/0/8 | 0/8 | 7.09/8.86 |
| 16 de verificação, or | 100.000% | 25.81% | 8/23/0 | 7/8 | 8.01/8.94 |
| 16 de verificação, and-normalizado | 0.000% | indefinida | 0/0/8 | 0/8 | 8.63/11.23 |
| 16 de verificação, or-cobertura | 12.500% | 25.00% | 1/3/7 | 3/8 | 10.51/12.22 |
| 16 de verificação, expansao | 100.000% | 100.00% | 8/0/0 | 0/8 | 9.51/12.47 |

Relatórios: [22 consultas](issue147-lexical-resultado.json) e
[verificação adicional](issue147-verificacao-resultado.json).

No conjunto principal, “encerramento dos subprocessos” e “limpar processo
descendente” passam a recuperar incidente e solução. “Faxina dos subordinados”
continua sem resultado, preservando a abstenção. Os conjuntos históricos de 27 e
12 consultas mantiveram acerto@5 de 0,227 e 0,25, com zero negativas retornadas
pela busca explícita; o pacote OR automático manteve os 3 falsos positivos de
cada avaliação.
