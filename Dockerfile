FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

WORKDIR /source
COPY PluginBuilder/. PluginBuilder/.

ARG CONFIGURATION_NAME=Release
ARG VERSION
ARG GIT_COMMIT
RUN cd PluginBuilder && dotnet publish -p:Version=${VERSION} -p:GitCommit=${GIT_COMMIT} --output /app/ --configuration ${CONFIGURATION_NAME}

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ENV LC_ALL=en_US.UTF-8
ENV LANG=en_US.UTF-8
WORKDIR /datadir
WORKDIR /app
ENV PB_DATADIR=/datadir
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
VOLUME /datadir

ENV DEBIAN_FRONTEND=noninteractive

# Install curl
RUN apt-get -qq update \
  && apt-get -y -qq install apt-transport-https ca-certificates curl gnupg lsb-release --no-install-recommends \
  && rm -rf /var/lib/apt/lists/*

# Install docker
RUN install -m 0755 -d /etc/apt/keyrings && \
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg | \
      gpg --dearmor -o /etc/apt/keyrings/docker.gpg && \
    chmod a+r /etc/apt/keyrings/docker.gpg && \
    echo \
      "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] \
      https://download.docker.com/linux/ubuntu \
      $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | \
      tee /etc/apt/sources.list.d/docker.list > /dev/null

RUN apt-get -qq update \
  && apt-get -y -qq install docker-ce-cli docker-buildx-plugin docker-compose-plugin \
  && rm -rf /var/lib/apt/lists/*

COPY --from=builder "/app" .
ENTRYPOINT ["/app/PluginBuilder"]
