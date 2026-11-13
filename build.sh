#!/bin/bash

# Build script for the Jellyfin Custom Metadata plugin

echo "🔨 Building the Jellyfin Custom Metadata plugin..."

# Clean
echo "🧹 Cleaning previous builds..."
dotnet clean

# Release build
echo "⚙️ Building in Release mode..."
dotnet build --configuration Release

if [ $? -eq 0 ]; then
    echo "✅ Build succeeded!"
    echo ""
    echo "📦 The compiled plugin is in:"
    echo "   bin/Release/net9.0/Jellyfin.Plugin.CustomMetadata.dll"
    echo ""
    echo "📋 To install the plugin in Jellyfin:"
    echo "   1. Copy the DLL to /var/lib/jellyfin/plugins/CustomMetadata/"
    echo "   2. Restart Jellyfin: sudo systemctl restart jellyfin"
    echo ""
else
    echo "❌ Build failed"
    exit 1
fi
