# Instalação local, do clone ao primeiro pedido

Este roteiro usa **Ubuntu/Linux ou Ubuntu no WSL2**, no mesmo usuário que executará
as CLIs e o D.A.N.T.E. No Windows, instale o WSL com `wsl --install` no PowerShell,
reinicie se solicitado e abra o Ubuntu para criar seu usuário Linux. Veja a
[instalação oficial do WSL](https://learn.microsoft.com/windows/wsl/install).

O README também apresenta execução manual em PowerShell. A instalação automática
como serviço deste repositório usa systemd; `/vitrine` procura fontes em caminhos
Linux. Para reproduzir o ambiente validado, siga este roteiro no Linux/WSL.

## 1. Dependências básicas e .NET

No terminal Linux:

```bash
sudo apt update
sudo apt install git curl ca-certificates python3
```

Instale o **SDK .NET 10**, que inclui o runtime. Use as
[instruções oficiais para sua distribuição](https://learn.microsoft.com/dotnet/core/install/linux).
Se usar a instalação por usuário da Microsoft:

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dante-dotnet-install.sh
bash /tmp/dante-dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.local/bin:$PATH"
dotnet --list-sdks
```

A saída deve incluir um SDK `10.0.*`. A instalação por script pode exigir bibliotecas
nativas da distribuição: consulte os
[pré-requisitos da Microsoft](https://learn.microsoft.com/dotnet/core/install/linux-scripted-manual).
Para persistir o PATH do terminal, adicione os dois `export` acima ao `~/.bashrc`.
O serviço tem seu próprio ambiente, configurado na etapa 6.

## 2. Instalar e autenticar pelo menos um agente

Instale as ferramentas **dentro do Linux/WSL**, sem usar `sudo` para iniciar os
agentes. Login feito no Windows não substitui o login no usuário Linux.

**Claude Code**, pelo [instalador oficial](https://code.claude.com/docs/en/setup):

```bash
curl -fsSL https://claude.ai/install.sh | bash
export PATH="$HOME/.local/bin:$PATH"
claude --version
claude
```

Conclua o login apresentado pela CLI (`/login`, se necessário), faça um pedido
simples e saia com `/exit`.

**Codex**, conforme a [documentação oficial](https://developers.openai.com/codex/cli/):
instale Node.js LTS com npm seguindo o [site do Node.js](https://nodejs.org/en/download),
no mesmo ambiente Linux, e então:

```bash
node --version
npm --version
npm install -g @openai/codex
codex --version
codex login
codex
```

Faça um pedido simples e saia com `/exit`. Se o npm acusar permissão, use uma
instalação de Node por usuário; não autentique a CLI como root. Se usar NVM, inclua
os caminhos de `node` e `codex` no PATH do serviço (etapa 6).

Não é necessária API key se o login da CLI já oferece acesso ao agente. A conta
precisa ter acesso ao produto escolhido. O D.A.N.T.E. não fornece assinatura nem
credenciais. As versões com evidência real registrada são Claude Code **2.1.287**,
Codex CLI **0.159.3**, ffmpeg **8.0.1** e whisper.cpp **1.8.3**; são versões
validadas, não um requisito mínimo inferido. Consulte [CURRENT_STATE](../context/CURRENT_STATE.md)
para capacidades e limitações de protocolo.

## 3. Baixar o projeto e configurar o Telegram

```bash
git clone https://github.com/juanverass/dante.git
cd dante
```

Se o GitHub solicitar autenticação, use a conta à qual o repositório foi liberado.
Não coloque tokens do GitHub na URL do clone.

No Telegram, abra o **[@BotFather](https://t.me/BotFather)**, envie `/newbot`,
escolha nome e username e guarde o token fornecido. Abra a conversa com seu novo
bot, clique em **Iniciar** e envie uma mensagem.

Antes de iniciar qualquer Worker, configure o token no terminal Linux. Para não
salvá-lo no histórico do shell:

```bash
read -r -s -p 'Token do BotFather: ' Telegram__BotToken
printf '\n'
export Telegram__BotToken
```

Descubra seu **User ID**, sem depender de outro bot, consultando
[`getUpdates`](https://core.telegram.org/bots/api#getupdates). Mantenha o Worker e
o serviço parados durante esta consulta. Ela imprime somente IDs de remetentes:

```bash
python3 - <<'PY'
import json
import os
import urllib.request

try:
    token = os.environ['Telegram__BotToken']
    with urllib.request.urlopen('https://api.telegram.org/bot' + token + '/getUpdates', timeout=20) as response:
        result = json.load(response)
    if not result.get('ok'):
        raise ValueError('Telegram recusou a consulta')
    ids = {update['message']['from']['id'] for update in result['result']
           if 'message' in update and 'from' in update['message']}
    print('\n'.join(str(user_id) for user_id in sorted(ids)) or
          'Nenhuma mensagem: envie um texto ao bot e repita a consulta.')
except Exception:
    raise SystemExit('Consulta falhou. Confira token, internet e se o Worker está parado.')
PY
export Telegram__AllowedUserIds="123456789"
```

Substitua o número pelo seu ID. Faça a consulta com mensagem enviada por você;
se houver vários remetentes, autorize apenas os IDs que você reconhece. A allowlist
usa `message.from.id`, não username nem ID de grupo. Sem allowlist válida o bot
bloqueia comandos. Sem token o processo pode subir, mas o polling fica desativado.

## 4. Build e primeiro teste manual

Na raiz do clone, com as variáveis da etapa anterior no mesmo terminal:

```bash
dotnet build Dante.sln
dotnet test Dante.sln
dotnet run --project src/Dante.Worker
```

A restauração de pacotes precisa de acesso ao NuGet. O serviço também precisa de
internet para o Telegram e para os agentes; usa long polling, sem porta pública,
webhook ou domínio. A suíte comum usa CLIs simuladas; testes com ferramentas/CLIs
reais são opt-in e aparecem como pulados normalmente.

No Telegram, envie `/ping` e espere `pong`. Se instalou **só Codex**, envie
`/agent set codex` antes da primeira mensagem comum: o agente padrão é Claude.
Envie `/use general`, depois `Responda apenas: funcionando`, e consulte `/status`.
`/ping` verifica o bot; a resposta ao pedido verifica também a CLI e seu login.

`Ctrl+C` encerra a execução manual. `export` dura apenas naquele terminal. Para
persistir a configuração manual, consulte o
[manual operacional](../maintainer/OPERATIONS_AND_DEBUGGING.md#4-configuração-para-execução-manual-no-wsl).

## 5. Dependências opcionais de mídia

| Funcionalidade | Dependências locais |
| --- | --- |
| Texto e imagens recebidas | CLI com suporte; sem ffmpeg/whisper |
| Quadros de vídeo | `ffmpeg` e `ffprobe` |
| Voz, áudio e transcrição da trilha do vídeo | `ffmpeg`, `ffprobe`, `whisper-cli` e modelo multilíngue |
| `/vitrine` | `ffmpeg` com filtro `drawtext` e fontes Inter ou DejaVu Sans |

Para ffmpeg e fontes no Ubuntu:

```bash
sudo apt install ffmpeg fonts-dejavu-core
ffmpeg -version
ffprobe -version
ffmpeg -hide_banner -filters | rg drawtext
```

Se não tiver `rg`, use `grep drawtext` no último comando. Inter é opcional:
`sudo apt install fonts-inter`. O renderer procura os pares regular/negrito em
`/usr/share/fonts/opentype/inter/Inter-{Regular,Bold}.otf` ou
`/usr/share/fonts/truetype/dejavu/DejaVuSans{,-Bold}.ttf`. Instalar uma fonte em outro
local não basta para o renderer atual.

Instale o pacote `whisper.cpp` **se disponível** na sua distribuição:

```bash
sudo apt install whisper.cpp
whisper-cli -h
```

Se o pacote não existir, compile a versão validada do
[projeto oficial whisper.cpp](https://github.com/ggml-org/whisper.cpp/tree/v1.8.3)
fora do clone do D.A.N.T.E.:

```bash
sudo apt install build-essential cmake
mkdir -p "$HOME/.local/src"
git clone --branch v1.8.3 --depth 1 https://github.com/ggml-org/whisper.cpp.git "$HOME/.local/src/whisper.cpp"
cmake -S "$HOME/.local/src/whisper.cpp" -B "$HOME/.local/src/whisper.cpp/build" -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF
cmake --build "$HOME/.local/src/whisper.cpp/build" --config Release -j 2
mkdir -p "$HOME/.local/bin"
ln -s "$HOME/.local/src/whisper.cpp/build/bin/whisper-cli" "$HOME/.local/bin/whisper-cli"
export PATH="$HOME/.local/bin:$PATH"
whisper-cli -h
```

O binário compilado fica no diretório de origem: não apague esse diretório depois
de criar o link. Não são necessários Python/pip, CUDA ou GPU para transcrever com
este caminho; Python acima serve apenas para consultar o ID do Telegram.

Baixe o modelo `small` **multilíngue** (`small.en` é específico para inglês):

```bash
mkdir -p "$HOME/.dante/models"
curl -fL --retry 3 -o "$HOME/.dante/models/ggml-small.bin" \
  https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin
test -s "$HOME/.dante/models/ggml-small.bin"
```

É um download de aproximadamente 466 MiB. Se usar outro local, defina
`DANTE_WHISPER_MODEL` com o **caminho absoluto** no terminal e no ambiente do serviço.
A presença do arquivo não comprova que o download está íntegro: valide enviando uma
mensagem de voz curta ao bot e pedindo a transcrição. Envie um vídeo curto para
validar quadros e, se houver fala, a transcrição. Para `/vitrine`, envie um print e
`/vitrine Monte uma apresentação deste print`.

Sem whisper/modelo, vídeos ainda podem fornecer quadros; voz/áudio são recusados.
O D.A.N.T.E. não instala ferramentas nem baixa modelos automaticamente.

## 6. Rodar sem terminal aberto

No WSL, confira `ps -p 1 -o comm=`: deve mostrar `systemd`. Se necessário, preserve
as outras configurações e adicione a `/etc/wsl.conf`:

```ini
[boot]
systemd=true
```

Execute `wsl --shutdown` **no PowerShell do Windows** e reabra a distro. Esse
comando encerra as distros WSL em execução. Confira `systemctl --user status`.
Siga então [Executando como serviço](../../README.md#executando-como-serviço):
pare o Worker manual, execute `deploy/dante-service.sh install`, preencha
`~/.config/dante/dante.env` com token e allowlist e execute `install` novamente.
Depois registre a tarefa de logon do Windows indicada no README.

O serviço **não carrega `~/.bashrc` nem NVM**. Seu PATH padrão inclui
`~/.local/bin`, `~/.dotnet`, `/usr/local/bin` e `/usr/bin`, mas não o diretório
versionado do NVM. Confira no terminal `command -v dotnet node codex claude
ffmpeg ffprobe whisper-cli` (os opcionais podem estar ausentes) e inclua os
diretórios necessários em `dante.env`, com valores literais. Exemplo, substituindo
`ana` e a versão de Node pelos seus caminhos reais:

```text
PATH=/home/ana/.nvm/versions/node/v22.20.0/bin:/home/ana/.local/bin:/home/ana/.dotnet:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
DOTNET_ROOT=/home/ana/.dotnet
DANTE_WHISPER_MODEL=/home/ana/.dante/models/ggml-small.bin
```

Não use `$HOME`, `$PATH`, `~` ou `export` nesse arquivo. `DOTNET_ROOT` se aplica à
instalação por usuário; `DANTE_WHISPER_MODEL` é opcional no caminho padrão. Ao
trocar a versão do Node/NVM, atualize também o PATH literal. Valide e reinicie:

```bash
bash deploy/dante-env.sh check "$HOME/.config/dante/dante.env"
systemctl --user restart dante
systemctl --user status dante --no-pager
journalctl --user -u dante -n 30 --no-pager
```

Repita `/ping`, um pedido ao agente e os testes de mídia que pretende usar, agora
com o serviço. Não rode duas instâncias com o mesmo token. O arquivo guarda
segredos, deve permanecer com permissão `600` e fora do Git.

## Diagnóstico rápido

| Sintoma | Conferir |
| --- | --- |
| Processo sobe, `/ping` não responde | Token, allowlist, internet, mensagem enviada ao bot correto, instância duplicada |
| `/ping` funciona, conversa falha | Agente escolhido, login no usuário Linux, versão da CLI, PATH |
| Funciona no terminal, falha como serviço | `dante.env`, PATH literal (Node/NVM inclusive), `DOTNET_ROOT`, modelo e logs |
| Voz recusada | `ffmpeg`, `ffprobe`, `whisper-cli` e modelo acessíveis ao serviço |
| Vídeo mostra quadros sem fala | Whisper/modelo ausente ou vídeo sem trilha de áudio |
| `/vitrine` falha | Filtro `drawtext`, arquivos de fonte nos caminhos suportados, ffmpeg no PATH |

Para atualizar o projeto, na branch `main`, use `git pull --ff-only` e
`deploy/dante-service.sh install`. Isso republica e reinicia o Worker: sessões em
memória não sobrevivem ao reinício. Faça novamente os testes básicos pelo Telegram.
