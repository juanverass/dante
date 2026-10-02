# Spike: capacidades multimodais das CLIs e contrato de anexos (#93)

Validação, contra as versões instaladas, do que Claude Code e Codex aceitam como mídia nos caminhos que o
D.A.N.T.E. já usa (sessão interativa e one-shot), e do que a Bot API permite receber e enviar. Orienta as
filhas da Epic #92 (#94–#99). Nada aqui é código do Worker.

Validado em 2026-10-01 (WSL2, Linux):

```text
claude --version   2.1.287 (Claude Code)
codex --version    codex-cli 0.159.3   (login: ChatGPT)
Bot API            10.3 (changelog de 2026-08-24)
```

Ferramentas locais: `python3` 3.14 (sem `pip`/`venv` funcional), sem `ffmpeg`, `ffprobe`, `whisper`,
ImageMagick, Pillow, Node ou navegador headless. `python3-pil` e `ffmpeg` existem no apt, mas exigem `sudo`.

## Como reproduzir

Scripts auxiliares (Python 3, sem dependências), que consomem cota real das CLIs. As mídias são sintéticas:
fatos que o modelo só acerta vendo os pixels ou ouvindo a fala, sem dado pessoal.

```bash
S=<repo>/docs/spikes/multimodal
mkdir -p /tmp/mm/work && cd /tmp/mm/work && git init -q
python3 $S/make_fixtures.py /tmp/mm/media
# fala sintética para o teste de áudio (TTS do Windows, a partir do WSL):
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w $S/make_speech.ps1)" \
    -OutFile "$(wslpath -w /tmp/mm/media/speech.wav)"
python3 $S/claude_media_spike.py /tmp/mm/work /tmp/mm/media
python3 $S/codex_media_spike.py /tmp/mm/work /tmp/mm/media
```

O teste de áudio exige `speech.wav`, com a frase conhecida "A palavra secreta e girassol.". Sem ela, os probes
rodam o resto e imprimem `PULADO: speech.wav ausente` no turno de áudio. `tone.wav` é só um tom de 440 Hz, sem
fala, e não serve para avaliar transcrição. Fora do Windows, qualquer TTS que grave a mesma frase em WAV
substitui o `make_speech.ps1`.

Os one-shot foram exercitados com os mesmos argumentos de `ClaudeRunner`/`CodexRunner`, comandos na matriz.

## Matriz de capacidades

`✅` validado localmente · `❌` validado que não funciona · `—` não existe na interface · `doc` só documentação.

| Capacidade | Claude sessão (`stream-json`) | Claude one-shot (`--print`) | Codex sessão (`app-server`) | Codex one-shot (`exec`) |
| --- | --- | --- | --- | --- |
| imagem no turno | ✅ blocos `image` base64 no `content` | ✅ arquivo + ferramenta `Read`; fora do `cwd` só com `--add-dir` | ✅ itens `localImage` (path) | ✅ `-i <arquivo>` antes do `--` |
| várias imagens no turno | ✅ 2 blocos, descritas na ordem | ✅ (uma por `Read`) | ✅ 2 `localImage` | ✅ `-i a -i b` |
| imagem no steer | n/a: steer é interrupt + mensagem nova (AD-16), que leva blocos | — | ✅ `turn/steer` com `localImage` | — |
| imagem aberta pelo próprio agente | ✅ `Read` em `attachments/x.png` no `cwd`, sem pedido de aprovação em `manual` | ✅ | ✅ (`imageView`) | ✅ (`view_image`) |
| áudio | ❌ `Read` recusa binário; API sem entrada de áudio (doc) | ❌ idem | ❌ `localAudio` é aceito pelo protocolo, mas o modelo responde que a sessão não suporta áudio | — |
| vídeo | — (doc: só imagens/PDF) | — | — (sem tipo de entrada) | — |
| gerar imagem | — (doc: Claude não gera nem edita imagens) | — | ✅ item `imageGeneration` com `savedPath` | ✅ arquivo gravado, mas o caminho não aparece na saída nem no `--json` |
| gerar a partir de prints | — | — | ✅ `localImage` ×2 + pedido → composição nova | ✅ `-i` ×2 |

Comandos one-shot usados (iguais aos runners, com o acréscimo indicado):

```text
claude --print --permission-mode auto --permission-prompts none [--add-dir <anexos>] -- "<prompt>"
claude --print --restricted --strict-mcp-config --tools Read,Write,Edit --permission-mode auto
       --permission-prompts none [--add-dir <anexos>] -- "<prompt>"          (General)
codex exec --approve-for-me -i <img> [-i <img>] -- "<prompt>"
codex exec --sandbox workspace-write --skip-git-repo-check --ignore-user-config -i <img> -- "<prompt>"  (General)
```

Achados que viram regra:

- **Imagem funciona nos quatro caminhos sem mudar modo nem permissão.** Claude sessão recebe base64 no
  próprio `content` (hoje o driver manda `content` como string); Codex sessão recebe `localImage` com path
  absoluto (hoje o driver manda só `text`). O processo da CLI lê o arquivo, então o path pode ficar fora do
  `cwd` e da sandbox.
- **Claude one-shot precisa de `--add-dir <diretório do anexo>`.** Sem ele, o modo General (`--restricted`)
  recusa ler fora do workspace — o isolamento da AD-09 se mantém e o anexo não precisa ser gravado dentro do
  repositório. `--add-dir` libera só aquele diretório.
- **Áudio e vídeo não chegam ao modelo em nenhuma das CLIs.** O schema do `app-server` declara `localAudio`,
  mas o turno completa com o modelo dizendo que não ouve áudio: suporte não se infere pelo schema.
- **Geração de imagem existe só no Codex** (`image_generation` estável em `codex features list`), usa a
  autenticação ChatGPT já existente e consome a cota do plano (o schema prevê a falha
  `usageLimitExceeded`). O PNG é gravado pela própria CLI em
  `~/.codex/generated_images/<thread>/<id>.png`, **fora do workspace e inclusive no modo General** com
  `--ignore-user-config`. Na sessão, o caminho chega estruturado em `imageGeneration.savedPath`; no
  one-shot, não há evento com o caminho.
- **Geração redesenha, não copia.** Com dois prints sintéticos (um com 4 linhas de texto de terminal), o
  Codex produziu a composição com título e o texto idêntico, mas re-renderizado (fonte e proporções
  mudaram). Para prints reais e densos a fidelidade não é garantida.

Fora do escopo, observado: em `claude --print` sem `--restricted`, um aviso de conector do claude.ai não
autorizado ("O conector Google Drive do claude.ai precisa de autorização…") sai no stdout junto da resposta
— chega ao Telegram como parte da saída do job.

## Limites das plataformas

Bot API 10.3 (texto oficial, core.telegram.org/bots/api):

| Item | Limite |
| --- | --- |
| download por `getFile` | até 20 MB; link válido por pelo menos 1 hora |
| `sendPhoto` | até 10 MB; largura + altura ≤ 10000; proporção ≤ 20 |
| `sendDocument`, `sendVideo`, `sendAudio`, `sendAnimation` | até 50 MB por upload multipart |
| legenda | 0–1024 caracteres |
| álbum | mensagens com o mesmo `media_group_id`, cada item num update próprio; `sendMediaGroup` aceita 2–10 itens |
| campos de mídia em `Message` | `photo` (array de `PhotoSize`), `document`, `voice`, `audio`, `video` (com `thumbnail`), `video_note`, `animation`, `caption`, `media_group_id` |

Claude (doc oficial de Vision): JPEG, PNG, GIF (só o primeiro quadro) e WebP; até 10 MB por imagem em base64
na API direta; até 8000×8000 px, e 2000 px por lado quando a requisição passa de 20 imagens; requisição de até
32 MB; imagens maiores que o limite nativo do modelo são reduzidas. Áudio e vídeo não são entrada; Claude não
gera nem edita imagens.

## Contrato de anexos (proposto para as filhas)

### Entrada neutra

O `SessionRegistry` e os runners passam a receber texto opcional + anexos já baixados, sem tipos do Telegram:

```json
{
  "text": "monte uma imagem para o LinkedIn com estes dois prints",
  "attachments": [
    { "id": "A000012", "kind": "image", "mediaType": "image/png", "path": "/home/u/.dante/attachments/123/S000004/A000012.png",
      "bytes": 482113, "width": 1170, "height": 2532, "name": "print1.png" },
    { "id": "A000013", "kind": "image", "mediaType": "image/jpeg", "path": "/home/u/.dante/attachments/123/S000004/A000013.jpg",
      "bytes": 201554, "width": 1280, "height": 960, "name": null }
  ]
}
```

`kind`: `image | audio | video | document`. Só `image` chega ao agente nesta Epic sem processamento; os
demais dependem da #96. Tradução por driver, validada acima:

| Destino | Tradução |
| --- | --- |
| Claude sessão | `content: [{text: "Imagem 1 (print1.png):"}, {image base64}, …, {text: <texto>}]` |
| Codex sessão (`turn/start` e `turn/steer`) | `input: [{text}, {localImage: path}, …]` |
| Claude one-shot | `--add-dir <dir do job>` e o prompt lista os paths absolutos dos anexos |
| Codex one-shot | `-i <path>` por imagem, antes do `--` |

### Saída (artefatos)

Artefato só existe por canal explícito, nunca por varredura do workspace nem por path citado na prosa:

1. evento estruturado da CLI: `imageGeneration.savedPath` (Codex sessão) vira `ArtifactProducedEvent`, entregue
   ao dono como foto (≤ 10 MB) ou documento (≤ 50 MB);
2. pedido explícito do usuário (proposta: `/send <caminho>`), aceito só para arquivo regular dentro do diretório
   da sessão (sem `..`, sem symlink saindo dele), ≤ 50 MB.

Como o one-shot do Codex não informa o caminho da imagem gerada, geração fica restrita às sessões.

### Semântica no Telegram

| Situação | Comportamento proposto |
| --- | --- |
| foto/print com legenda | um turno: legenda = texto, imagem = anexo (mesma regra das mensagens comuns: sessão ativa ou abertura implícita, AD-23) |
| legenda `/claude [@alias] …` ou `/codex …` | one-shot com o anexo; outra legenda iniciada por `/` é recusada como comando desconhecido |
| foto sem legenda | guardada como **pendente** do usuário no contexto atual: `Recebi 1 imagem. Envie o pedido.`; não inicia agente |
| prints em mensagens consecutivas | acumulam nos pendentes (até 10) |
| álbum (`media_group_id`) | itens agrupados (janela curta após o último item); a legenda vem num dos itens; vira um único turno ou um único lote pendente |
| próximo texto | consome todos os pendentes num único turno |
| troca de contexto (`/use`, `/session start/select/close`, `/agent set`) ou 10 min sem pedido | pendentes descartados com aviso; nunca migram entre sessões, contextos ou usuários |
| durante um turno | a mensagem com anexos entra na fila FIFO como um item só (AD-16); a ordem dos anexos é preservada |
| sessão com segredos vinculados | anexos aceitos; a saída continua omitida (AD-10) |
| áudio, voz, vídeo, video note | recusa explícita até a #96 ("ainda não processo áudio/vídeo"), nunca descarte silencioso |
| formato fora de JPEG/PNG/GIF/WebP, ou acima dos limites | recusa com o limite na mensagem |

Prints enviados como **arquivo** (`document` com `image/*`) são aceitos como imagem e preservam a qualidade;
`photo` vem recomprimido pelo Telegram (usa-se o maior `PhotoSize`).

### Limites e retenção

- por anexo: ≤ 20 MB (teto do `getFile`) e, para imagem, ≤ 7 MB em disco (cabe nos 10 MB em base64 do Claude),
  ≤ 8000 px por lado;
- por turno: ≤ 10 imagens e ≤ 20 MB somados (abaixo dos 32 MB da requisição do Claude em base64);
- armazenamento fora do checkout: `~/.dante/attachments/<telegramUserId>/<sessão|job>/`, diretórios `700`,
  arquivos `600`, nome gerado pelo D.A.N.T.E. (o nome original é só metadado);
- retenção: apagados ao encerrar a sessão ou ao terminar o job; pendentes expiram em 10 min; na inicialização,
  sobras com mais de 24 h são removidas (o registro de anexos é em memória, como jobs e sessões — AD-06);
- logs e `/status` mostram contagem e tamanho, nunca conteúdo nem `file_id`.

## Áudio e vídeo (#96)

Nenhuma CLI entrega áudio ou vídeo ao modelo. Caminhos possíveis, todos fora do que está instalado:

| Opção | O que exige | Custo |
| --- | --- | --- |
| transcrição local (whisper.cpp ou equivalente) | compilar/instalar e baixar um modelo (centenas de MB) | grátis, CPU local |
| API de transcrição externa | credencial nova e serviço pago | pago |
| vídeo por quadros | `ffmpeg` (apt, `sudo`) para extrair quadros como imagens | grátis |
| vídeo pela miniatura | `video.thumbnail` do Telegram, já disponível | grátis, mas é um único quadro |

**Decisão humana necessária** antes da #96: qual ferramenta instalar ou contratar. Sem ela, áudio e vídeo
são recusados explicitamente.

Decisão (2026-10-02, #96): transcrição local com `whisper.cpp` (modelo `small`) e quadros pelo `ffmpeg`, instalados
pelo mantenedor; sem API paga. Implementação e limites em AD-29.

## Caso LinkedIn (#98)

Duas rotas viáveis, com trade-offs diferentes:

| Rota | Disponível hoje? | Fidelidade aos prints | Custo/dependência |
| --- | --- | --- | --- |
| A. geração pelo Codex (`imageGeneration` a partir de `localImage`) | sim, validada | redesenha; texto preservado no teste sintético, sem garantia em prints reais | cota do plano ChatGPT já autenticado |
| B. montagem determinística (prints colados pixel a pixel + fundo/título) | não | exata | biblioteca de imagem: `python3-pil` via apt (`sudo`) ou pacote .NET novo — decisão humana |

**Decisão humana necessária** antes da #98: aceitar a rota A como está (com aviso de que a imagem é
redesenhada) ou autorizar a dependência da rota B. Nenhum serviço pago novo é usado em nenhuma das duas.
