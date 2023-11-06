FROM mcr.microsoft.com/dotnet/sdk:6.0

RUN apt-get update && apt-get install -y git jq openssh-client && rm -rf /var/lib/apt/lists/*


RUN useradd -r --create-home dotnet
USER dotnet

WORKDIR /build-tools
ENV PLUGIN_PACKER_VERSION=https://github.com/btcpayserver/btcpayserver
RUN git clone --depth 1 -b v1.11.7 --single-branch https://github.com/btcpayserver/btcpayserver && \
    cd btcpayserver/BTCPayServer.PluginPacker && \
    dotnet build -c Release -o "/build-tools/PluginPacker" && \
    rm -rf /build-tools/btcpayserver


WORKDIR /out
WORKDIR /build
COPY --chown=dotnet:dotnet entrypoint.sh /entrypoint.sh

CMD [ "/entrypoint.sh" ]
