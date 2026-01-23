#!/usr/bin/env bash
set -e

git --version
dotnet --info

: "${BUILD_CONFIG:=Release}"

BRANCH_OPTS=""
[[ "$GIT_REF" ]] && BRANCH_OPTS="-b ${GIT_REF}"

git clone --depth 1 --recurse-submodules $BRANCH_OPTS --single-branch "${GIT_REPO}" .
GIT_COMMIT="$(git rev-parse HEAD)"
GIT_COMMIT_DATE=$(git show -s --format=%ci)
# To UTC
GIT_COMMIT_DATE=$(date -d "$GIT_COMMIT_DATE" --iso-8601=seconds --utc)
[[ "$PLUGIN_DIR" ]] && cd "${PLUGIN_DIR}"

shopt -s nullglob
csprojs=( *.csproj )
if (( ${#csprojs[@]} != 1 )); then
    echo "Expected exactly one .csproj for ASSEMBLY_NAME in ${PLUGIN_DIR:-$(pwd)}, found ${#csprojs[@]}: ${csprojs[*]}" >&2
    exit 1
fi
ASSEMBLY_NAME="${csprojs[0]}"
shopt -u nullglob

# Publish the csproj explicitly to prevent MSBuild behavior that catches .sln files, and handle retarded project folder structures like https://github.com/btcpay-monero/btcpayserver-monero-plugin/tree/master/Plugins/Monero
dotnet publish "${ASSEMBLY_NAME}" -c "${BUILD_CONFIG}" -o "/tmp/publish"
ASSEMBLY_NAME="${ASSEMBLY_NAME/.csproj/}"

# PluginPacker crash because of no gpg, but we don't use it anyway...
/build-tools/PluginPacker/BTCPayServer.PluginPacker "/tmp/publish" "${ASSEMBLY_NAME}" "/tmp/publish-package" || true
cp /tmp/publish-package/*/*/* /out
rm /out/SHA256SUMS.asc /out/SHA256SUMS

BUILD_DATE=$(date --iso-8601=seconds --utc)
# To UTC
BUILD_DATE=$(date -d "$BUILD_DATE" --iso-8601=seconds --utc)
BUILD_HASH=($(sha256sum /out/${ASSEMBLY_NAME}.btcpay))

jq --null-input \
--arg buildConfig "$BUILD_CONFIG" \
--arg gitRef "$GIT_REF" \
--arg gitRepository "$GIT_REPO" \
--arg pluginDir "$PLUGIN_DIR" \
--arg buildConfig "$BUILD_CONFIG" \
--arg gitCommit "$GIT_COMMIT" \
--arg gitCommitDate "$GIT_COMMIT_DATE" \
--arg buildDate "$BUILD_DATE" \
--arg buildHash "$BUILD_HASH" \
--arg assemblyName "$ASSEMBLY_NAME" \
'{
"assemblyName": $assemblyName,
"gitRepository": $gitRepository,
"gitRef": $gitRef,
"pluginDir": $pluginDir,
"buildConfig": $buildConfig,
"gitCommit": $gitCommit,
"gitCommitDate": $gitCommitDate,
"buildDate": $buildDate,
"buildHash": $buildHash
}' > /out/build-env.json



# {
#   "gitRepository": "https://github.com/Kukks/btcpayserver",
#   "gitRef": "plugins/collection",
#   "pluginDir": "Plugins/BTCPayServer.Plugins.AOPP",
#   "gitCommit": "bed25814a7a47f7bf13b2a1cb9a2dcf544d268dd",
#   "gitCommitDate": "2022-10-31T10:03:52+00:00",
#   "buildDate": "2022-11-07T06:10:20+00:00",
#   "buildHash": "f56cec255e2fc92c1b2c0d39546d87548daa98a7fa0e9f7a7f28f6dc129a31b6"
# }

# ls /out/
# BTCPayServer.Plugins.AOPP.btcpay  BTCPayServer.Plugins.AOPP.btcpay.json   build-env.json
