#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
expected=2868deedd9d911b715fec7306ea9cada36d86bff
actual="$(git -C "$repo_root/submodules/btcpayserver" rev-parse HEAD)"
if [ "$actual" != "$expected" ]; then
    echo "Build requires the pinned BTCPay Server v2.4.7 source. Run git submodule update --init submodules/btcpayserver." >&2
    exit 1
fi

if ! git -C "$repo_root/submodules/btcpayserver" diff --quiet HEAD --; then
    echo "Build requires an unmodified pinned BTCPay host. Review tracked source changes before building." >&2
    exit 1
fi

# Git ignore rules and filename extensions are not build-input exclusions.
# Require a clean source tree, exempting only normal output roots directly under
# tracked project directories. This covers response/config/resource/Razor inputs
# and links without depending on a list of recognized file extensions.
host_root="$repo_root/submodules/btcpayserver"
scan="$(mktemp)"
projects="$(mktemp)"
trap 'rm -f "$scan" "$projects"' EXIT
git -C "$host_root" ls-files -z -- '*.csproj' > "$projects"
output_roots=()
while IFS= read -r -d '' project; do
    prefix="${project%/*}/"
    if [ "$project" = "${project%/*}" ]; then prefix=""; fi
    output_roots+=("${prefix}bin/" "${prefix}obj/")
done < "$projects"
git -C "$host_root" ls-files --others -z -- > "$scan"
while IFS= read -r -d '' path; do
    generated=false
    for output in "${output_roots[@]}"; do
        if [[ "$path" == "$output"* ]] && [ -d "$host_root/${output%/}" ] && [ ! -L "$host_root/${output%/}" ]; then
            generated=true
            break
        fi
    done
    if [ "$generated" = true ]; then continue; fi
    echo "Build requires no untracked or ignored files or links outside tracked project output roots in the pinned BTCPay host." >&2
    exit 1
done < "$scan"
