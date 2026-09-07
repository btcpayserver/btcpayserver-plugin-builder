#!/usr/bin/env bash

if [[ "${1:-}" != "--run-build" ]]; then
    : "${BUILD_TIMEOUT_SECONDS:=900}"
    : "${BUILD_LOG_MAX_BYTES:=10485760}"
    : "${BUILD_LOG_MAX_LINE_BYTES:=65536}"
    : "${BUILD_LOG_MAX_LINES:=10000}"

    exec /usr/bin/perl /limit-output.pl \
        "$BUILD_LOG_MAX_BYTES" \
        "$BUILD_LOG_MAX_LINE_BYTES" \
        "$BUILD_LOG_MAX_LINES" \
        "$BUILD_TIMEOUT_SECONDS" \
        -- "$0" --run-build
fi

shift
[[ "$(id -u)" == "10001" && "$(id -g)" == "10001" ]] || {
    printf 'Build rejected: worker must run as uid/gid 10001.\n' >&2
    exit 1
}

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

fail() {
    printf 'Build rejected: %s\n' "$*" >&2
    exit 1
}

: "${PLUGIN_DIR:=}"
: "${BUILD_CONFIG:=Release}"

(( ${#PLUGIN_DIR} <= 1024 )) || fail "plugin directory is too long"
[[ "$PLUGIN_DIR" != /* && "$PLUGIN_DIR" != *$'\n'* && "$PLUGIN_DIR" != *$'\r'* ]] ||
    fail "plugin directory must be a relative path"
[[ "/$PLUGIN_DIR/" != *"/../"* && "/$PLUGIN_DIR/" != *"/./"* ]] ||
    fail "plugin directory contains traversal"
[[ "$BUILD_CONFIG" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ ]] ||
    fail "build configuration is invalid"

[[ -d /source/repository && ! -L /source/repository ]] ||
    fail "trusted source checkout is unavailable"

repo_dir=/build/repository
publish_dir=/build/publish
package_dir=/build/package
mkdir -p "$repo_dir" "$publish_dir" "$package_dir" /build/home "$NUGET_PACKAGES" "$NUGET_HTTP_CACHE_PATH" "$TMPDIR"

# Compile a private writable copy. The pristine checkout stays mounted read-only
# and is used later by the trusted staging step to derive provenance.
cp --archive --no-preserve=ownership --reflink=never -- /source/repository/. "$repo_dir/"

project_dir="$repo_dir"
if [[ -n "$PLUGIN_DIR" ]]; then
    project_dir="$(realpath -e -- "$repo_dir/$PLUGIN_DIR")" || fail "plugin directory does not exist"
    [[ "$project_dir" == "$repo_dir" || "$project_dir" == "$repo_dir"/* ]] ||
        fail "plugin directory escapes the repository"
fi
cd "$project_dir"

shopt -s nullglob
csprojs=( *.csproj )
shopt -u nullglob
if (( ${#csprojs[@]} != 1 )); then
    fail "expected exactly one .csproj in ${PLUGIN_DIR:-the repository root}; found ${#csprojs[@]}"
fi

project_file="${csprojs[0]}"
assembly_name="${project_file%.csproj}"
[[ "$assembly_name" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$ ]] ||
    fail "project file does not produce a safe artifact name"

dotnet restore "$project_file" \
    --property:Configuration="$BUILD_CONFIG" \
    --configfile /build-tools/NuGet.Config \
    --packages "$NUGET_PACKAGES"

# Publish the project explicitly so a repository-level solution cannot select
# a different target.
dotnet publish "$project_file" \
    --configuration "$BUILD_CONFIG" \
    --output "$publish_dir" \
    --no-restore

/build-tools/PluginPacker/BTCPayServer.PluginPacker \
    "$publish_dir" "$assembly_name" "$package_dir"

mapfile -d '' artifact_files < <(find "$package_dir" -type f -name "$assembly_name.btcpay" -print0)
mapfile -d '' manifest_files < <(find "$package_dir" -type f -name "$assembly_name.btcpay.json" -print0)
(( ${#artifact_files[@]} == 1 )) || fail "expected exactly one .btcpay artifact; found ${#artifact_files[@]}"
(( ${#manifest_files[@]} == 1 )) || fail "expected exactly one .btcpay.json manifest; found ${#manifest_files[@]}"

cp --no-preserve=all --reflink=never -- "${artifact_files[0]}" "/out/$assembly_name.btcpay"
cp --no-preserve=all --reflink=never -- "${manifest_files[0]}" "/out/$assembly_name.btcpay.json"
