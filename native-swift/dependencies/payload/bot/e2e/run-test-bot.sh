#!/usr/bin/env bash
# Launch an isolated V2 test bot without modifying the user's runtime state.
set -euo pipefail

supported_schemes="socks, socks4, socks4a, socks5, socks5h, http, https"
skip_build=0
fault_proxy=0
forward_proxy=""
opencode_version=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --skip-build) skip_build=1 ;;
    --fault-proxy) fault_proxy=1 ;;
    --forward-proxy)
      [ "$#" -ge 2 ] || { echo "--forward-proxy needs a scheme. Supported: $supported_schemes." >&2; exit 2; }
      forward_proxy="$2"
      shift
      ;;
    --opencode-version)
      [ "$#" -ge 2 ] || { echo '--opencode-version needs a version. Supported: v2.' >&2; exit 2; }
      opencode_version="$2"
      shift
      ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done
case "$forward_proxy" in
  "" | socks | socks4 | socks4a | socks5 | socks5h | http | https) ;;
  *) echo "Unknown --forward-proxy scheme '$forward_proxy'. Supported: $supported_schemes." >&2; exit 2 ;;
esac
case "$opencode_version" in
  "" | v2) ;;
  *) echo "Unknown --opencode-version '$opencode_version'. Supported: v2." >&2; exit 2 ;;
esac

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project_root="$(dirname "$script_dir")"
test_home="$project_root/.tmp/e2e/home"
source_env="$script_dir/.env"
proxy_dir="$project_root/.tmp/e2e/fault-proxy"
forward_dir="$project_root/.tmp/e2e/forward-proxy"
proxy_port=8765
forward_port=8766
proxy_root="http://127.0.0.1:$proxy_port"
forward_pid_file="$forward_dir/proxy.pid"
opencode_state_home="$project_root/.tmp/e2e/opencode-state"

test_env_value() {
  [ -f "$source_env" ] || return 0
  grep -E "^[[:space:]]*$1[[:space:]]*=" "$source_env" | tail -n 1 |
    sed -e "s/^[[:space:]]*$1[[:space:]]*=[[:space:]]*//" -e 's/[[:space:]]*$//' \
      -e "s/^[\"']//" -e "s/[\"']\$//" || true
}

# Validate before creating directories or starting any process.
if [ -n "$forward_proxy" ]; then
  if [ "$fault_proxy" -eq 1 ]; then
    echo '--forward-proxy cannot be combined with --fault-proxy.' >&2
    exit 1
  fi
  for name in TELEGRAM_PROXY_URL TELEGRAM_API_ROOT; do
    if [ -n "$(test_env_value "$name")" ] || [ -n "${!name:-}" ]; then
      echo "--forward-proxy cannot be used while $name is set in e2e/.env or the environment." >&2
      exit 1
    fi
  done
fi
if [ "$fault_proxy" -eq 1 ] && [ -n "$(test_env_value TELEGRAM_PROXY_URL)" ]; then
  echo '--fault-proxy cannot be used while e2e/.env sets TELEGRAM_PROXY_URL.' >&2
  exit 1
fi
if [ ! -f "$source_env" ]; then
  cp "$script_dir/.env.example" "$source_env"
  echo "Created $source_env. Fill in TELEGRAM_BOT_TOKEN and TELEGRAM_ALLOWED_USER_ID, then run again."
  exit 1
fi
saved_version="$(test_env_value OPENCODE_SERVER_VERSION)"
if [ -z "$opencode_version" ] && [ -n "$saved_version" ] && [ "$saved_version" != v2 ]; then
  echo 'Only OpenCode V2 is supported. Set OPENCODE_SERVER_VERSION=v2.' >&2
  exit 1
fi

mkdir -p "$test_home"
cp "$source_env" "$test_home/.env"
while IFS= read -r line; do
  line="${line%$'\r'}"
  case "$line" in
    [A-Za-z_]*=*) unset "${line%%=*}" 2>/dev/null || true ;;
  esac
done < "$test_home/.env"

service_config="${XDG_CONFIG_HOME:-$HOME/.config}/opencode/service.json"
opencode_password=""
if [ -f "$service_config" ]; then
  opencode_password="$(node -e 'process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")).password ?? ""))' "$service_config")"
fi
if [ -z "$opencode_password" ]; then
  echo "OpenCode V2 needs a server password. Set one with 'opencode service set password <value>'." >&2
  exit 1
fi
opencode_bin="$(npm root -g)/@opencode/cli/bin"
if [ ! -e "$opencode_bin/opencode" ]; then
  echo 'No global npm V2 install found; using opencode on PATH.' >&2
  opencode_bin=""
fi
if [ "$skip_build" -eq 0 ]; then
  echo 'Building...'
  (cd "$project_root" && npm run build)
fi
export OPENCODE_TELEGRAM_HOME="$test_home"
export OPENCODE_SERVER_VERSION=v2
export OPENCODE_SERVER_PASSWORD="$opencode_password"
export XDG_STATE_HOME="$opencode_state_home"
unset OPENCODE_CONFIG_DIR
[ -z "$opencode_bin" ] || export PATH="$opencode_bin:$PATH"

stop_leftover_proxy() {
  [ -f "$1" ] || return 0
  local pid args
  pid="$(tr -d '[:space:]' < "$1")"
  if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
    args="$(ps -p "$pid" -o args= 2>/dev/null || true)"
    case "$args" in *"$2"*) kill "$pid" 2>/dev/null || true ;; esac
  fi
  rm -f "$1"
}
if [ "$fault_proxy" -eq 1 ]; then
  stop_leftover_proxy "$proxy_dir/proxy.pid" fault-proxy.mjs
  mkdir -p "$proxy_dir"
  upstream="$(test_env_value TELEGRAM_API_ROOT)"
  [ -n "$upstream" ] || upstream=https://api.telegram.org
  nohup node "$script_dir/fault-proxy.mjs" --port "$proxy_port" --upstream "$upstream" > "$proxy_dir/proxy-output.log" 2>&1 &
  ready=0
  for _ in $(seq 1 20); do
    if node -e "fetch('$proxy_root/__fault/state').then((r) => process.exit(r.ok ? 0 : 1), () => process.exit(1))"; then
      ready=1
      break
    fi
    sleep 0.25
  done
  [ "$ready" -eq 1 ] || { echo "Fault proxy did not come up. See $proxy_dir/proxy-output.log" >&2; exit 1; }
  export TELEGRAM_API_ROOT="$proxy_root"
fi
if [ -n "$forward_proxy" ]; then
  stop_leftover_proxy "$forward_pid_file" forward-proxy.mjs
  mkdir -p "$forward_dir"
  nohup node "$script_dir/forward-proxy.mjs" --scheme "$forward_proxy" --port "$forward_port" > "$forward_dir/proxy-output.log" 2>&1 &
  forward_pid=$!
  stop_forward_proxy() {
    kill "$forward_pid" 2>/dev/null || true
    rm -f "$forward_pid_file"
  }
  trap stop_forward_proxy EXIT
  trap 'exit 130' INT
  trap 'exit 143' TERM
  ready=0
  for _ in $(seq 1 20); do
    kill -0 "$forward_pid" 2>/dev/null || break
    if [ -f "$forward_pid_file" ]; then ready=1; break; fi
    sleep 0.25
  done
  [ "$ready" -eq 1 ] || { echo "Forward proxy did not come up. See $forward_dir/proxy-output.log" >&2; exit 1; }
  export TELEGRAM_PROXY_URL="$forward_proxy://127.0.0.1:$forward_port"
  if [ "$forward_proxy" = https ]; then
    export NODE_EXTRA_CA_CERTS="$script_dir/forward-proxy-test-only.crt"
  fi
fi
echo "Test home: $test_home"
echo "OpenCode: v2 (${opencode_bin:-opencode on PATH})"
if [ -n "$forward_proxy" ]; then
  node "$project_root/dist/index.js"
else
  exec node "$project_root/dist/index.js"
fi
