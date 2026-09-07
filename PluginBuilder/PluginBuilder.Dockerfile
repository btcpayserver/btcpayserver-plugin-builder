FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c

RUN sed -i 's|http://archive.ubuntu.com|https://archive.ubuntu.com|g; s|http://security.ubuntu.com|https://security.ubuntu.com|g' \
        /etc/apt/sources.list.d/ubuntu.sources \
    && rm -rf /var/lib/apt/lists/* \
    && apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates git jq perl-base \
    && rm -rf /var/lib/apt/lists/*

COPY NuGet.Config /build-tools/NuGet.Config

# Keep the trusted packer immutable even if its release tag is moved.
RUN mkdir -p /build-tools/btcpayserver \
    && cd /build-tools/btcpayserver \
    && git init \
    && git remote add origin https://github.com/btcpayserver/btcpayserver \
    && git -c protocol.allow=never -c protocol.https.allow=always \
        fetch --depth 1 origin 50ae4bfc5da2e193db5b37fa718dbb92e41958b5 \
    && git checkout --detach FETCH_HEAD \
    && dotnet restore BTCPayServer.PluginPacker/BTCPayServer.PluginPacker.csproj \
        --configfile /build-tools/NuGet.Config \
    && dotnet build BTCPayServer.PluginPacker/BTCPayServer.PluginPacker.csproj \
        --configuration Release \
        --no-restore \
        --output /build-tools/PluginPacker \
    && rm -rf /build-tools/btcpayserver /build /root/.nuget /root/.local /tmp/*

RUN groupadd --gid 10002 pluginclone \
    && useradd --uid 10002 --gid 10002 --no-create-home \
        --home-dir /tmp/home --shell /usr/sbin/nologin pluginclone \
    && groupadd --gid 10001 pluginbuild \
    && useradd --uid 10001 --gid 10001 --no-create-home \
        --home-dir /build/home --shell /usr/sbin/nologin pluginbuild \
    && mkdir -p /build /build/home /out /source /staging \
    && chown -R 10001:10001 /build /out /source /staging

COPY --chown=10001:10001 --chmod=0555 entrypoint.sh /entrypoint.sh
COPY --chown=10001:10001 --chmod=0555 clone-source.sh /clone-source.sh
COPY --chown=10001:10001 --chmod=0555 limit-output.pl /limit-output.pl
COPY --chown=10001:10001 --chmod=0555 stage-artifacts.sh /stage-artifacts.sh

ENV HOME=/build/home \
    DOTNET_CLI_HOME=/build/home \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_PACKAGES=/build/.nuget/packages \
    NUGET_HTTP_CACHE_PATH=/build/.nuget/http-cache \
    NUGET_XMLDOC_MODE=skip \
    TMPDIR=/build/tmp

USER 10001:10001
WORKDIR /build

ENTRYPOINT ["/entrypoint.sh"]
