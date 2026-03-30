#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/webvttdl"
OUTPUT_DIR="$PROJECT_DIR/bin/Release"

CONFIG="${1:-Release}"

echo "=========================================="
echo "webvttdl Build (Mono)"
echo "=========================================="

if ! command -v mcs &> /dev/null; then
    echo "ERROR: mcs not found."
    echo "Install Mono:"
    echo "  Fedora/RHEL: sudo dnf install mono-devel"
    echo "  Debian/Ubuntu: sudo apt install mono-devel"
    exit 1
fi

echo "Configuration: $CONFIG"
echo "Output: $OUTPUT_DIR"
echo ""

rm -rf "$OUTPUT_DIR" "$PROJECT_DIR/obj"
mkdir -p "$OUTPUT_DIR"

CS_FILES=(
    "$PROJECT_DIR/Properties/AssemblyInfo.cs"
    "$PROJECT_DIR/M3u8Parser.cs"
    "$PROJECT_DIR/CurlDownloader.cs"
    "$PROJECT_DIR/WebVttMerger.cs"
    "$PROJECT_DIR/WebVttToSrtConverter.cs"
    "$PROJECT_DIR/Program.cs"
)

echo "Building..."
mcs \
    -target:exe \
    -out:"$OUTPUT_DIR/webvttdl.exe" \
    -sdk:4 \
    -platform:x86 \
    -r:System.dll \
    -r:System.Core.dll \
    -optimize+ \
    -warn:4 \
    "${CS_FILES[@]}"

cat > "$OUTPUT_DIR/webvttdl.exe.config" << 'EOF'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup>
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.0"/>
  </startup>
</configuration>
EOF

CURL_DIR="$PROJECT_DIR/bin/curl"
if [ -d "$CURL_DIR" ]; then
    echo "Copying curl files from $CURL_DIR..."
    cp "$CURL_DIR"/* "$OUTPUT_DIR/"
else
    echo "WARNING: $CURL_DIR not found, skipping curl copy."
fi

echo ""
echo "Build successful!"
echo ""
ls -lh "$OUTPUT_DIR/"
echo ""
echo "Usage: mono $OUTPUT_DIR/webvttdl.exe <master-m3u8-url>"
