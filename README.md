# Introduction

This project hosts a server with a front end which can be used to build BTCPay Server plugins and store the binaries on some storage.

## Prerequisite:

It assumes you installed docker on your system.

## Configuration

All parameters are configured via environment variables.

* `PB_POSTGRES`: Connection to a postgres database (example: `User ID=postgres;Include Error Detail=true;Host=127.0.0.1;Port=61932;Database=blah`)
* `PB_STORAGE_CONNECTION_STRING`: Connection string to azure storage to store build results (example: `BlobEndpoint=http://127.0.0.1:32827/satoshi;AccountName=satoshi;AccountKey=Rxb41pUHRe+ibX5XS311tjXpjvu7mVi2xYJvtmq1j2jlUpN+fY/gkzyBMjqwzgj42geXGdYSbPEcu5i5wjSjPw==`)
* `ASPNETCORE_URLS`: The url the web server will be listening (example: `http://127.0.0.1:8080`)
* `PB_DATADIR`: Where some persistent data get saved (example: `/datadir`)

## API

### Get published versions

`HTTP GET /api/v1/plugins?btcpayVersion=1.2.3.4&includePreRelease=true`

List the published versions of the server compatible with `btcpayVersion`. (optionally include `includePreRelease`)

### Download a version

`HTTP GET /api/v1/plugins/{pluginSelector}/versions/{version}`

Download the binaries of the plugin.

`pluginSelector` can be either a plugin slug (example: `rockstar-stylist`) or a plugin identifier surrounded by brackets (example: `[BTCPayServer.Plugins.RockstarStylist]`).
