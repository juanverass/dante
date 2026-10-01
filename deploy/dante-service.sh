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
    [ "$(ps -p 1 -o comm= 2>/dev/null)" = systemd ] ||
        fail "systemd não está ativo. No WSL, adicione [boot] systemd=true em /etc/wsl.conf e rode 'wsl --shutdown' no Windows."
    systemctl --user show-environment >/dev/null 2>&1 || fail "o gerenciador systemd do usuário não está acessível."
}

token_configured() { grep -Eq '^Telegram__BotToken=.+' "$ENV_FILE"; }

install_service() {
    require_user_systemd
    command -v dotnet >/dev/null || fail "dotnet não encontrado no PATH."

    # Publish beside the current build and swap, so a running service never sees a half-written app.
    local staging="$APP_DIR.new"
    rm -rf "$staging"
    dotnet publish "$REPO_ROOT/src/Dante.Worker/Dante.Worker.csproj" -c Release -o "$staging" --nologo
    systemctl --user stop "$SERVICE" 2>/dev/null || true
    rm -rf "$APP_DIR.old"
    [ ! -d "$APP_DIR" ] || mv "$APP_DIR" "$APP_DIR.old"
    mv "$staging" "$APP_DIR"
    rm -rf "$APP_DIR.old"

    mkdir -p "$CONFIG_DIR"
    chmod 700 "$CONFIG_DIR"
    if [ ! -f "$ENV_FILE" ] && [ -f "$CONFIG_DIR/env" ]; then
        # The manual setup keeps shell "export" lines in ~/.config/dante/env; systemd reads plain KEY=value.
        (umask 077 && sed -E 's/^[[:space:]]*export[[:space:]]+//' "$CONFIG_DIR/env" > "$ENV_FILE")
        echo "Criado $ENV_FILE a partir de $CONFIG_DIR/env, sem 'export'. Revise o conteúdo."
    elif [ ! -f "$ENV_FILE" ]; then
        install -m 600 "$REPO_ROOT/deploy/dante.env.example" "$ENV_FILE"
        echo "Criado $ENV_FILE (permissão 600). Preencha Telegram__BotToken e Telegram__AllowedUserIds."
    fi
    chmod 600 "$ENV_FILE"

    mkdir -p "$UNIT_DIR"
    install -m 644 "$REPO_ROOT/deploy/systemd/$SERVICE" "$UNIT_DIR/$SERVICE"
    systemctl --user daemon-reload
    systemctl --user enable "$SERVICE"

    # Linger starts the user's systemd (and the service) when the WSL distro boots, without a login shell.
    if [ "$(loginctl show-user "$(id -un)" -p Linger --value 2>/dev/null)" != yes ]; then
        loginctl enable-linger "$(id -un)" 2>/dev/null ||
            echo "aviso: não foi possível ativar o linger. Rode: sudo loginctl enable-linger $(id -un)" >&2
    fi

    if token_configured; then
        systemctl --user restart "$SERVICE"
        echo "Serviço $SERVICE ativo. Status: deploy/dante-service.sh status"
    else
        echo "Serviço instalado e habilitado, mas não iniciado: configure $ENV_FILE e rode deploy/dante-service.sh start"
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
        systemctl --user start "$SERVICE" ;;
    stop) systemctl --user stop "$SERVICE" ;;
    restart) systemctl --user restart "$SERVICE" ;;
    status) systemctl --user status "$SERVICE" --no-pager ;;
    logs) journalctl --user -u "$SERVICE" -f ;;
    *) echo "Uso: $0 install|uninstall|start|stop|restart|status|logs" >&2; exit 2 ;;
esac
