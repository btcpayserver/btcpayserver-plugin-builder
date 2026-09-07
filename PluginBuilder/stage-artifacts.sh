#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'
umask 077

readonly metadata_max_bytes=$((1024 * 1024))
readonly artifact_max_bytes=$((256 * 1024 * 1024))

fail() {
    printf 'Artifact staging rejected: %s\n' "$*" >&2
    exit 1
}

validate_leaf() {
    local value="$1"
    [[ "$value" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$ ]] ||
        fail "assembly name is not a safe file name"
}

validate_regular() {
    local source="$1"
    local max_bytes="$2"
    local label="$3"
    local size

    [[ -f "$source" && ! -L "$source" ]] || fail "$label is not a regular non-symlink file"
    size="$(stat --format=%s -- "$source")" || fail "cannot stat $label"
    (( size > 0 )) || fail "$label is empty"
    (( size <= max_bytes )) || fail "$label exceeds its size limit"
}

[[ "$#" -eq 0 ]] || fail "this command accepts no arguments"
[[ -d /untrusted-output && ! -L /untrusted-output ]] || fail "/untrusted-output is unavailable"
[[ -d /source/repository && ! -L /source/repository ]] || fail "/source/repository is unavailable"
[[ -d /staging && ! -L /staging ]] || fail "/staging is unavailable"
[[ -z "$(find /staging -xdev -mindepth 1 -maxdepth 1 -print -quit)" ]] ||
    fail "/staging is not empty"

mapfile -d '' artifact_files < <(
    find /untrusted-output -xdev -mindepth 1 -maxdepth 1 -type f -name '*.btcpay' -print0
)
(( ${#artifact_files[@]} == 1 )) ||
    fail "expected exactly one top-level .btcpay artifact; found ${#artifact_files[@]}"

artifact="${artifact_files[0]}"
artifact_name="${artifact##*/}"
assembly_name="${artifact_name%.btcpay}"
validate_leaf "$assembly_name"

manifest="/untrusted-output/$assembly_name.btcpay.json"
validate_regular "$manifest" "$metadata_max_bytes" "plugin manifest"
validate_regular "$artifact" "$artifact_max_bytes" "plugin artifact"
jq -e 'type == "object"' "$manifest" >/dev/null || fail "plugin manifest is not valid JSON"

export GIT_CONFIG_NOSYSTEM=1
export GIT_CONFIG_GLOBAL=/dev/null
git_commit="$(git -c safe.directory=/source/repository -C /source/repository rev-parse --verify 'HEAD^{commit}')" ||
    fail "cannot resolve the checked-out commit"
[[ "$git_commit" =~ ^([0-9a-f]{40}|[0-9a-f]{64})$ ]] ||
    fail "checked-out commit has an invalid object ID"

git_commit_date_raw="$(git -c safe.directory=/source/repository -C /source/repository show -s --format=%cI "$git_commit")" ||
    fail "cannot read the checked-out commit date"
git_commit_date="$(date --date "$git_commit_date_raw" --iso-8601=seconds --utc)" ||
    fail "checked-out commit date is invalid"
build_date="$(date --iso-8601=seconds --utc)"

artifact_hash="$(sha256sum -- "$artifact")"
artifact_hash="${artifact_hash%% *}"
[[ "$artifact_hash" =~ ^[0-9a-f]{64}$ ]] || fail "could not calculate artifact SHA-256"

# Staging is private and empty; consumers read only after this command succeeds
# and its container has been removed, so files can be written directly.
cp --no-preserve=all --reflink=never -- "$manifest" /staging/manifest.json
cp --no-preserve=all --reflink=never -- "$artifact" /staging/artifact.btcpay
printf '%s\n' "$artifact_hash" > /staging/artifact.sha256

jq --null-input \
    --arg assemblyName "$assembly_name" \
    --arg gitCommit "$git_commit" \
    --arg gitCommitDate "$git_commit_date" \
    --arg buildDate "$build_date" \
    --arg buildHash "$artifact_hash" \
    '{
        assemblyName: $assemblyName,
        gitCommit: $gitCommit,
        gitCommitDate: $gitCommitDate,
        buildDate: $buildDate,
        buildHash: $buildHash
    }' > /staging/build-env.json
chmod 0600 /staging/manifest.json /staging/artifact.btcpay \
    /staging/artifact.sha256 /staging/build-env.json
