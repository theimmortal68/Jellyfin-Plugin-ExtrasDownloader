#!/bin/bash
set -e

echo "=========================================="
echo "Building Jellyfin Extras Downloader Plugin"
echo "=========================================="

cd "$(dirname "$0")"

# Build the POT server first (requires Node.js)
POT_SERVER_DIR="./Jellyfin.Plugin.ExtrasDownloader/pot-server"

if [ -d "$POT_SERVER_DIR" ]; then
    echo ""
    echo "Building POT server..."
    echo "----------------------------------------"

    if command -v node &> /dev/null; then
        cd "$POT_SERVER_DIR"

        # Install dependencies if needed
        if [ ! -d "node_modules" ]; then
            echo "Installing Node.js dependencies..."
            npm install
        fi

        # Build TypeScript
        echo "Compiling TypeScript..."
        npm run build 2>/dev/null || npx tsc

        cd - > /dev/null
        echo "POT server built successfully"
    else
        echo "WARNING: Node.js not found. POT server will not be built."
        echo "         Users will need to configure an external POT provider URL."
    fi
fi

# Build the .NET plugin
echo ""
echo "Building .NET plugin..."
echo "----------------------------------------"

dotnet build -c Release

# Create plugin directory structure
PLUGIN_DIR="./dist/ExtrasDownloader"
mkdir -p "$PLUGIN_DIR"

# Copy the DLL
cp ./Jellyfin.Plugin.ExtrasDownloader/bin/Release/Jellyfin.Plugin.ExtrasDownloader.dll "$PLUGIN_DIR/"

# Copy POT server if built
if [ -d "$POT_SERVER_DIR/build" ]; then
    echo "Copying POT server files..."
    mkdir -p "$PLUGIN_DIR/pot-server"
    cp -r "$POT_SERVER_DIR/build" "$PLUGIN_DIR/pot-server/"
    cp "$POT_SERVER_DIR/package.json" "$PLUGIN_DIR/pot-server/"
    cp "$POT_SERVER_DIR/package-lock.json" "$PLUGIN_DIR/pot-server/"
fi

# Create meta.json for plugin manifest
cat > "$PLUGIN_DIR/meta.json" << 'EOF'
{
    "guid": "d2a0abca-4598-4d40-97bc-f74966738645",
    "name": "Extras Downloader",
    "description": "Downloads trailers and extras from TMDB/YouTube",
    "overview": "Automatically downloads trailers, featurettes, behind-the-scenes content, and other extras for your movie and TV show library. Triggers automatically when new media is added.",
    "owner": "Your Name",
    "category": "Metadata",
    "versions": [
        {
            "version": "2.0.0.0",
            "changelog": "Added automatic download on new media, POT server integration for YouTube bot bypass",
            "targetAbi": "10.9.0.0",
            "sourceUrl": "",
            "checksum": "",
            "timestamp": ""
        }
    ]
}
EOF

echo ""
echo "=========================================="
echo "Build complete!"
echo "=========================================="
echo ""
echo "Plugin files are in: $PLUGIN_DIR"
echo ""
echo "To install:"
echo "  1. Copy the 'ExtrasDownloader' folder to your Jellyfin plugins directory"
echo "  2. Restart Jellyfin"
echo ""
echo "Jellyfin plugin directories:"
echo "  Linux:   /var/lib/jellyfin/plugins/"
echo "  Windows: C:\\ProgramData\\Jellyfin\\Server\\plugins\\"
echo "  Docker:  /config/plugins/"
echo ""
echo "Requirements on Jellyfin server:"
echo "  - yt-dlp (must be in PATH or configured in plugin settings)"
echo "  - Node.js (optional, for bundled POT server)"
