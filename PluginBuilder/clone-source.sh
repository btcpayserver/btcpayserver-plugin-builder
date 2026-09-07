#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'
umask 022

fail() {
    printf 'Source checkout rejected: %s\n' "$*" >&2
    exit 1
}

[[ "$(id -u)" == "10002" && "$(id -g)" == "10002" ]] ||
    fail "checkout must run as uid/gid 10002"

: "${GIT_REPO:?GIT_REPO is required}"
: "${GIT_REF:=}"

case "$GIT_REPO" in
    https://github.com/*|https://gitlab.com/*) ;;
    *) fail "repository must use HTTPS on github.com or gitlab.com" ;;
esac

repo_path="${GIT_REPO#https://github.com/}"
if [[ "$repo_path" == "$GIT_REPO" ]]; then
    repo_path="${GIT_REPO#https://gitlab.com/}"
fi
[[ -n "$repo_path" && "$repo_path" =~ ^[A-Za-z0-9._/-]+$ ]] ||
    fail "repository path contains unsupported characters"
[[ "$repo_path" == */* && "$repo_path" != /* && "$repo_path" != */ && "$repo_path" != *//* ]] ||
    fail "repository path is malformed"
[[ "/$repo_path/" != *"/../"* && "/$repo_path/" != *"/./"* ]] ||
    fail "repository path contains traversal segments"

if [[ -n "$GIT_REF" ]]; then
    (( ${#GIT_REF} <= 255 )) || fail "Git ref is too long"
    git check-ref-format --branch "$GIT_REF" >/dev/null 2>&1 ||
        fail "Git ref is invalid"
fi

[[ -d /source && ! -L /source ]] || fail "/source is unavailable"
if [[ -n "$(find /source -xdev -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
    fail "/source is not empty"
fi

mkdir -p /tmp/home
export HOME=/tmp/home
export GIT_TERMINAL_PROMPT=0
export GIT_ASKPASS=/bin/false
export SSH_ASKPASS=/bin/false
export GCM_INTERACTIVE=Never
export GIT_CONFIG_NOSYSTEM=1
export GIT_CONFIG_GLOBAL=/dev/null
export GIT_CONFIG_COUNT=2
export GIT_CONFIG_KEY_0=protocol.allow
export GIT_CONFIG_VALUE_0=never
export GIT_CONFIG_KEY_1=protocol.https.allow
export GIT_CONFIG_VALUE_1=always
unset SSH_AUTH_SOCK

clone_args=(clone --depth 1 --recurse-submodules --single-branch)
if [[ -n "$GIT_REF" ]]; then
    clone_args+=(--branch "$GIT_REF")
fi
clone_args+=(-- "$GIT_REPO" /source/repository)
git "${clone_args[@]}"

git -C /source/repository rev-parse --verify 'HEAD^{commit}' >/dev/null ||
    fail "checkout has no valid commit"
