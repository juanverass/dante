# Corte de relevância da busca híbrida (#280)

Continuação da [avaliação lexical × híbrida](../README.md) da #258. A busca híbrida devolvia todos
os itens autorizados, porque não havia corte. Esta pasta registra o mecanismo de corte, a
calibração com 27 consultas, a validação final com 12 consultas separadas e a decisão de
ativação.

## Mecanismo implementado

- **Limiar absoluto de similaridade (R1)**: `DANTE_BRAIN_EMBEDDING_MIN_SIMILARITY`, em cultura
  invariante e na faixa aberta (0, 1). No SQL, só entra no ranking semântico quem atinge o limiar.
  O corte é aplicado **antes** do RRF e da paginação: cada lista é ranqueada só entre os aprovados.
- **Limiar vinculado ao modelo calibrado**: `DANTE_BRAIN_EMBEDDING_MIN_SIMILARITY_MODEL` precisa
  identificar exatamente o modelo configurado, no formato `provedor|nome|versão|dimensão`
  (aqui `ollama|bge-m3|790764642607|1024`), comparado campo a campo. Trocar provedor, nome, versão
  (digest) ou dimensão sem recalibrar desliga a semântica.
- **Fail-closed**: sem limiar válido e vinculado ao modelo atual, a consulta não gera embedding e a
  busca fica lexical. A indexação de vetores não depende do limiar.
- **Correspondência lexical**:
  - quando casa com todos os termos da mensagem, entra sempre. A verificação usa `plainto_tsquery`
    (linguagem natural, lexemas em AND), para que `or`, aspas ou `-` na mensagem não virem operadores;
  - no canal automático com o híbrido ativo, quando casa só alguns dos termos em OR, precisa de
    apoio semântico acima do limiar (`TextoIntegral`, definido pelo Context Builder);
  - o canal agente (MCP), a conversa natural e o modo lexical não mudam.
- **Abstenção**: sem resultado lexical e sem nada acima do limiar, a busca retorna vazio e o pacote
  sai vazio. A expansão por relações parte só dos aprovados.
- **Inalterados**: o embedding do canal automático continua sobre os termos em OR (a variante A
  foi medida e rejeitada, ver abaixo); o limiar `0.6` fixo das fontes brutas
  (`IndiceDeFontesPostgreSql`) não mudou, e essa divergência fica registrada para avaliação
  futura; escopo, sensibilidade, validade, segredos e AD-59 seguem como antes.

## Método

1. As 12 consultas de validação (`tests/Dante.Tests/ConsultasDeReferencia/busca-brain-validacao.json`)
   foram escritas e commitadas (`4562069`) **antes** de qualquer medição de scores. Não participaram
   da calibração.
2. O harness gravou a matriz completa de scores das 27 consultas de calibração contra os 31 itens
   indexáveis, em banco descartável ([`calibracao-matriz.json`](calibracao-matriz.json)).
3. O [`calibrar_corte.py`](../calibrar_corte.py) simula os dois canais com a mesma regra do
   índice. O R0 simulado reproduz o híbrido da #258 (acerto@5 1, MRR 0,883, precisão 0,102). Ele
   varre três regras, com e sem a variante A:
   - R1, limiar absoluto τ;
   - R2, τ mais margem até o melhor score;
   - R3, z-score por consulta mais piso τ.
4. A escolha usa os critérios registrados antes da medição e a **pior utilidade na vizinhança da
   grade** (faixa estável, não ponto isolado), com validação **leave-one-out**
   ([`calibracao-resultado.json`](calibracao-resultado.json)).
5. Por fim, o código real foi medido nas 27 consultas e nas 12 de validação, com τ fixo.
   A validação **não** orientou nenhum parâmetro. Depois da correção de review (vínculo do limiar ao
   modelo e `plainto_tsquery`), as duas medições foram repetidas com o mesmo τ:
   - com corte, acerto, MRR, revocação, precisão e consultas sem resposta ficaram idênticos;
   - a verificação integral coincide com o critério anterior nos 837 pares consulta × item, porque
     nenhuma consulta tem operadores;
   - só mudou a ordem de itens empatados. Os arquivos versionados são os dessa remedição.

A simulação do `calibrar_corte.py` estima o pacote (tokens e prioridade) e não reproduz a expansão
por relações nem o snapshot do Context Builder. Por isso ela orienta a **escolha**, e todos os
números de resultado abaixo vêm do harness com o código real.

Ambiente: Ollama 0.40.14, `bge-m3` (digest `790764642607`, 1024 dimensões), 100% na GPU RTX 3060 Ti;
PostgreSQL 17.11 com pgvector 0.8.7 na instância de testes, com banco aleatório criado e removido
pelo harness.

## Calibração

- **Com o lexical OR intacto, nenhuma regra é viável.** Palavras soltas montam pacote no canal
  automático: S01 casa "cliente", S02 "código" e S03 "busca". O próprio lexical já tinha 3 das 5
  consultas sem resposta com pacote.
- **Com a correspondência lexical parcial exigindo apoio semântico** (decisão do usuário, registrada
  na #280):

  | Regra | Parâmetros | Paráfrase (cal.) | Paráfrase (LOO) | Precisão (LOO) | Pontos viáveis |
  | --- | --- | ---: | ---: | ---: | ---: |
  | R1 | τ = 0,52 | 0,941 | 0,941 | 0,543 | 5 (τ 0,49–0,53) |
  | R2 | τ = 0,52, δ = 0,07 | 0,941 | 0,765 | 0,641 | 142 |
  | R3 | τ = 0,51, z = 1,5 | 0,941 | 0,824 | 0,558 | 44 |

  O R1 foi escolhido porque o leave-one-out é idêntico à calibração (26 de 27 escolhas em 0,52) e
  tem um parâmetro só. R2 e R3 perdem paráfrases no leave-one-out.
- **Variante A** (embedding da mensagem natural no canal automático), medida separadamente:
  - com τ = 0,52, melhora o pacote: precisão 0,633 e pacote com relevante 0,955;
  - mas não tem ponto viável com limiar único, porque S03 ("a busca usa SQLite FTS5") atinge 0,544
    e recebe pacote;
  - um limiar próprio do canal automático (~0,55) ficaria colado na S03, sem faixa estável;
  - foi rejeitada por decisão do usuário.

## Resultado do código real

### Calibração (27 consultas)

Lexical e híbrido sem corte foram reexecutados na mesma sessão e reproduzem as métricas de
qualidade da #258.

| Métrica | Lexical | Híbrido sem corte | Híbrido com corte (τ = 0,52) |
| --- | ---: | ---: | ---: |
| Acerto@5 literal | 1 | 1 | **1** |
| Acerto@5 paráfrase + outro idioma | 0 (0/17) | 1 (17/17) | **0,941 (16/17)** |
| MRR | 0,227 | 0,883 | 0,867 |
| Pacote com relevante | 0,864 | 1 | 0,773 |
| Precisão do pacote | 0,323 | 0,102 | **0,543** |
| Sem resposta com pacote (de 5) | 3 | 5 | **0** |
| Sem resposta com resultado no agente (de 5) | 0 | 5 | 1 |
| Tokens do pacote (média) | 593 | 1862 | **298** |
| Latência agente p50 / p95 (ms) | 11,9 / 14,9 | 32,0 / 39,1 | 23,7 / 26,3 |
| Latência automático p50 / p95 (ms) | 7,8 / 20,0 | 38,6 / 46,0 | 15,1 / 17,9 |
| Embedding por consulta (ms) | — | 8,6 | 7,6 |

Arquivo: [`busca-hibrido-corte.json`](busca-hibrido-corte.json). Lexical e híbrido sem corte vêm da
mesma execução e não foram versionados de novo; os da #258 estão na [pasta anterior](..).

### Validação final (12 consultas, sem ajuste)

| Métrica | Lexical | Híbrido sem corte | Híbrido com corte (τ = 0,52) |
| --- | ---: | ---: | ---: |
| Acerto@5 literal | 1 (2/2) | 1 | **1** |
| Acerto@5 paráfrase + outro idioma | 0 (0/6) | 1 (6/6) | **0,5 (3/6)** |
| MRR | 0,25 | 1 | 0,625 |
| Pacote com relevante | 0,625 | 1 | 0,5 |
| Precisão do pacote | 0,25 | 0,056 | **0,5** |
| Sem resposta com pacote (de 4) | 3 | 4 | **0** |
| Tokens do pacote (média) | 290 | 1852 | 123 |
| Latência agente p50 / p95 (ms) | 11,9 / 13,1 | 33,5 / 39,9 | 25,5 / 29,2 |
| Latência automático p50 / p95 (ms) | 5,7 / 14,0 | 38,6 / 175,2 | 16,6 / 24,9 |

Arquivos: [`busca-validacao-lexical.json`](busca-validacao-lexical.json),
[`busca-validacao-hibrido.json`](busca-validacao-hibrido.json) e
[`busca-validacao-hibrido-corte.json`](busca-validacao-hibrido-corte.json).

## Critérios de decisão (registrados antes da medição)

| Critério | Calibração | Validação |
| --- | --- | --- |
| Acerto@5 literal = 100% | ✓ | ✓ |
| Paráfrase + outro idioma ≥ 80% | ✓ (94,1%) | **✗ (50%)** |
| Zero pacotes sem resposta | ✓ | ✓ |
| Precisão do pacote > 0,323 | ✓ (0,543) | ✓ (0,500) |

## Diagnóstico da validação (a posteriori, sem reajuste)

No híbrido sem corte, o item relevante é o **top-1 semântico nas 8 consultas com resposta**. O
ranking acerta e o limiar é que não separa:

- V03, V06 e V08 têm o relevante em 0,494, 0,496 e 0,500, logo abaixo de τ;
- V10 ("Redis como cache do Worker", sem resposta e próxima do domínio) chega a **0,573**.

Com o `bge-m3`, paráfrases legítimas e ruído próximo do domínio ocupam a mesma faixa de
similaridade (cerca de 0,49 a 0,58). Nenhum limiar absoluto separa os dois casos de forma
estável. A faixa viável da calibração já era estreita (0,04). Escolher τ = 0,49 agora seria
calibrar na validação, e mesmo assim a V10 passaria no canal agente.

## Decisão

**Manter o homelab em modo lexical.** A validação reprovou no critério de paráfrases (50% contra
80% exigidos).

O mecanismo continua útil e seguro:

- sem limiar configurado, nada muda (fail-closed);
- com limiar, a precisão do pacote sobe de 0,10 para cerca de 0,5, as consultas sem resposta
  deixam de receber pacote e os tokens do pacote caem de 84% a 93%;
- as paráfrases ficam bem acima do lexical (50% a 94%, contra 0%), mas abaixo do critério
  combinado.

Caminhos para uma Issue futura, não iniciada:

- reordenar os candidatos com um *reranker* local (cross-encoder), cujo score é calibrado por par
  consulta–documento;
- calibrar com um corpus real maior.

## Reproduzir

```bash
export DANTE_TEST_POSTGRES='Host=127.0.0.1;Port=55433;Username=<conta_teste>;Password=<senha>;Database=postgres'
export DANTE_TEST_EMBEDDING_ENDPOINT=http://127.0.0.1:11434/v1/embeddings DANTE_TEST_EMBEDDING_MODEL=bge-m3
export DANTE_TEST_EMBEDDING_VERSION=790764642607 DANTE_TEST_EMBEDDING_DIMENSION=1024 DANTE_TEST_EMBEDDING_PROVIDER=ollama
export DANTE_TEST_EMBEDDING_MIN_SIMILARITY=0.52 DANTE_TEST_BUSCA_RELATORIO=/tmp/avaliacao-280
# O harness vincula o limiar ao modelo acima (ollama|bge-m3|790764642607|1024).
dotnet test tests/Dante.Tests --filter "FullyQualifiedName~ConsultasDeReferenciaDaBuscaTests"
python3 -I docs/brain/avaliacao-busca-hibrida/calibrar_corte.py /tmp/avaliacao-280/calibracao-matriz.json \
  tests/Dante.Tests/ConsultasDeReferencia/busca-brain.json
```
