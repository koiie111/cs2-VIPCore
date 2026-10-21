#!/bin/sh
set -eu
cd "$(dirname "$0")"
dotnet build -c Release --nologo
out=bin/Release/net10.0
rm -f "$out/FxFilterApi.dll"
dotnet "$out/FxFilterBridgeValidation.dll" missing
cp ../../VIPCore/FxFilterApi.dll "$out/"
dotnet "$out/FxFilterBridgeValidation.dll"
