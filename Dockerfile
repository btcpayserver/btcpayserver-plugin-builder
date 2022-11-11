FROM mcr.microsoft.com/dotnet/sdk:6.0.403-bullseye-slim AS builder
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

WORKDIR /source
COPY PluginBuilder/. PluginBuilder/.

ARG CONFIGURATION_NAME=Release
RUN cd PluginBuilder && dotnet publish --output /app/ --configuration ${CONFIGURATION_NAME}


FROM mcr.microsoft.com/dotnet/aspnet:6.0.11-bullseye-slim
ENV LC_ALL en_US.UTF-8
ENV LANG en_US.UTF-8
WORKDIR /datadir
WORKDIR /app
ENV PB_DATADIR=/datadir
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
VOLUME /datadir

COPY --from=builder "/app" .
ENTRYPOINT ["/app/PluginBuilder"]