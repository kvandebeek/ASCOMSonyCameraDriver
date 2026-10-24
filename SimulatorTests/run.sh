#!/usr/bin/env bash
# Builds the driver sources against a simulated SonyMTPCamera.dll and runs the tests under Mono.
#
#   ./run.sh                 test the current working tree
#   ./run.sh --baseline REF  also test git revision REF (e.g. 7346839) and compare image output
#
# Needs: gcc, mono-mcs, mono-runtime, Mono WinForms/Drawing/Configuration.Install, curl, unzip
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
OUT="$HERE/bin"
ASCOM_VERSION=6.5.2
BASELINE=""

if [ "${1:-}" = "--baseline" ]; then
    BASELINE="${2:?--baseline needs a git revision}"
fi

mkdir -p "$OUT"

# Real ASCOM assemblies, for the interface/exception types and the signature check
ASCOM="$HERE/packages/ascom.platform.$ASCOM_VERSION/lib/net40"
if [ ! -d "$ASCOM" ]; then
    mkdir -p "$HERE/packages/ascom.platform.$ASCOM_VERSION"
    curl -sSL -o "$OUT/ascom.nupkg" "https://api.nuget.org/v3-flatcontainer/ascom.platform/$ASCOM_VERSION/ascom.platform.$ASCOM_VERSION.nupkg"
    unzip -qo "$OUT/ascom.nupkg" -d "$HERE/packages/ascom.platform.$ASCOM_VERSION"
fi

FRAMEWORK="-r:System.Windows.Forms -r:System.Drawing -r:System.Configuration -r:System.Configuration.Install"

gcc -O2 -Wall -shared -fPIC "$HERE/FakeSonyMTPCamera.c" -o "$OUT/libSonyMTPCamera.so"

# $1 = label, $2 = directory holding the driver sources
run_tests() {
    local label="$1" src="$2" dir="$OUT/$1"
    mkdir -p "$dir"
    cp "$OUT/libSonyMTPCamera.so" "$dir/"

    # Driver must compile against the real ASCOM assemblies, including at the old C# 4 language level
    mcs -target:library -langversion:4 -nowarn:219 \
        -r:"$ASCOM/ASCOM.DeviceInterfaces.dll" -r:"$ASCOM/ASCOM.Astrometry.dll" -r:"$ASCOM/ASCOM.Exceptions.dll" \
        -r:"$ASCOM/ASCOM.Utilities.dll" -r:"$ASCOM/ASCOM.Attributes.dll" $FRAMEWORK \
        -out:"$dir/driver-check.dll" "$src"/*.cs "$src"/Properties/*.cs

    # Test build swaps ASCOM.Utilities/Astrometry for Stubs.cs, as the real ones need Windows and the VB runtime
    cp "$ASCOM/ASCOM.DeviceInterfaces.dll" "$ASCOM/ASCOM.Exceptions.dll" "$ASCOM/ASCOM.Attributes.dll" "$dir/"
    mcs -optimize+ -nowarn:618,219,414,169,649 \
        -r:"$ASCOM/ASCOM.DeviceInterfaces.dll" -r:"$ASCOM/ASCOM.Exceptions.dll" -r:"$ASCOM/ASCOM.Attributes.dll" $FRAMEWORK \
        -out:"$dir/tests.exe" "$src"/*.cs "$src"/Properties/*.cs "$HERE"/Tests.cs "$HERE"/DriverTests.cs "$HERE"/Stubs.cs

    echo "===== $label"
    # A non-UTC zone so the LastExposureStartTime UTC check means something
    (cd "$dir" && TZ="${TZ:-Europe/Brussels}" LD_LIBRARY_PATH=. mono tests.exe | tee "$OUT/$label.out" | grep -v '^HASH')
}

status=0
run_tests current "$REPO/ASCOMSonyCameraDriver" || status=$?

if [ -n "$BASELINE" ]; then
    rm -rf "$OUT/baseline-src" && mkdir -p "$OUT/baseline-src"
    git -C "$REPO" archive "$BASELINE" ASCOMSonyCameraDriver | tar -x -C "$OUT/baseline-src"
    run_tests baseline "$OUT/baseline-src/ASCOMSonyCameraDriver" || true
    echo "===== image output differences (baseline vs current)"
    diff <(grep '^HASH' "$OUT/baseline.out") <(grep '^HASH' "$OUT/current.out") || true
fi

exit $status
