#!/bin/bash
# Build and pack the Flash plugin as an installable BTCPay Server plugin (.btcpay)

set -euo pipefail

PLUGIN_NAME="BTCPayServer.Plugins.Flash"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PUBLISH_DIR="$SCRIPT_DIR/bin/publish/$PLUGIN_NAME"
OUTPUT_DIR="${1:-$REPO_ROOT/packaged}"

# Override PLUGIN_PACKER to point at any checkout containing BTCPayServer.PluginPacker.
PLUGIN_PACKER="${PLUGIN_PACKER:-$REPO_ROOT/../BTCPayServerPluginsKukks/submodules/btcpayserver/BTCPayServer.PluginPacker}"

DOTNET="${DOTNET:-dotnet}"
if ! command -v "$DOTNET" &>/dev/null; then
    if [ -x "$HOME/.dotnet/dotnet" ]; then
        DOTNET="$HOME/.dotnet/dotnet"
    else
        echo "Error: dotnet not found. Install the .NET SDK or set DOTNET path."
        exit 1
    fi
fi

if [ ! -d "$PLUGIN_PACKER" ]; then
    echo "Error: PluginPacker not found at $PLUGIN_PACKER"
    echo "Ensure BTCPayServerPluginsKukks is cloned as a sibling directory, or set PLUGIN_PACKER."
    exit 1
fi

echo "Building $PLUGIN_NAME..."
"$DOTNET" publish "$SCRIPT_DIR/$PLUGIN_NAME.csproj" -c Release -o "$PUBLISH_DIR"

echo "Packing plugin..."
"$DOTNET" run --project "$PLUGIN_PACKER" -- "$PUBLISH_DIR" "$PLUGIN_NAME" "$OUTPUT_DIR"

PLUGIN_FILE=$(find "$OUTPUT_DIR/$PLUGIN_NAME" -mindepth 2 -maxdepth 2 -name "$PLUGIN_NAME.btcpay" -printf '%T@ %p\n' 2>/dev/null | sort -rn | head -n1 | cut -d' ' -f2- || true)
if [ -z "$PLUGIN_FILE" ]; then
    echo "Error: packing reported success but no $PLUGIN_NAME.btcpay was found under $OUTPUT_DIR/$PLUGIN_NAME"
    exit 1
fi

echo ""
echo "Done! Installable plugin created at:"
echo "  $PLUGIN_FILE"
echo ""
echo "To install: Upload this file via BTCPay Server > Settings > Plugins"
