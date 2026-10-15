#!/usr/bin/env bash
#
# como / hazme — autocompilación NativeAOT para Debian, Ubuntu y derivadas.
#
#   curl -fsSL https://raw.githubusercontent.com/XWolfOverride/Como-y-Hazme/master/build.sh | bash
#
# Qué hace:
#   1. Comprueba que están instaladas las herramientas necesarias (NO instala nada).
#   2. Descarga (o actualiza) la última versión del código con git.
#   3. Compila `como` y `hazme` con NativeAOT.
#   4. Deja los binarios en la raíz del proyecto y te explica cómo instalarlos.
#
# Variables opcionales:
#   COMO_REPO_URL   URL del repositorio (por defecto, el oficial; útil para forks)

set -euo pipefail

REPO_URL="${COMO_REPO_URL:-https://github.com/XWolfOverride/Como-y-Hazme}"
DIR_NAME="Como-y-Hazme"

if [ -t 1 ]; then
    B=$'\e[1m'; G=$'\e[32m'; R=$'\e[31m'; Y=$'\e[33m'; C=$'\e[36m'; N=$'\e[0m'
else
    B=''; G=''; R=''; Y=''; C=''; N=''
fi

ok()   { printf '%s✔%s %s\n' "$G" "$N" "$*"; }
bad()  { printf '%s✖%s %s\n' "$R" "$N" "$*"; }
info() { printf '%s»%s %s\n' "$C" "$N" "$*"; }
die()  { printf '\n%s%s%s\n' "$R" "$*" "$N" >&2; exit 1; }

echo
echo "${B}como · hazme — autocompilación${N}"
echo

# ---------------------------------------------------------------- arquitectura
case "$(uname -s)" in
    Linux) ;;
    *) die "Este script es para Linux. En Windows usa build.ps1; en macOS consulta el README." ;;
esac

case "$(uname -m)" in
    x86_64|amd64)  RID="linux-x64" ;;
    aarch64|arm64) RID="linux-arm64" ;;
    *) die "Arquitectura no soportada por el script: $(uname -m)" ;;
esac
info "Sistema detectado: $RID"
echo

# ---------------------------------------------------------------- prerrequisitos
missing=()      # descripción de lo que falta
apt_pkgs=()     # paquetes apt que lo resolverían
manual=()       # lo que no se resuelve con apt

if command -v git >/dev/null 2>&1; then
    ok "git ($(git --version | awk '{print $3}'))"
else
    bad "git"; missing+=("git"); apt_pkgs+=("git")
fi

dotnet_ok=0
if command -v dotnet >/dev/null 2>&1; then
    dotnet_ver="$(dotnet --version 2>/dev/null || true)"
    dotnet_major="${dotnet_ver%%.*}"
    if [[ "$dotnet_major" =~ ^[0-9]+$ ]] && [ "$dotnet_major" -ge 10 ]; then
        ok "SDK de .NET ($dotnet_ver)"
        dotnet_ok=1
    else
        bad "SDK de .NET: versión ${dotnet_ver:-desconocida}, se necesita 10 o superior"
    fi
else
    bad "SDK de .NET 10"
fi
if [ "$dotnet_ok" -eq 0 ]; then
    missing+=("SDK de .NET 10"); manual+=("SDK de .NET 10 (ver README: instalación del SDK)")
fi

if command -v clang >/dev/null 2>&1; then
    ok "clang ($(clang --version | head -n1 | sed -E 's/.*version ([0-9.]+).*/\1/'))"
else
    bad "clang"; missing+=("clang"); apt_pkgs+=("clang")
fi

if command -v ld >/dev/null 2>&1; then
    ok "enlazador (ld)"
else
    bad "enlazador (ld)"; missing+=("binutils"); apt_pkgs+=("build-essential")
fi

have_zlib=0
if [ -f /usr/include/zlib.h ]; then
    have_zlib=1
elif command -v clang >/dev/null 2>&1 \
     && printf '#include <zlib.h>\nint main(void){return 0;}\n' | clang -x c -fsyntax-only - >/dev/null 2>&1; then
    have_zlib=1
fi
if [ "$have_zlib" -eq 1 ]; then
    ok "cabeceras de zlib"
else
    bad "cabeceras de zlib"; missing+=("zlib (cabeceras)"); apt_pkgs+=("zlib1g-dev")
fi

if [ "${#missing[@]}" -gt 0 ]; then
    echo
    printf '%sFaltan herramientas necesarias:%s %s\n' "$Y" "$N" "$(IFS=,; echo "${missing[*]}" | sed 's/,/, /g')"
    echo
    if [ "${#apt_pkgs[@]}" -gt 0 ]; then
        echo "Instálalas con (este script no instala nada por ti):"
        echo
        echo "    sudo apt update && sudo apt install -y ${apt_pkgs[*]}"
        echo
    fi
    for m in "${manual[@]}"; do
        echo "  · $m"
    done
    echo
    echo "Después vuelve a ejecutar este script. Detalles en el README del proyecto."
    exit 1
fi

# ---------------------------------------------------------------- código fuente
echo
if [ -f Como.slnx ] && [ -d .git ]; then
    ROOT="$PWD"
    info "Usando el repositorio actual: $ROOT"
    git -C "$ROOT" pull --ff-only || die "No se pudo actualizar (¿hay cambios locales?). Resuélvelo y repite."
elif [ -d "$DIR_NAME/.git" ]; then
    ROOT="$PWD/$DIR_NAME"
    info "Actualizando $ROOT"
    git -C "$ROOT" pull --ff-only || die "No se pudo actualizar $ROOT (¿hay cambios locales?). Resuélvelo o bórralo y repite."
else
    ROOT="$PWD/$DIR_NAME"
    info "Descargando $REPO_URL"
    git clone --depth 1 "$REPO_URL" "$ROOT" || die "No se pudo clonar el repositorio."
fi

# ---------------------------------------------------------------- compilación
export DOTNET_NOLOGO=1
export DOTNET_CLI_UI_LANGUAGE=en
rm -rf "$ROOT/publish"

# Evita que git vea los binarios como cambios, solo en este clon (sistema de archivos sensible a mayúsculas).
# No se hace en .gitignore: en Windows/macOS "/como" también ignoraría la carpeta "Como/".
exclude="$ROOT/.git/info/exclude"
if [ -d "$ROOT/.git" ]; then
    mkdir -p "$ROOT/.git/info"
    for f in /como /hazme /publish/; do
        grep -qxF "$f" "$exclude" 2>/dev/null || echo "$f" >> "$exclude"
    done
fi

build() {
    local project="$1" exe="$2"
    info "Compilando $exe (NativeAOT, $RID)... puede tardar un minuto"
    dotnet publish "$ROOT/$project/$project.csproj" -c Release -r "$RID" \
        -o "$ROOT/publish/$exe" -nologo -v q \
        || die "Falló la compilación de $exe."
    [ -f "$ROOT/publish/$exe/$exe" ] || die "No se encontró el binario $exe tras compilar."
    cp -f "$ROOT/publish/$exe/$exe" "$ROOT/$exe"
    chmod +x "$ROOT/$exe"
    ok "$exe → $ROOT/$exe"
}

build Como  como
build Hazme hazme
rm -rf "$ROOT/publish"

# ---------------------------------------------------------------- final
echo
echo "${B}${G}Listo.${N} Los binarios están en: ${B}$ROOT${N}"
ls -lh "$ROOT/como" "$ROOT/hazme" | awk '{printf "    %s  %s\n", $5, $9}'
echo
echo "${B}Para instalarlos${N} elige una opción (desde ${C}$ROOT${N}):"
echo
echo "  ${B}Solo para tu usuario${N} (sin sudo; asegúrate de que ~/.local/bin está en tu PATH):"
echo "    mkdir -p ~/.local/bin && install -m 755 como hazme ~/.local/bin/"
echo
echo "  ${B}Para todos los usuarios${N}:"
echo "    sudo install -m 755 como hazme /usr/local/bin/"
echo
echo "Comprueba con:  como -connectors"
echo "Después configura tu API key, por ejemplo:  como -set claude.api-key sk-ant-..."
echo
