#!/usr/bin/env bash
# Tizen Notifier: build and install.
#   ./build.sh build   <tv>        -> out/TizNotify1-<tv>.tpk, signed, with that TV's token baked in
#   ./build.sh install <tv> <ip>   -> install out/TizNotify1-<tv>.tpk on the TV; launch it only if the install completed
# Settings: build.env next to this script (start from build.env.example). See "Build and install" in README.md.
#
# Sources and certificates are streamed into the containers on stdin (no bind mounts), so BUILD_DOCKER and
# INSTALL_DOCKER can be any command that runs docker, local or remote (e.g. "ssh buildhost docker").
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
usage() { sed -n 3,4p "$0" | sed 's/^# *//'; exit 2; }
[ -f "$HERE/build.env" ] && . "$HERE/build.env"

: "${CERT_DIR:=}"                                        # author.p12, distributor.p12, password, device-profile.xml
: "${TOKEN_FILE:=$HERE/secrets/token-{tv}}"             # per-TV token; created on first build
: "${ALLOW:=}"                                           # comma-separated hosts the TV may fetch from / call back to
: "${TIZEN5_TVS:=}"                                      # space-separated TV names that run Tizen 5
: "${HA_URL:=}"                                          # Home Assistant base URL for dashboards (optional)
: "${HA_TOKEN_FILE:=}"                                   # per-TV token of a non-admin HA user; {tv} = TV name
: "${RELAY_URL:=}"                                       # video relay base URL (relay/; optional)
: "${RELAY_TOKEN_FILE:=}"                                # the relay's token, if it has one
: "${BUILD_DOCKER:=docker}"
: "${SDK_IMAGE:=mcr.microsoft.com/dotnet/sdk:6.0}"
: "${TIZEN_TOOLS_IMAGE:=}"                               # empty = use sdb and tizen from this machine
: "${INSTALL_DOCKER:=docker}"
: "${TIZEN_STUDIO:=}"                                    # Tizen Studio dir holding tools/ (default: common locations)

cmd=${1:-}; tv=${2:-}
[ -n "$cmd" ] && [ -n "$tv" ] || usage
[[ "$tv" =~ ^[A-Za-z0-9_-]+$ ]] || { echo "tv name: letters, digits, _ and - only" >&2; exit 2; }
[ -n "$CERT_DIR" ] && [ -d "$CERT_DIR" ] || { echo "set CERT_DIR in build.env" >&2; exit 2; }
TPK=$HERE/out/TizNotify1-$tv.tpk

b64() { base64 < "$1" | tr -d '\n'; echo; }

case "$cmd" in
  build)
    tok=${TOKEN_FILE//\{tv\}/$tv}
    if [ ! -s "$tok" ]; then
      mkdir -p "$(dirname "$tok")"
      (umask 077; python3 -I -c 'import secrets; print(secrets.token_urlsafe(32))' > "$tok")
      echo "new token for $tv: $tok"
    fi
    tfm=tizen80
    for t in $TIZEN5_TVS; do [ "$t" = "$tv" ] && tfm=tizen50; done
    ver=$(sed -n 's/.*package="TizNotify1" version="\([^"]*\)".*/\1/p' "$HERE/src/tizen-manifest.xml")
    [ -n "$ver" ] || { echo "no version in src/tizen-manifest.xml" >&2; exit 1; }

    ST=$(mktemp -d "$HERE/.stage.XXXXXX"); chmod 700 "$ST"; trap 'rm -rf "$ST"' EXIT
    mkdir -m 700 "$ST/cert"
    cp "$CERT_DIR/author.p12" "$CERT_DIR/distributor.p12" "$CERT_DIR/password" "$ST/cert/"
    hatok=${HA_TOKEN_FILE//\{tv\}/$tv}
    if [ -n "$HA_URL" ] && [ ! -s "$hatok" ]; then echo "HA_URL is set but there is no HA token at $hatok" >&2; exit 2; fi
    python3 -I - "$tok" "$ALLOW" "$ST/config.json" "$HA_URL" "$hatok" "$RELAY_URL" "$RELAY_TOKEN_FILE" <<'PY'
import json, sys
tok, allow, out, ha_url, ha_tok, relay_url, relay_tok = sys.argv[1:]
cfg = {"token": open(tok).read().strip(), "port": 9123,
       "allow": [h.strip() for h in allow.split(",") if h.strip()]}
if ha_url:
    cfg["ha"] = {"url": ha_url.rstrip("/"), "token": open(ha_tok).read().strip()}
if relay_url:
    cfg["relay"] = {"url": relay_url.rstrip("/"), "token": open(relay_tok).read().strip() if relay_tok else ""}
json.dump(cfg, open(out, "w"))
PY
    COPYFILE_DISABLE=1 tar -czf "$ST/b.tgz" --exclude bin --exclude obj -C "$HERE" src -C "$ST" config.json cert

    echo "building $tv ($tfm, $ver)..." >&2
    mkdir -p "$HERE/out"
    {
      cat <<EOF
set -euo pipefail
W=\$(mktemp -d); cd "\$W"
base64 -d <<'__TGZ__' | tar xzf - 2>/dev/null
EOF
      b64 "$ST/b.tgz"
      cat <<EOF
__TGZ__
mkdir -p src/res && mv config.json src/res/config.json
mkdir -p /work && rm -rf /work/cert && mv cert /work/cert   # the csproj signs with /work/cert/*
if [ "$tfm" = tizen50 ]; then
  sed -i 's#<TargetFramework>tizen80</TargetFramework>#<TargetFramework>tizen50</TargetFramework><DefineConstants>\$(DefineConstants);TIZEN5</DefineConstants>#; s#Include="Tizen.NET" Version="8.0.0.\*"#Include="Tizen.NET" Version="5.0.0.*"#' src/TizenNotifier.csproj
  sed -i 's/api-version="6.0"/api-version="5.0"/' src/tizen-manifest.xml
fi
cd src
dotnet build -c Release -nologo -v minimal >&2 || { rm -rf /work/cert; exit 1; }
rm -rf /work/cert
cat bin/Release/$tfm/TizNotify1-$ver.tpk
EOF
    } | $BUILD_DOCKER run --rm -i -v tizen-notifier-nuget:/root/.nuget/packages "$SDK_IMAGE" bash -s > "$TPK.partial" \
      || { rm -f "$TPK.partial"; echo "BUILD FAILED" >&2; exit 1; }
    [ -s "$TPK.partial" ] || { rm -f "$TPK.partial"; echo "BUILD FAILED (no package)" >&2; exit 1; }
    mv "$TPK.partial" "$TPK"; chmod 600 "$TPK"
    ls -la "$TPK"
    ;;

  install)
    ip=${3:-}
    [[ "$ip" =~ ^[A-Za-z0-9.:-]+$ ]] || usage
    [ -s "$TPK" ] || { echo "no $TPK: run ./build.sh build $tv first" >&2; exit 1; }
    [ -s "$CERT_DIR/device-profile.xml" ] || { echo "no device-profile.xml in CERT_DIR" >&2; exit 1; }
    script=$(
      cat <<EOF
set -uo pipefail
for d in "$TIZEN_STUDIO" "\$HOME/tizen-studio" /home/developer/tizen-studio; do
  [ -n "\$d" ] && [ -d "\$d/tools" ] && PATH=\$PATH:\$d/tools:\$d/tools/ide/bin
done
command -v sdb >/dev/null && command -v tizen >/dev/null || { echo "sdb / tizen not found" >&2; exit 1; }
W=\$(mktemp -d); trap 'rm -rf "\$W"' EXIT; cd "\$W"
base64 -d > TizNotify1.tpk <<'__TPK__'
EOF
      b64 "$TPK"
      cat <<EOF
__TPK__
base64 -d > device-profile.xml <<'__DP__'
EOF
      b64 "$CERT_DIR/device-profile.xml"
      cat <<EOF
__DP__
sdb connect $ip >/dev/null; sleep 2
D=\$(sdb devices | awk 'NR>1 && \$1 ~ /^$ip:/ {print \$3}')
[ -n "\$D" ] || { echo "sdb could not connect to $ip (Developer Mode on, Host PC IP = this machine?)" >&2; exit 1; }
sdb -s $ip:26101 shell 0 mkdir -p /home/owner/share/tmp/sdk_tools >/dev/null
sdb -s $ip:26101 push device-profile.xml /home/owner/share/tmp/sdk_tools/device-profile.xml 2>&1 | tail -1
R=\$(tizen install -n TizNotify1.tpk -t "\$D" 2>&1); echo "\$R" | grep -iE 'install completed|fail|error'
rc=1
if echo "\$R" | grep -q 'install completed'; then rc=0; tizen run -p TizNotify1.notifier -t "\$D" 2>&1 | grep -iE 'launched|fail'; fi
sdb disconnect $ip >/dev/null
exit \$rc
EOF
    )
    if [ -n "$TIZEN_TOOLS_IMAGE" ]; then
      printf '%s\n' "$script" | $INSTALL_DOCKER run --rm -i --entrypoint bash "$TIZEN_TOOLS_IMAGE" -s
    else
      printf '%s\n' "$script" | bash -s
    fi
    ;;

  *) usage;;
esac
