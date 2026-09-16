FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

WORKDIR /source
COPY PluginBuilder.Builds/. PluginBuilder.Builds/.
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

# The public application calls the restricted build broker over HTTP. It has no
# Docker client and must not receive a Docker socket or host scratch mounts.
RUN apt-get -qq update \
  && apt-get -y -qq install ca-certificates curl --no-install-recommends \
  && rm -rf /var/lib/apt/lists/*

COPY --from=builder "/app" .
ENTRYPOINT ["/app/PluginBuilder"]
