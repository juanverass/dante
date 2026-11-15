# Cenários da retomada: protocolo da #147 (Fase 0)

Esta é a infraestrutura de teste para medir o PacoteDeContexto contra o histórico visível em três durações. Ela usa dados
reais e comparáveis. Nada aqui chama o Claude, executa o E2E real ou mede tokens: essas etapas dependem de
autorização específica na #147. Só código de teste foi alterado; o código de produção não mudou.

## 1. Restauração do cenário curto

As retomadas medidas em 15/11/2026 tinham bancos descartáveis já removidos; o estado só existe no export
`estadoAntesDeB` de cada `resultado.json`.
[`RestauracaoDoBrain147`](../../../tests/Dante.Tests/RestauracaoDoBrain147.cs) restaura esse estado.

1. Lê a execução só se o manifesto conferir com o disco (`ExecucaoDeMedicao147.Verificar`). O teste exige também o hash do
   manifesto publicado (`7d83c7cf…` r1, `d089a2ab…` r2).
2. Recria espaço e projeto com os Ids exportados, o proprietário da identidade Telegram 42 e os nomes usados pelo E2E.
3. Insere fontes, conhecimentos, candidatos, relações, vínculos e snapshot, uma linha por vez.
   - Cada propriedade mapeada pelo EF, inclusive campos privados como `historico`, recebe o valor exportado de mesmo nome.
   - Propriedade sem correspondente no export é erro. Coleção extra e banco não vazio também são recusados.
   - Os índices derivados (`brain_index`) vêm dos triggers de produção.
4. Reexporta pela mesma serialização do harness e exige igualdade **byte a byte** com o export.
5. Reconstrói o pacote pela mesma chamada da sessão B, com os mesmos parâmetros:
   - `usar espaço` / `usar projeto`;
   - `TelegramBrain.PrepararAsync` com a consulta exportada;
   - mensagem 300, sessão `147B`, bootstrap.

**Prova histórica:** o estado restaurado, no código anterior à #289, precisa reproduzir byte a byte o pacote histórico.

- Os arquivos `RestauracaoDoBrain147.cs`, `RestauracaoDoBrain147Tests.cs` e `CenariosDaRetomada147.cs` são copiados sem
  alteração para um worktree em `97bd98d`. Lá roda o filtro `RestauracaoDoBrain147Tests`.
- O teste detecta o formato pelo DTO e compara três artefatos com os exportados:
  - `pacote-texto-para-injecao.txt`;
  - `entrada-b.txt`;
  - `pacote.json`, em que só o Id aleatório do pacote é trocado textualmente pelo histórico.
- Resultado: r1 `61598ec6…` / `73a7adb2…` / `065b3706…`; r2 `6503e862…` / `9c59743d…` / `44f17d0b…`. Todos são
  idênticos aos hashes do manifesto.

**Formato vigente:** duas restaurações em bancos distintos produzem os mesmos bytes, iguais aos hashes pré-registrados
no teste:

- r1: texto `b7a6562f…` (4.238 B);
- r2: texto `48ece640…` (4.175 B).

As 18 unidades essenciais do caso 1 estão presentes nos dois pacotes. O teste roda com `DANTE_TEST_POSTGRES` e as
evidências locais em `~/.local/share/dante-tests/issue-147`. As evidências não são versionadas; sem elas, o teste é
ignorado.

## 2. Roteiros pré-registrados

[`CenariosDaRetomada147/cenarios.json`](../../../tests/Dante.Tests/CenariosDaRetomada147/cenarios.json) tem SHA-256
`9ed460f1645a8f9cdc38688954bb0a5dd60eaa0f4ef21ba5befe04f449cea58b`. O hash é fixado em teste e publicado na #147.
Alterar a fixture exige novo pré-registro.

| Duração | Casos | Turnos de A | Casos (PR, commit) |
|---|---:|---:|---|
| curto | 1 | 2 | #283 `146a0fe` |
| médio | 3 | 6 | + #285 `9951c17`, #247 `4d90671` |
| longo | 6 | 12 | + #245 `9ed53b2`, #261 `5f98cc5`, #89 `9039865` |

Cada caso tem dois turnos: o registro da fonte (Incidente e Solução como `ConclusaoDoAgente`, com vínculo) e a
atualização do snapshot com as referências de todos os registros.

- **Caso 1:** é a fixture preexistente do harness. Os pedidos do curto reproduzem exatamente `entrada-a1`/`entrada-a2` da
  r1 (`b0b8c3bc…`, `36be7077…`), e a consulta B do curto é a das medições.
- **Casos 2–6:** incidente, solução e título são citação literal da mensagem do commit integrado. O teste confere isso
  com `git`: o commit existe, é ancestral do HEAD e tem a PR no assunto.
- **Identificador:** o marcador aleatório de cada execução para o caso 1, `<marcador>-cN` para os demais.
- **Respostas:** as respostas de A são do modelo real na execução. Nenhuma conversa é escrita na fixture.

O E2E `SessaoIndependenteRetomaCasoRealSomentePeloBrain(duracao, repeticao)` segue o roteiro em 6 combinações
(3 durações × 2 repetições). Ele continua opt-in (`DANTE_LIVE_BRAIN_MCP=1`) e grava `roteiro.json` na execução.

### 2.1 Roteiro com fundamento (`issue147-cenarios@2`)

O `@1` não usa o campo `fundamento` da #292: pede a referência da evidência na justificativa. Assim a proveniência
sai da conversa e a omissão condicional da fonte (#289/#292) nunca é exercitada. Por isso a medição de eficiência usa
[`CenariosDaRetomada147/cenarios-fundamento.json`](../../../tests/Dante.Tests/CenariosDaRetomada147/cenarios-fundamento.json),
de SHA-256 `e4161af239b8d0f707f48876dcbf6f5f7a99f17e9f98cbbd96346f6c0fe7effe`. O hash é fixado em
`RoteiroComFundamento147Tests` e publicado na #147.

O `@1` continua intacto, com o mesmo hash, como pré-registro das retomadas de 15/11. A verificação delas segue
usando o `@1`.

**O que é igual ao `@1`** (um teste compara JSON a JSON): casos, Markdown, consultas B, durações, decisão literal,
separador e essenciais.

**O que muda:**
- **Captura do Incidente e da Solucao:**
  - é feita com `fundamento = { fonte: fonte:<id>@<rev>, citacao }`;
  - as citações são pré-registradas: o Incidente vai da primeira linha do documento até a linha do incidente; a Solucao,
    da linha `## Solução` (`inicioDaSolucao`) até a última linha;
  - os dois trechos cobrem o documento inteiro, sem lacuna nem sobreposição;
  - o conteúdo de cada candidato contém as linhas que ele cita, e o do Incidente termina com a linha do identificador;
  - o servidor calcula intervalos e hashes.
- **Decisão do usuário:** segue `Decisao/DitoPeloUsuario`, sem fundamento.
- **Snapshot:** referencia os registros sem recontá-los em `progresso`/`ultimo_resultado`, que podem ser omitidos
  (contrato da #292).
- **Essenciais pré-registrados por cenário** (`totaisDeEssenciais`): curto 18, médio 30, longo 48. O E2E exige
  exatamente esse conjunto.

**Consolidação condicional**
([`ConsolidacaoDaRetomada147`](../../../tests/Dante.Tests/ConsolidacaoDaRetomada147.cs)) é conferida de forma
independente do produto, só pelo estado exportado e pelo texto do pacote. Por caso:

- **Trechos íntegros:** o Incidente e a Solucao têm a referência canônica de trecho da própria fonte e revisão, com o
  hash do documento e exatamente o intervalo pré-registrado. O sha256 e o `TrechoDaFonte` são iguais ao texto do
  intervalo, e o conteúdo contém esse trecho.
- **Cobertura comprovada:** os dois trechos são íntegros e os dois conhecimentos estão no pacote.
- **Divergências:**
  - fonte omitida sem cobertura comprovada;
  - fonte integral com cobertura comprovada;
  - conteúdo que não é nem a íntegra nem o marcador de omissão;
  - `ConsolidadaEm` diferente dos dois conhecimentos.

O E2E (`@2`) e `VerificacaoDaRetomada147` exigem zero divergências.

**Montagem offline** (`RoteiroComFundamento147Tests`, sem CLI e sem medição): aplica os pedidos do `@2` à letra pelos
serviços de produção e reconstrói o pacote pela chamada da sessão B. Os pacotes e as estimativas abaixo são do código
desta PR; não são medições.

| Duração | Pacote | Itens | Essenciais | Fontes no pacote |
|---|---:|---:|---:|---|
| curto | 3.109 B | 5 | 18/18 | c1 omitida com prova |
| médio | 5.731 B | 9 | 28/30 (faltam `c1/c2.ref_fonte`) | c3 omitida com prova; c1 e c2 fora do pacote |
| longo | 6.105 B | 10 | 35/48 | nenhuma; o caso 2 e o incidente do caso 4 ficam fora |

No médio e no longo, a seleção do bootstrap (2048 tokens estimados) descarta fontes que seriam omitidas. O curto
exige 100%. O médio e o longo ficam registrados como dado, sem ajuste de critério: o E2E real continua exigindo 100% em
cada repetição.

## 3. Essenciais e integridade

**Essenciais do pacote** ([`EssenciaisDaRetomada147`](../../../tests/Dante.Tests/CenariosDaRetomada147.cs)): 18
unidades no caso 1 e 6 por caso seguinte. Cada unidade é uma lista de alternativas literais ou de referências
`conhecimento:<id>@<rev>` / `fonte:<id>@<rev>` resolvidas no estado. A presença é verificada no texto do pacote e nos
valores JSON decodificados. Presença é condição necessária, não suficiente.

**Critérios da resposta B por caso**: ficam em
[`CenariosDaRetomada147/criterios-resposta-b.json`](../../../tests/Dante.Tests/CenariosDaRetomada147/criterios-resposta-b.json)
(SHA-256 `f8bf27a2d9bef5c73d5aa5369152693dcfd26c0a38abd47277cae0ea5987b2e2`, fixado em teste). O arquivo é separado de
`cenarios.json`, cujo hash não muda.

- **Curto:** só os critérios históricos de `AvaliacaoDoHarness147`, inalterados. São eles: incidente, solução,
  restrição, próximo passo, marcador, as três distinções e as consultas de origem e de relação do caso 1.
- **Médio e longo, por caso:**
  - **Atribuição:** a resposta é lida por linha. Uma linha com o identificador exato do caso ou `#<PR>` pertence a esse
    caso; as linhas seguintes sem âncora continuam nele. Texto escrito sob outro caso não conta.
  - **Identificação:** o identificador e `#<PR>` do mesmo caso aparecem no mesmo parágrafo.
  - **Incidente e solução, casos 2–6:** grupos de termos normalizados, todos deriváveis do Markdown do próprio caso.
    Um teste prova que o texto literal de um caso atende os seus critérios e não os de nenhum outro, então trocar a
    solução entre casos reprova.
  - **Incidente e solução, caso 1:** os predicados históricos, aplicados ao texto atribuído ao caso 1.
  - **Origem e relação, casos 2–6:** o rastro de B precisa ter `brain_mostrar_origem` bem-sucedido com o Id do
    incidente do caso e a sessão `147A`. Precisa ter também `brain_listar_relacoes` bem-sucedido com os Ids do incidente
    e da solução do caso. Os Ids vêm do estado.
- Mencionar só o identificador e o número da PR não basta.

**Gates:** antes de reprovar, o E2E persiste todas as evidências, inclusive `roteiro.json` e
`criterios-resposta-b.json`. Depois exige, em cada repetição, as três famílias de critérios:

- os critérios históricos;
- 100% dos critérios por caso;
- 100% dos essenciais do pacote.

O conjunto de unidades é o exato esperado pela fixture: falta ou sobra de unidade também reprova. A média entre
repetições não compensa uma repetição reprovada.

**Verificação da execução** (`VerificacaoDaRetomada147`), antes de qualquer uso:

- **Evidência íntegra:** manifesto e `SHA256SUMS`; `roteiro.json` precisa ser igual ao pré-registrado.
- **Histórico e estado da mesma execução:**
  - pedidos de A iguais ao roteiro com o marcador da execução;
  - referências dos pedidos de snapshot existentes no estado;
  - `historico-visivel.txt` igual às entradas e respostas de A.
- **Nada confirmado indevidamente:** só a decisão do usuário é Confirmada; conclusões do agente ficam em Inferido.
- **Relações preservadas:**
  - por caso: uma fonte literal, um Incidente e uma Solução com o identificador do caso e um `ResolvidoPor` entre eles;
  - um snapshot que referencia todas as fontes e a decisão.
- **Estado inalterado pela sessão B.**
- **Pacote coerente:** `pacote.json` e `pacote-texto` são o mesmo pacote; a entrada B é o pacote seguido da consulta
  pré-registrada.

Os testes cobrem:

- as retomadas medidas: 0 divergências e 18/18 essenciais;
- evidência adulterada;
- com manifesto regravado de forma coerente: confirmação indevida, relação removida e estado de outra execução.

**Regressões dos gates** (`RespostaPorCaso147Tests`): um médio sintético coerente com o roteiro passa em todos os
gates. Cada um destes casos reprova:

- **pacote:** truncar a fonte e o incidente do caso 3, ou retirar apenas o commit do caso 2;
- **resposta B, solução:**
  - omitir a solução do caso 3;
  - pôr a solução do caso 3 no caso 2, ou trocar as soluções entre os dois;
  - escrever a solução do caso 1 sob o caso 2;
- **resposta B, identificação:**
  - dar só identificador e número da PR;
  - identificar um caso com a PR de outro;
- **rastro:** faltar a consulta de origem ou de relação de um caso posterior, ou a consulta retornar erro de aplicação.

## 4. Limitações

- **Montagem offline não é o agente real:** ela mostra o que o `@2` produz se cumprido à letra. A captura, os títulos e
  o snapshot do modelo real podem divergir, e só o E2E autorizado decide.
- **Médio e longo ainda não têm dados reais:** só existem roteiro, verificação e montagem offline. Histórico, estado e pacote nascem na primeira
  execução autorizada do E2E; só então podem ser verificados e medidos.
- **O curto restaurado reproduz o estado antes de B, não o banco inteiro:**
  - espaço e projeto não estavam no export e foram recriados com os mesmos Ids e nomes;
  - estado em memória do Worker e tabelas fora do export (métricas, trabalhos de embedding) não são restaurados.
  - A igualdade byte a byte do pacote mostra que isso não afeta a construção desse pacote. Não garante o mesmo para
    outras consultas.
- **O caso 1 não é citação literal:** seu Markdown é a fixture preexistente do harness. Foi mantido para comparabilidade.
- **Essenciais literais:** os do caso 1 foram verificados contra os pacotes de 15/11. Uma paráfrase do agente nos
  conhecimentos dos casos seguintes só conta pela referência do item, não pelo texto.
- **Critérios por termos:** os critérios da resposta B por caso são heurísticos (termos normalizados e atribuição por
  linha). Uma resposta com outra estrutura ou outro vocabulário pode ser reprovada, um falso negativo que é
  conservador. Termos presentes não provam interpretação correta. Por isso esses critérios se somam aos históricos e
  aos do pacote, sem substituí-los.
- **Longo:** o orçamento do bootstrap (2048 tokens estimados, 12 itens) deve truncar o pacote. O critério decisivo passa a
  ser a preservação dos essenciais, conforme o diagnóstico da #147.
