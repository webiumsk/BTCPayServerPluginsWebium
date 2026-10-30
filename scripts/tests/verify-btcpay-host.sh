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
echo '<Project Sdk="Microsoft.NET.Sdk" />' > "$host/Fixture.csproj"
printf '%s\n' '*.rsp' '*.props' '*.razor' '*.resx' 'nuget.config' 'hidden' > "$host/.gitignore"
git -C "$host" add source.cs Fixture.csproj .gitignore
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
mkdir -p "$host/obj"
echo 'generated source' > "$host/obj/generated.cs"
echo 'generated response' > "$host/obj/compiler.rsp"
verify
for output in bin obj; do
    mkdir -p "$host/nested/$output"
    echo 'injected source' > "$host/nested/$output/injected.cs"
    reject 'nested bin/obj source outside a tracked project output root'
    rm "$host/nested/$output/injected.cs"
done
rm -rf "$host/nested"
for input in ignored.razor ignored.resx unknown.input; do
    echo 'unexpected host input' > "$host/$input"
    reject 'ignored or extension-independent host input'
    rm "$host/$input"
done
mkdir -p "$host/BTCPayServer"
echo 'injected response' > "$host/BTCPayServer/Directory.Build.rsp"
reject 'ignored MSBuild response file'
rm "$host/BTCPayServer/Directory.Build.rsp"
echo '<Project />' > "$host/Directory.Build.props"
reject 'ignored build configuration'
rm "$host/Directory.Build.props"
for config in nuget.config NuGet.Config NUGET.CONFIG; do
    for directory in "$host" "$host/BTCPayServer"; do
        echo '<configuration />' > "$directory/$config"
        reject 'NuGet configuration filename casing'
        rm "$directory/$config"
    done
done
mkdir -p "$fixture/external"
echo 'injected source' > "$fixture/external/injected.cs"
for link in extra hidden; do
    ln -s "$fixture/external" "$host/$link"
    reject 'extensionless source-directory symlink'
    rm "$host/$link"
done
mkdir -p "$host/nested"
git -C "$host/nested" init -q
echo 'injected source' > "$host/nested/injected.cs"
reject 'nested untracked repository with compiled source'
rm -rf "$host/nested"
verify
echo 'injected source' > "$host/injected.cs"
reject 'untracked compiled source'
rm "$host/injected.cs"
echo '<Project />' > "$host/Directory.Build.targets"
reject 'untracked build configuration'
rm "$host/Directory.Build.targets"
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
