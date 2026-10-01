#!/usr/bin/env bash
# Valida e converte arquivos de ambiente do D.A.N.T.E. para o formato do systemd (EnvironmentFile).
# Uso: deploy/dante-env.sh check <arquivo>
#      deploy/dante-env.sh convert <origem> <destino>
#
# O systemd não usa shell: não expande $VAR, ~, `comando` nem escapes. Por isso só linhas literais são aceitas,
# e qualquer outra recusa o arquivo inteiro em vez de entregar ao Worker um valor diferente do que o shell daria.
# Mensagens citam apenas o número da linha, nunca o conteúdo, que pode ser segredo.
set -euo pipefail

name='[A-Za-z_][A-Za-z0-9_]*'
double_quoted='"[^"\\$`]*"'
single_quoted="'[^']*'"
bare='[^][:space:]"'"'"'\\$`~;&|<>()#]*'
assignment="^[[:space:]]*(export[[:space:]]+)?($name)=($double_quoted|$single_quoted|$bare)[[:space:]]*\$"
ignored='^[[:space:]]*(#.*)?$'

# Prints the systemd form of the file, or lists the unsupported lines on stderr and fails.
render() {
    local file="$1" line number=0 invalid=()
    local output=()
    while IFS= read -r line || [ -n "$line" ]; do
        number=$((number + 1))
        line="${line%$'\r'}"
        if [[ $line =~ $ignored ]]; then
            output+=("$line")
        elif [[ $line =~ $assignment ]]; then
            output+=("${BASH_REMATCH[2]}=${BASH_REMATCH[3]}")
        else
            invalid+=("$number")
        fi
    done < "$file"

    if [ ${#invalid[@]} -gt 0 ]; then
        echo "erro: $file tem linhas que o systemd não lê como o shell: ${invalid[*]}." >&2
        echo "Use CHAVE=valor literal, sem \$VAR, ~, \`comando\`, barra invertida ou comentário na mesma linha." >&2
        return 1
    fi
    [ ${#output[@]} -eq 0 ] || printf '%s\n' "${output[@]}"
}

case "${1:-}" in
    check)
        [ $# -eq 2 ] || { echo "Uso: $0 check <arquivo>" >&2; exit 2; }
        render "$2" > /dev/null ;;
    convert)
        [ $# -eq 3 ] || { echo "Uso: $0 convert <origem> <destino>" >&2; exit 2; }
        [ ! -e "$3" ] || { echo "erro: $3 já existe." >&2; exit 1; }
        temporary="$(mktemp "$(dirname "$3")/.dante-env.XXXXXX")"
        trap 'rm -f "$temporary"' EXIT
        chmod 600 "$temporary"
        render "$2" > "$temporary"
        mv "$temporary" "$3"
        trap - EXIT ;;
    *) echo "Uso: $0 check <arquivo> | convert <origem> <destino>" >&2; exit 2 ;;
esac
