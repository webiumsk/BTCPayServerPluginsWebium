#!/bin/bash
# Build and pack Satoshi Tickets plugin as an installable BTCPay Server plugin (.btcpay)

set -e

PLUGIN_NAME="BTCPayServer.Plugins.SatoshiTickets"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PUBLISH_DIR="$SCRIPT_DIR/bin/publish/$PLUGIN_NAME"
OUTPUT_DIR="${1:-$REPO_ROOT/packaged}"

PLUGIN_PACKER="$REPO_ROOT/submodules/btcpayserver/BTCPayServer.PluginPacker"
if [ ! -d "$PLUGIN_PACKER" ]; then
    PLUGIN_PACKER="$REPO_ROOT/../btcpayserver/BTCPayServer.PluginPacker"
fi
if [ ! -d "$PLUGIN_PACKER" ]; then
    PLUGIN_PACKER="$REPO_ROOT/../BTCPayServerPluginsKukks/submodules/btcpayserver/BTCPayServer.PluginPacker"
fi

DOTNET="${DOTNET:-dotnet}"
if ! command -v "$DOTNET" &>/dev/null; then
    if [ -x "$HOME/.dotnet/dotnet" ]; then
        DOTNET="$HOME/.dotnet/dotnet"
    else
        echo "Error: dotnet not found. Install .NET SDK or set DOTNET path."
        exit 1
    fi
fi

if [ ! -d "$PLUGIN_PACKER" ]; then
    echo "Error: PluginPacker not found."
    echo "Clone btcpayserver into $REPO_ROOT/submodules/btcpayserver or as a sibling directory."
    exit 1
fi

echo "Building $PLUGIN_NAME..."
$DOTNET publish "$SCRIPT_DIR/$PLUGIN_NAME.csproj" -c Release -o "$PUBLISH_DIR"

echo "Packing plugin..."
$DOTNET run --project "$PLUGIN_PACKER" -- "$PUBLISH_DIR" "$PLUGIN_NAME" "$OUTPUT_DIR"

PLUGIN_VERSION=$(ls -1t "$OUTPUT_DIR/$PLUGIN_NAME/" 2>/dev/null | head -1)
echo ""
echo "Done! Installable plugin created at:"
echo "  $OUTPUT_DIR/$PLUGIN_NAME/$PLUGIN_VERSION/$PLUGIN_NAME.btcpay"
echo ""
echo "To install: Upload this file via BTCPay Server > Settings > Plugins"
