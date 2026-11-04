# Avaliação lexical × híbrida da busca do Brain (#258)

Medição da busca do Brain com o mesmo conjunto de referência nos dois modos. Os números
abaixo vêm dos relatórios versionados nesta pasta, gerados pelo harness opt-in
`ConsultasDeReferenciaDaBuscaTests`:

| Arquivo | Modo | Gerado em |
| --- | --- | --- |
| [`busca-lexical.json`](busca-lexical.json) | lexical (linha de base) | 2026-11-04 14:40 UTC |
| [`busca-hibrido.json`](busca-hibrido.json) | híbrido (lexical + pgvector, RRF) | 2026-11-04 17:39 UTC |

## Ambiente

- Conjunto `busca-brain.json` versão 1: 34 itens sintéticos (31 indexáveis) e 27 consultas (5 literais,
  15 paráfrases, 2 em outro idioma, 2 sem resposta, 3 de sensibilidade). Itens Secreto,
  Confidencial e inativo são proibidos no índice e no resultado.
- PostgreSQL 17.11 com pgvector 0.8.7 numa **instância exclusiva de testes**
  (`127.0.0.1:55433`). Cada execução cria e remove um banco aleatório. O banco canônico
  não é lido nem alterado.
- Embedding local: Ollama 0.40.14 (release oficial, SHA-256 do tarball conferido), em
  processo de usuário, ouvindo só em `127.0.0.1:11434`. Modelo `bge-m3`, digest
  `7907646426070047a77226ac3e684fbbe8410524f7b4a74d02837e43f2146bab`, F16, 1024 dimensões.
  O endpoint é `/v1/embeddings` (contrato `input`/`model` → `data[0].embedding`).
- GPU: RTX 3060 Ti, 8 GB, driver 595.99.02, CUDA 13.2. `ollama ps` reporta `100% GPU`
  e o log mostra 25/25 camadas na `CUDA0`. Durante a medição: 902 MiB de VRAM e pico de
  40% de uso da GPU.
- Canal agente: `BuscarAsync` com texto cru e limite 10. Canal automático:
  `ConstruirAsync` com termos em OR, orçamento de 2048 tokens e limite de 12 itens.

## Resultado geral

| Métrica | Lexical | Híbrido |
| --- | ---: | ---: |
| Acerto@1 | 0,227 | 0,818 |
| Acerto@5 | 0,227 | 1 |
| MRR | 0,227 | 0,883 |
| Revocação@5 | 0,214 | 0,987 |
| Pacote com relevante | 0,864 | 1 |
| Precisão do pacote | 0,323 | 0,102 |
| Sem resposta com resultado (de 5) | 0 | 5 |
| Sem resposta com pacote (de 5) | 3 | 5 |
| Recuperados (média) | 3,926 | 31 |
| Selecionados no pacote (média) | 3,556 | 12 |
| Descartados (média) | 0,37 | 19 |
| Tokens do pacote (média) | 592,667 | 1861,444 |
| Tokens do pacote (total) | 16002 | 50259 |
| Latência agente p50 (ms) | 11,753 | 30,113 |
| Latência agente p95 (ms) | 30,147 | 32,9 |
| Latência automático p50 (ms) | 7,544 | 37,272 |
| Latência automático p95 (ms) | 16,925 | 45,134 |
| Embedding por consulta (ms) | 0 | 7,717 |

As taxas de acerto, MRR e revocação são calculadas sobre as 22 consultas com resposta. As
linhas "sem resposta" cobrem as 5 consultas sem item relevante permitido (sem-resposta e
sensibilidade). Indexação: 31 vetores em 467 ms (10,3 ms por embedding). Primeira
consulta após a indexação: 39 ms.

## Por categoria

| Categoria | Consultas | Acerto@5 lexical | Acerto@5 híbrido | MRR lexical | MRR híbrido | Tokens lexical | Tokens híbrido |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| literal | 5 | 1 | 1 | 1 | 1 | 692,6 | 1861 |
| parafrase | 15 | 0 | 1 | 0 | 0,828 | 633,4 | 1859,8 |
| outro-idioma | 2 | 0 | 1 | 0 | 1 | 747 | 1888,5 |
| sem-resposta | 2 | 0 | 0 | 0 | 0 | 0 | 1859,5 |
| sensibilidade | 3 | 0 | 0 | 0 | 0 | 514,667 | 1853,667 |

## Por consulta (canal agente)

| Consulta | Categoria | Relevantes | Lexical top-1 | Híbrido top-3 | Posição do 1º relevante (híbrido) |
| --- | --- | --- | --- | --- | ---: |
| L01 | literal | brain-postgres | brain-postgres | brain-postgres, procedimento-backup, incidente-grants | 1 |
| L02 | literal | brain-postgres, procedimento-backup, incidente-scram, solucao-scram, incidente-grants, solucao-grants, fato-tailscale | solucao-scram | solucao-grants, solucao-scram, brain-postgres | 1 |
| L03 | literal | brain-postgres | brain-postgres | brain-postgres, decisao-rrf, referencia-pgvector | 1 |
| L04 | literal | brain-postgres | brain-postgres | brain-postgres, solucao-grants, decisao-candidatos | 1 |
| L05 | literal | solucao-happy-eyeballs | solucao-happy-eyeballs | solucao-happy-eyeballs, decisao-rrf, aprendizado-snapshot | 1 |
| P01 | parafrase | brain-postgres | — | brain-postgres, incidente-grants, procedimento-backup | 1 |
| P02 | parafrase | brain-postgres | — | fato-tailscale, fato-hardware, nota-runner | 4 |
| P03 | parafrase | brain-postgres | — | fato-hardware, solucao-orfao, brain-postgres | 3 |
| P04 | parafrase | brain-postgres | — | fato-hardware, fato-tailscale, brain-postgres | 3 |
| P05 | parafrase | incidente-ipv6, solucao-happy-eyeballs, aprendizado-ipv6-dns | — | incidente-ipv6, fato-allowlist, solucao-happy-eyeballs | 1 |
| P06 | parafrase | solucao-happy-eyeballs, incidente-ipv6 | — | aprendizado-ipv6-dns, solucao-happy-eyeballs, incidente-ipv6 | 2 |
| P07 | parafrase | limitacao-busca | — | limitacao-busca, decisao-candidatos, decisao-rrf | 1 |
| P08 | parafrase | incidente-scram, solucao-scram | — | incidente-scram, solucao-scram, incidente-ipv6 | 1 |
| P09 | parafrase | incidente-grants, solucao-grants | — | incidente-grants, incidente-scram, procedimento-reindex | 1 |
| P10 | parafrase | procedimento-deploy | — | procedimento-deploy, pessoal-passaporte, nota-runner | 1 |
| P11 | parafrase | procedimento-backup | — | procedimento-backup, instrucao-segredos, aprendizado-snapshot | 1 |
| P12 | parafrase | fato-hardware | — | fato-hardware, fato-ferramentas-mcp, solucao-happy-eyeballs | 1 |
| P13 | parafrase | incidente-orfao, solucao-orfao | — | solucao-orfao, incidente-orfao, aprendizado-snapshot | 1 |
| P14 | parafrase | preferencia-ptbr | — | preferencia-ptbr, decisao-erros-mcp, pessoal-passaporte | 1 |
| P15 | parafrase | decisao-rrf | — | decisao-rrf, procedimento-reindex, decisao-pgvector-exato | 1 |
| X01 | outro-idioma | decisao-rrf, decisao-pgvector-exato | — | decisao-rrf, decisao-pgvector-exato, decisao-candidatos | 1 |
| X02 | outro-idioma | incidente-ipv6, solucao-happy-eyeballs | — | incidente-ipv6, solucao-happy-eyeballs, aprendizado-ipv6-dns | 1 |
| N01 | sem-resposta | — | — | pessoal-passaporte, solucao-happy-eyeballs, instrucao-segredos | — |
| N02 | sem-resposta | — | — | procedimento-reindex, aprendizado-tokens, procedimento-deploy | — |
| S01 | sensibilidade | — | — | incidente-orfao, incidente-grants, pessoal-passaporte | — |
| S02 | sensibilidade | — | — | preferencia-ptbr, pessoal-passaporte, aprendizado-tokens | — |
| S03 | sensibilidade | — | — | decisao-pgvector-exato, procedimento-reindex, referencia-pgvector | — |

## Invariantes verificados pelo harness

As asserções passaram em todas as execuções:

- o endpoint de embedding é loopback (`Externo = false`);
- nenhum item Secreto, Confidencial sem permissão ou inativo recebeu vetor, apareceu no
  resultado ou foi selecionado no pacote;
- 31 itens indexáveis geraram 31 vetores;
- nenhuma consulta literal perdeu o acerto@5 do modo lexical.

## Reprodutibilidade

Foram feitas três execuções do híbrido e duas do lexical na mesma sessão.

- **Qualidade**: acerto@1, acerto@5, MRR e revocação@5 iguais nas três execuções
  híbridas. A precisão do pacote variou de 0,099 a 0,102.
- **Ordem de empates**: o lexical reexecutado tem as mesmas métricas do baseline
  versionado, mas a ordem de itens com score empatado muda entre execuções (L01, L02,
  P01, P02, P05, P06, S03). No híbrido isso aparece como quase-empates do RRF, por
  exemplo na ordem do top-5 da L02. Não há critério de desempate determinístico.
- **Latência fria**: na primeira execução híbrida, 10 das 27 consultas tiveram
  embedding medido pelo cliente entre 1,3 s e 2,1 s (p95 de 2066 ms no agente). No
  mesmo intervalo, o log do Ollama registrou de 9 a 15 ms por requisição. Replays
  fora do .NET (curl e conexão persistente, 27 textos × 2) ficaram entre 6 e 12 ms.
  As execuções 2 e 3 não repetiram o efeito. A causa ficou indeterminada: o atraso está
  do lado do cliente, fora do processamento do modelo. Os números acima são da
  execução 3.

## Leitura

- **Recuperação**: o híbrido resolve a limitação observada na #147. Paráfrases e
  consultas em outro idioma passam de 0 para 100% de acerto@5, e as literais não
  perdem nada.
- **Sem corte de relevância**: a busca vetorial exata devolve todos os itens
  autorizados, e o RRF não descarta nenhum (31 recuperados em toda consulta). Por isso:
  - toda consulta sem resposta passa a ter resultado e pacote;
  - o pacote automático sempre enche até o limite de 12 itens;
  - os tokens do pacote triplicam (média de 593 para 1861, dentro do orçamento de 2048);
  - a precisão do pacote cai de 0,323 para 0,102.
- **Custo de latência**: cerca de 8 ms de embedding na GPU por consulta, e cerca de 20 a
  30 ms a mais por busca no total.

## Decisão

**Manter a busca híbrida desligada na instância do homelab** por enquanto. O
`DANTE_BRAIN_EMBEDDING_*` não foi configurado em produção, e o modo segue `lexical`.

Motivos:

- o pacote automático é montado a cada mensagem do Telegram. Sem corte de relevância,
  qualquer mensagem, inclusive as sem relação com o Brain, passaria a injetar cerca de
  1860 tokens com cerca de 90% de itens irrelevantes;
- isso vai contra a construção seletiva de contexto (#140);
- o ganho de recuperação é real e está medido, mas corrigir o ruído exige um limiar de
  similaridade ou corte no ranking, e mudar o ranking (RRF) está fora do escopo da
  #258.

A decisão é reversível só por configuração. Para reavaliar, use o mesmo harness e
este conjunto. A recomendação é tratar o corte de relevância semântica numa Issue
própria e repetir esta medição antes de habilitar.

> **Atualização (#280):** o corte de relevância foi implementado e medido. O homelab continua
> lexical porque as paráfrases não atingiram o critério na validação final. Ver
> [corte de relevância](corte-280/README.md).

## Como reproduzir

Pré-requisitos: instância de teste com pgvector e conta com CREATE DATABASE, e Ollama
local com `bge-m3` (ver [busca](../../development/search.md#provedor-local-ollama--bge-m3)).

```bash
export DANTE_TEST_POSTGRES='Host=127.0.0.1;Port=55433;Username=<conta_teste>;Password=<senha>;Database=postgres'
export DANTE_TEST_EMBEDDING_ENDPOINT=http://127.0.0.1:11434/v1/embeddings
export DANTE_TEST_EMBEDDING_MODEL=bge-m3
export DANTE_TEST_EMBEDDING_VERSION=790764642607
export DANTE_TEST_EMBEDDING_DIMENSION=1024
export DANTE_TEST_EMBEDDING_PROVIDER=ollama
export DANTE_TEST_BUSCA_RELATORIO=/tmp/avaliacao-258
dotnet test tests/Dante.Tests --filter "FullyQualifiedName~ConsultasDeReferenciaDaBuscaTests"
```

Os relatórios `busca-lexical.json` e `busca-hibrido.json` são gravados no diretório
indicado. Para atualizar esta pasta, copie-os para cá conscientemente.
