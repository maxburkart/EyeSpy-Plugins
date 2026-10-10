#!/usr/bin/env bash
# Builds the plugin into out/ContinuousLowRes and a zip next to it. Requires the .NET 9 SDK.
set -euo pipefail
cd "$(dirname "$0")"
rm -rf out && mkdir -p out
dotnet build ContinuousLowRes/ContinuousLowRes.csproj -c Release -o out/ContinuousLowRes
(cd out && zip -qr ContinuousLowRes.zip ContinuousLowRes)
echo "Built: out/ContinuousLowRes  (copy to [Agent]/Plugins/ContinuousLowRes and restart Agent)"
