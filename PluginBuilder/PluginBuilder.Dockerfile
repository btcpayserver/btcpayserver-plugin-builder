FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS dotnet8-packer

RUN apt-get update && apt-get install -y git && rm -rf /var/lib/apt/lists/*

WORKDIR /build-tools
RUN git clone --depth 1 -b v2.0.0 --single-branch https://github.com/btcpayserver/btcpayserver && \
    cd btcpayserver/BTCPayServer.PluginPacker && \
    dotnet build -c Release -o "/build-tools/PluginPacker-net8" && \
    rm -rf /build-tools/btcpayserver

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet10-packer

RUN apt-get update && apt-get install -y git && rm -rf /var/lib/apt/lists/*

WORKDIR /build-tools
RUN git clone --depth 1 -b v2.3.6-rc3 --single-branch https://github.com/btcpayserver/btcpayserver && \
    cd btcpayserver/BTCPayServer.PluginPacker && \
    dotnet build -c Release -o "/build-tools/PluginPacker-net10" && \
    rm -rf /build-tools/btcpayserver


FROM mcr.microsoft.com/dotnet/sdk:10.0
COPY --from=mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim /usr/share/dotnet/sdk /usr/share/dotnet/sdk
COPY --from=mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim /usr/share/dotnet/shared /usr/share/dotnet/shared
COPY --from=dotnet8-packer /build-tools/PluginPacker-net8 /build-tools/PluginPacker-net8
COPY --from=dotnet10-packer /build-tools/PluginPacker-net10 /build-tools/PluginPacker-net10

RUN apt-get update && apt-get install -y git jq openssh-client && rm -rf /var/lib/apt/lists/*

RUN useradd -r --create-home dotnet
USER dotnet

WORKDIR /out
WORKDIR /build
COPY --chown=dotnet:dotnet entrypoint.sh /entrypoint.sh

CMD [ "/entrypoint.sh" ]
