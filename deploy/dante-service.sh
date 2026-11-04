#!/usr/bin/env bash
# Instala e opera o D.A.N.T.E. como serviço do usuário (systemd --user) no WSL/Linux.
# Uso: deploy/dante-service.sh install|uninstall|start|stop|restart|status|logs
set -euo pipefail

SERVICE=dante.service
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_DIR="$HOME/.local/share/dante/app"
CONFIG_DIR="$HOME/.config/dante"
ENV_FILE="$CONFIG_DIR/dante.env"
UNIT_DIR="$HOME/.config/systemd/user"

fail() { echo "erro: $*" >&2; exit 1; }

require_user_systemd() {
    [ "$(id -u)" -ne 0 ] || fail "execute como o seu usuário, não como root: o serviço usa as credenciais locais das CLIs."
    systemctl --user show-environment >/dev/null 2>&1 && return
    [ "$(ps -p 1 -o comm= 2>/dev/null)" = systemd ] ||
        fail "systemd não está ativo. No WSL, adicione [boot] systemd=true em /etc/wsl.conf e rode 'wsl --shutdown' no Windows."
    fail "o gerenciador systemd do usuário não está acessível."
}

token_configured() { grep -Eq '^Telegram__BotToken=("[^"]+"|'"'"'[^'"'"']+'"'"'|[^"'"'"'].*)$' "$ENV_FILE"; }

# The environment file must exist in systemd's literal format before anything is installed or started.
prepare_environment() {
    mkdir -p "$CONFIG_DIR"
    chmod 700 "$CONFIG_DIR"
    if [ ! -f "$ENV_FILE" ] && [ -f "$CONFIG_DIR/env" ]; then
        # The manual setup keeps shell "export" lines in ~/.config/dante/env; only literal values convert unchanged.
        bash "$REPO_ROOT/deploy/dante-env.sh" convert "$CONFIG_DIR/env" "$ENV_FILE" ||
            fail "$CONFIG_DIR/env não pode ser convertido sem mudar valores. Crie $ENV_FILE a partir de deploy/dante.env.example com valores literais e rode install novamente."
        echo "Criado $ENV_FILE a partir de $CONFIG_DIR/env, sem 'export'."
    elif [ ! -f "$ENV_FILE" ]; then
        install -m 600 "$REPO_ROOT/deploy/dante.env.example" "$ENV_FILE"
        echo "Criado $ENV_FILE (permissão 600)."
    fi
    chmod 600 "$ENV_FILE"
    bash "$REPO_ROOT/deploy/dante-env.sh" check "$ENV_FILE" ||
        fail "corrija $ENV_FILE e rode install novamente."
}

# Subconjunto conservador: nunca tenta adivinhar resets, quoting, escapes ou continuações.
# O parser do systemd aceita mais sintaxe; uma unidade fora deste subconjunto exige revisão manual.
environment_file_sequence() {
    LC_ALL=C awk '
        function recusar() { exit 2 }
        {
            sub(/\r$/, "")
            line=$0
            sub(/^[[:space:]]+/, "", line); sub(/[[:space:]]+$/, "", line)
            if (line == "" || line ~ /^[#;]/) next
            if (index(line, "\\") != 0) recusar()
            if (line ~ /^\[[A-Za-z][A-Za-z0-9]*\]$/) { section=line; next }
            if (line !~ /^[A-Za-z][A-Za-z0-9]*[[:space:]]*=/) recusar()
            equal=index(line, "=")
            key=substr(line, 1, equal-1); sub(/[[:space:]]+$/, "", key)
            if (key != "EnvironmentFile") next
            if (section != "[Service]") recusar()
            value=substr(line, equal+1); sub(/^[[:space:]]+/, "", value)
            # Reset ou formato não interpretado com segurança: recusar, nunca descartar.
            if (value == "" || value !~ /^-?(\/|%h\/)[A-Za-z0-9_.\/%:@+\-]+$/) recusar()
            print value
        }
    ' "$1"
}

check_installed_environment_files() {
    [ -f "$UNIT_DIR/$SERVICE" ] || return 0
    local installed_files template_files
    installed_files="$(environment_file_sequence "$UNIT_DIR/$SERVICE")" &&
        template_files="$(environment_file_sequence "$REPO_ROOT/deploy/systemd/$SERVICE")" ||
        fail "não foi possível interpretar com segurança EnvironmentFile. Revise a migração para um drop-in; nenhum arquivo foi alterado."
    [ "$installed_files" = "$template_files" ] ||
        fail "unidade instalada tem sequência EnvironmentFile diferente do template. Revise a migração para um drop-in antes de instalar; nenhum arquivo foi alterado."
}

install_service() {
    require_user_systemd
    command -v dotnet >/dev/null || fail "dotnet não encontrado no PATH."
    check_installed_environment_files
    prepare_environment

    # Publish beside the current build and swap, so a running service never sees a half-written app.
    local staging="$APP_DIR.new"
    rm -rf "$staging"
    dotnet publish "$REPO_ROOT/src/Dante.Worker/Dante.Worker.csproj" -c Release -o "$staging" --nologo
    systemctl --user stop "$SERVICE" 2>/dev/null || true
    rm -rf "$APP_DIR.old"
    [ ! -d "$APP_DIR" ] || mv "$APP_DIR" "$APP_DIR.old"
    mv "$staging" "$APP_DIR"
    rm -rf "$APP_DIR.old"

    mkdir -p "$UNIT_DIR"
    # Não copia exemplos nem altera $UNIT_DIR/$SERVICE.d: complementos são do operador.
    install -m 644 "$REPO_ROOT/deploy/systemd/$SERVICE" "$UNIT_DIR/$SERVICE"
    systemctl --user daemon-reload

    # Linger starts the user's systemd (and the service) when the WSL distro boots, without a login shell.
    if [ "$(loginctl show-user "$(id -un)" -p Linger --value 2>/dev/null)" != yes ]; then
        loginctl enable-linger "$(id -un)" 2>/dev/null ||
            echo "aviso: não foi possível ativar o linger. Rode: sudo loginctl enable-linger $(id -un)" >&2
    fi

    # Only a configured service is enabled, so a boot never starts the Worker without its token.
    if token_configured; then
        systemctl --user enable "$SERVICE"
        systemctl --user restart "$SERVICE"
        echo "Serviço $SERVICE habilitado e ativo. Status: deploy/dante-service.sh status"
    else
        systemctl --user disable "$SERVICE" 2>/dev/null || true
        echo "Serviço instalado, mas não habilitado: preencha Telegram__BotToken e Telegram__AllowedUserIds em $ENV_FILE e rode install novamente."
    fi

    echo
    echo "Para iniciar o WSL junto com o Windows, rode no PowerShell (Windows), a partir do repositório:"
    echo "  powershell -ExecutionPolicy Bypass -File deploy\\windows\\Register-DanteAutostart.ps1 -Distro ${WSL_DISTRO_NAME:-<distro>}"
}

uninstall_service() {
    require_user_systemd
    systemctl --user disable --now "$SERVICE" 2>/dev/null || true
    rm -f "$UNIT_DIR/$SERVICE"
    systemctl --user daemon-reload
    rm -rf "$APP_DIR" "$APP_DIR.new" "$APP_DIR.old"
    echo "Serviço removido. Mantidos: $ENV_FILE (apague-o se não for mais usar) e ~/.dante (settings e repositórios)."
    echo "O linger continua ativo; para desativá-lo: loginctl disable-linger $(id -un)"
    echo "No Windows, remova a inicialização automática com Register-DanteAutostart.ps1 -Unregister."
}

case "${1:-}" in
    install) install_service ;;
    uninstall) uninstall_service ;;
    start)
        [ -f "$ENV_FILE" ] && token_configured || fail "configure Telegram__BotToken em $ENV_FILE antes de iniciar."
        bash "$REPO_ROOT/deploy/dante-env.sh" check "$ENV_FILE" || fail "corrija $ENV_FILE antes de iniciar."
        systemctl --user start "$SERVICE" ;;
    stop) systemctl --user stop "$SERVICE" ;;
    restart) systemctl --user restart "$SERVICE" ;;
    status) systemctl --user status "$SERVICE" --no-pager ;;
    logs) journalctl --user -u "$SERVICE" -f ;;
    *) echo "Uso: $0 install|uninstall|start|stop|restart|status|logs" >&2; exit 2 ;;
esac
