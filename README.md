# Introduction

This project hosts a server with a front end which can be used to build BTCPay Server plugins and store the binaries on some storage.
You can find our live server on [https://plugin-builder.btcpayserver.org/](https://plugin-builder.btcpayserver.org/), that is updated through
[btcpayserver-plugin-builder-infra](https://github.com/btcpayserver/btcpayserver-plugin-builder-infra) repository.

## Build architecture

[Build isolation](docs/build-isolation.md) explains the web application/broker boundary,
the build lifecycle, gVisor, restricted networking, artifact publication and failure handling.
For local setup, see [local development](PluginBuilder.Tests/README.md).

## Prerequisite

The public application requires PostgreSQL, artifact storage, and the internal
build broker. Docker and the `runsc` runtime belong on the broker's Linux host,
not in the public application container. The [infrastructure README](https://github.com/btcpayserver/btcpayserver-plugin-builder-infra#prerequisites)
covers host preparation, firewall rules, deployment and migration.

## Configuration

All parameters are configured via environment variables.

* `PB_POSTGRES`: Connection to a postgres database (example: `User ID=postgres;Include Error Detail=true;Host=127.0.0.1;Port=61932;Database=btcpayplugin`)
* `PB_STORAGE_CONNECTION_STRING`: Connection string to azure storage to store build results (example: `BlobEndpoint=http://127.0.0.1:32827/satoshi;AccountName=satoshi;AccountKey=Rxb41pUHRe+ibX5XS311tjXpjvu7mVi2xYJvtmq1j2jlUpN+fY/gkzyBMjqwzgj42geXGdYSbPEcu5i5wjSjPw==`)
* `PB_BUILD_BROKER_URL`: Internal broker URL (for example, `http://build-broker:8080`). The API does not fall back to local Docker if the broker is absent or unavailable.
* `PB_BUILD_BROKER_TOKEN_FILE`: Path to the shared broker authentication token file. Deployments require an absolute path mounted read-only into only the application and broker. The development profile resolves its relative fixture path against the project directory. Do not put a production token value in environment variables or source control.
* `PB_CHEAT_MODE`: If set to `true`, it's considered that the server is running in a development environment and will allow to bypass some security checks (right now only registering admin account).
* `PB_ENABLE_LOCAL_ARTIFACT_DOWNLOAD_PROXY`: If set to `true`, loopback artifact URLs can be proxied through the API download endpoint for local development.
* `ASPNETCORE_URLS`: The url the web server will be listening (example: `http://127.0.0.1:8080`)
* `XDG_CONFIG_HOME`: Parent of the application's persistent data directory on Linux (example: `/datadir`, resulting in `/datadir/BTCPayServer-PluginBuilder`). Keep deployment mounts, including the private broker-download buffer, aligned with this setting.

## API

[Admin events and notifications](docs/admin-events.md) documents the admin polling API,
email/webhook subscriptions, signatures, delivery retries, and local agent cursors.
[Admin agent access](docs/admin-agent-access.md) covers revocable tokens, token activity
audits, user/build inspection, listing review and the local connection helper.

Full interactive API documentation is available at [`/docs`](https://plugin-builder.btcpayserver.org/docs) on the live server.

The OpenAPI specification is available at [`/swagger/v1/swagger.json`](https://plugin-builder.btcpayserver.org/swagger/v1/swagger.json).

Some endpoints require HTTP Basic Auth using your login email and password:

```bash
curl --user "email:password" https://plugin-builder.btcpayserver.org/api/v1/plugins/{pluginSlug}/builds/{buildId}
```
