# Introduction

This project hosts a server with a front end which can be used to build BTCPay Server plugins and store the binaries on some storage.
You can find our live server on [https://plugin-builder.btcpayserver.org/](https://plugin-builder.btcpayserver.org/), that is updated through
[btcpayserver-infra](https://github.com/btcpayserver/btcpayserver-infra) repository.

## Prerequisite

It assumes you installed docker on your system.

## Configuration

All parameters are configured via environment variables.

* `PB_POSTGRES`: Connection to a postgres database (example: `User ID=postgres;Include Error Detail=true;Host=127.0.0.1;Port=61932;Database=btcpayplugin`)
* `PB_STORAGE_CONNECTION_STRING`: Connection string to azure storage to store build results (example: `BlobEndpoint=http://127.0.0.1:32827/satoshi;AccountName=satoshi;AccountKey=Rxb41pUHRe+ibX5XS311tjXpjvu7mVi2xYJvtmq1j2jlUpN+fY/gkzyBMjqwzgj42geXGdYSbPEcu5i5wjSjPw==`)
* `PB_CHEAT_MODE`: If set to `true`, it's considered that the server is running in a development environment and will allow to bypass some security checks (right now only registering admin account).
* `ASPNETCORE_URLS`: The url the web server will be listening (example: `http://127.0.0.1:8080`)
* `PB_DATADIR`: Where some persistent data get saved (example: `/datadir`)

## API

Full interactive API documentation is available at [`/docs`](https://plugin-builder.btcpayserver.org/docs) on the live server.

The OpenAPI specification is available at [`/swagger/v1/swagger.json`](https://plugin-builder.btcpayserver.org/swagger/v1/swagger.json).

Some endpoints require HTTP Basic Auth using your login email and password:

```bash
curl --user "email:password" https://plugin-builder.btcpayserver.org/api/v1/plugins/{pluginSlug}/builds/{buildId}
```
