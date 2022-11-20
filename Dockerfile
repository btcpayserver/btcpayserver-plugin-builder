FROM mcr.microsoft.com/dotnet/sdk:6.0.403-bullseye-slim AS builder
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

WORKDIR /source
COPY PluginBuilder/. PluginBuilder/.

ARG CONFIGURATION_NAME=Release
ARG VERSION
ARG GIT_COMMIT
RUN cd PluginBuilder && dotnet publish -p:Version=${VERSION} -p:GitCommit=${GIT_COMMIT} --output /app/ --configuration ${CONFIGURATION_NAME}

FROM mcr.microsoft.com/dotnet/aspnet:6.0.11-bullseye-slim
ENV LC_ALL en_US.UTF-8
ENV LANG en_US.UTF-8
WORKDIR /datadir
WORKDIR /app
ENV PB_DATADIR=/datadir
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
VOLUME /datadir

ENV DEBIAN_FRONTEND=noninteractive 

# Install curl
RUN apt-get -qq update \
  && apt-get -qq install apt-transport-https ca-certificates curl gnupg lsb-release --no-install-recommends \
  && rm -rf /var/lib/apt/lists/*

# Install docker
ENV DOCKER_VER=20.10.21
RUN \
  curl -fsSL https://download.docker.com/linux/debian/gpg | gpg --dearmor -o /usr/share/keyrings/docker-archive-keyring.gpg \
  && echo "deb [arch=amd64 signed-by=/usr/share/keyrings/docker-archive-keyring.gpg] https://download.docker.com/linux/debian \
  $(lsb_release -cs) stable" | tee /etc/apt/sources.list.d/docker.list \
  && apt-get -qq update \
  && . /etc/os-release \
  && apt-get -qq install docker-ce=5:${DOCKER_VER}~3-0~${ID}-${VERSION_CODENAME} --no-install-recommends \
  && rm -rf /var/lib/apt/lists/*

COPY --from=builder "/app" .
ENTRYPOINT ["/app/PluginBuilder"]
