#!/usr/bin/env bash
set -euo pipefail
source_root="$(cd "$(dirname "$0")/../.." && pwd)"
fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT
mkdir -p "$fixture/scripts" "$fixture/submodules/btcpayserver"
host="$fixture/submodules/btcpayserver"
git -C "$host" init -q
git -C "$host" config user.name 'Host guard test'
git -C "$host" config user.email 'test@example.invalid'
echo 'pinned source' > "$host/source.cs"
git -C "$host" add source.cs
git -C "$host" -c core.hooksPath=/dev/null commit -qm 'Fixture pin'
pinned="$(git -C "$host" rev-parse HEAD)"
# Use a disposable repository's pin so the test needs no host download.
sed "s/^expected=.*/expected=$pinned/" "$source_root/scripts/verify-btcpay-host.sh" > "$fixture/scripts/verify-btcpay-host.sh"
verify() { bash "$fixture/scripts/verify-btcpay-host.sh"; }
reject() {
    if verify > "$fixture/output" 2>&1; then
        echo "Host guard accepted $1" >&2
        exit 1
    fi
}
verify
mkdir -p "$host/bin"
echo 'build output' > "$host/bin/untracked.dll"
verify
echo 'modified source' > "$host/source.cs"
reject 'unstaged source changes'
git -C "$host" add source.cs
reject 'staged source changes'
git -C "$host" restore --source=HEAD --staged --worktree source.cs
rm "$host/source.cs"
reject 'tracked source deletion'
git -C "$host" restore source.cs
echo 'another revision' > "$host/source.cs"
git -C "$host" add source.cs
git -C "$host" -c core.hooksPath=/dev/null commit -qm 'Wrong revision'
reject 'a clean checkout at another revision'
echo 'Host source guard tests passed'
