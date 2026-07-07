#!/usr/bin/env bash
#
# install-dotnet.sh - set up a Linux machine for the Z+ apps.
#
#   (default)      Install the .NET 10 SDK (needed to build with `make`) plus the
#                  native libraries the self-contained binaries use at runtime.
#   --deps-only    Install ONLY those native libraries - enough to RUN the published
#                  self-contained binaries, without the SDK.
#   --help         Show this help.
#
# The published Z+ Linux binaries are self-contained (they bundle the .NET runtime),
# so to just run them you need only the native libraries. The .NET 10 SDK is required
# to build from source with the Makefile.
#
# Supports Debian/Ubuntu, Fedora/RHEL/CentOS, openSUSE, Arch and Alpine. On unknown
# distros the SDK is installed to ~/.dotnet via Microsoft's dotnet-install.sh.

set -euo pipefail

CHANNEL="10.0"
MODE="full"

for arg in "$@"; do
  case "$arg" in
    --deps-only) MODE="deps" ;;
    -h|--help)   sed -n '3,15p' "$0" | sed 's/^#\{0,1\} \{0,1\}//'; exit 0 ;;
    *) echo "Unknown option: $arg (try --help)" >&2; exit 2 ;;
  esac
done

log()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mwarning:\033[0m %s\n' "$*" >&2; }
have() { command -v "$1" >/dev/null 2>&1; }

# ---- distro detection --------------------------------------------------------
distro="unknown"; version=""; like=""
if [ -r /etc/os-release ]; then
  # shellcheck disable=SC1091
  . /etc/os-release
  distro="${ID:-unknown}"; version="${VERSION_ID:-}"; like="${ID_LIKE:-}"
fi

# ---- privilege escalation for package installs -------------------------------
SUDO=""
if [ "$(id -u)" -ne 0 ]; then
  if have sudo; then SUDO="sudo"; else warn "not root and no sudo found; package installs may fail"; fi
fi

# ---- native runtime dependencies --------------------------------------------
# ICU + OpenSSL + zlib for the runtime; X11 + fontconfig for the Avalonia GUIs.
install_native_deps() {
  log "Installing native libraries the Z+ binaries need at runtime..."
  if have apt-get; then
    $SUDO apt-get update -y
    $SUDO apt-get install -y --no-install-recommends \
      ca-certificates libicu-dev zlib1g libx11-6 libice6 libsm6 libfontconfig1
  elif have dnf; then
    $SUDO dnf install -y ca-certificates libicu openssl-libs zlib \
      libX11 libICE libSM fontconfig
  elif have yum; then
    $SUDO yum install -y ca-certificates libicu openssl-libs zlib \
      libX11 libICE libSM fontconfig
  elif have zypper; then
    $SUDO zypper --non-interactive install ca-certificates libicu libopenssl3 zlib \
      libX11-6 libICE6 libSM6 fontconfig
  elif have pacman; then
    $SUDO pacman -Sy --noconfirm --needed ca-certificates icu openssl zlib \
      libx11 libice libsm fontconfig
  elif have apk; then
    $SUDO apk add --no-cache ca-certificates icu-libs libgcc libstdc++ zlib \
      libx11 libice libsm fontconfig
  else
    warn "unknown package manager - install ICU, OpenSSL, zlib, libX11 and fontconfig yourself"
    return 1
  fi
}

# ---- .NET 10 SDK -------------------------------------------------------------
sdk_present() { have dotnet && dotnet --list-sdks 2>/dev/null | grep -qE "^${CHANNEL//./\\.}\."; }

install_sdk_pkg() {
  local pkg="dotnet-sdk-${CHANNEL}"
  if have apt-get; then
    $SUDO apt-get install -y "$pkg" && return 0
    log "Adding the Microsoft package feed..."
    local tmp; tmp="$(mktemp -d)"
    if curl -fsSL "https://packages.microsoft.com/config/${distro}/${version}/packages-microsoft-prod.deb" \
         -o "$tmp/ms.deb"; then
      $SUDO dpkg -i "$tmp/ms.deb" || true
      $SUDO apt-get update -y || true
      $SUDO apt-get install -y "$pkg" || true
    fi
    rm -rf "$tmp"
  elif have dnf; then
    $SUDO dnf install -y "$pkg" || true
  elif have zypper; then
    $SUDO zypper --non-interactive install "$pkg" || true
  elif have apk; then
    $SUDO apk add --no-cache "dotnet${CHANNEL%%.*}-sdk" || true
  fi
}

install_sdk_script() {
  log "Installing the .NET ${CHANNEL} SDK to \$HOME/.dotnet via dotnet-install.sh..."
  local tmp; tmp="$(mktemp)"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$tmp"
  bash "$tmp" --channel "$CHANNEL" --install-dir "$HOME/.dotnet"
  rm -f "$tmp"
  export PATH="$HOME/.dotnet:$PATH"
  local line='export PATH="$HOME/.dotnet:$PATH"'
  for rc in "$HOME/.bashrc" "$HOME/.profile"; do
    [ -e "$rc" ] || touch "$rc"
    grep -qF '.dotnet:$PATH' "$rc" 2>/dev/null || printf '\n%s\n' "$line" >> "$rc"
  done
  warn "Open a new shell (or 'source ~/.bashrc') so 'dotnet' is on your PATH."
}

# ---- run ---------------------------------------------------------------------
log "Detected: ${distro} ${version}"
install_native_deps || warn "native dependency install hit problems - continuing"

if [ "$MODE" = "deps" ]; then
  log "Done. The self-contained Z+ binaries can now run (chmod +x them first)."
  log "Re-run without --deps-only to also install the .NET SDK for building."
  exit 0
fi

if sdk_present; then
  log ".NET ${CHANNEL} SDK already installed: $(dotnet --version)"
else
  install_sdk_pkg || true
  sdk_present || install_sdk_script
fi

log "Done."
if have dotnet; then dotnet --version | sed 's/^/    .NET SDK /'; fi
log "Build the Linux apps with:  make"
