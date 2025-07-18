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

### Public/Unauthenticated endpoints

#### Get published versions

`HTTP GET /api/v1/plugins?btcpayVersion=1.2.3.4&includePreRelease=true&includeAllVersions=false`

List the published versions of the server compatible with `btcpayVersion`. (optionally include `includePreRelease`)

If `includeAllVersions` is set to `true`, all versions will be returned, otherwise only the latest version for each plugin will be returned.

* `searchPluginIdentifier`: Query parameter to search by plugin identifier
* `searchPluginName`: Query parameter to search by plugin slug or name. If it is formatted as `[abc]`, it will be converted to `searchPluginIdentifier=abc`.

#### Get a version

`HTTP GET /api/v1/plugins/{pluginSelector}/versions/{version}`

`pluginSelector` can be either a plugin slug (example: `rockstar-stylist`) or a plugin identifier surrounded by brackets (example: `[BTCPayServer.Plugins.RockstarStylist]`).

#### Download a version

`HTTP GET /api/v1/plugins/{pluginSelector}/versions/{version}/download`

Download the binaries of the plugin.

### Authenticated endpoints

The following endpoints require HTTP Basic Auth.
Use your login email and password to provide the HTTP `Authorization` header like this:
`Authorization: Basic {credentials}`, where `{credentials}` is the base64 encoded form of `email:password` (note the `:` as delimiter).
See the cURL examples below.

#### Get build details

`HTTP GET /api/v1/plugins/{pluginSelector}/builds/{buildId}`

Get the details for a specific plugin build.

Sample cURL request:

```bash
curl --user "$EMAIL:$PASSWORD" -X GET -H "Content-Type: application/json" \
     "https://plugin-builder.btcpayserver.org/api/v1/plugins/{pluginSelector}/builds/{buildId}"
```

Sample response:

```json
{
    "projectSlug": "lnbank",
    "buildId": 8,
    "buildInfo": {
        "gitRepository": "https://github.com/dennisreimann/btcpayserver-plugin-lnbank",
        "gitRef": "v1.5.1",
        "pluginDir": "BTCPayServer.Plugins.LNbank",
        "gitCommit": "4f0548cbc22d5a91493ef9a42db41a066251622a",
        "gitCommitDate": "2023-05-18T21:46:37+02:00",
        "buildDate": "2023-05-23T18:02:06+02:00",
        "buildHash": "c63a96792aafc80bedb067cd554667cfbd235cadb43aeceda39166c8018b6001",
        "url": "https://plugin-builder.btcpayserver.org/satoshi/artifacts/lnbank/8/BTCPayServer.Plugins.LNbank.btcpay",
        "error": null,
        "buildConfig": "Release",
        "assemblyName": "BTCPayServer.Plugins.LNbank",
        "additionalObjects": null
    },
    "manifestInfo": {
        "identifier": "BTCPayServer.Plugins.LNbank",
        "name": "LNbank",
        "version": "1.5.1",
        "description": "Use the BTCPay Server Lightning node in custodial mode and give users access via custodial layer 3 wallets.",
        "systemPlugin": false,
        "dependencies": [
            {
                "identifier": "BTCPayServer",
                "condition": ">=1.9.0"
            }
        ]
    },
    "createdDate": "2023-05-23T16:01:26.949327+00:00",
    "downloadLink": "https://plugin-builder.btcpayserver.org/satoshi/artifacts/lnbank/8/BTCPayServer.Plugins.LNbank.btcpay",
    "published": true,
    "prerelease": false,
    "commit": "4f0548cb",
    "repository": "https://github.com/dennisreimann/btcpayserver-plugin-lnbank",
    "gitRef": "v1.5.1"
}
```

#### Create a new build

`HTTP POST /api/v1/plugins/{pluginSelector}/builds`

Create a new build by specifying the `gitRepository` (required), `gitRef` (optional, default: `master`), `pluginDirectory` (optional) and `buildConfig` (optional, default: `Release`).

Sample cURL request:

```bash
curl --user "$EMAIL:$PASSWORD" -X POST -H "Content-Type: application/json" \
     -d "{'gitRepository': 'https://github.com/dennisreimann/btcpayserver-plugin-lnbank', 'gitRef': 'v1.5.1', 'pluginDirectory': 'BTCPayServer.Plugins.LNbank' }" \
     "https://plugin-builder.btcpayserver.org/api/v1/plugins/{pluginSelector}/builds/{buildId}"
```

Sample response:

```json
{
    "pluginSlug": "lnbank",
    "buildId": 12,
    "buildUrl": "https://plugin-builder.btcpayserver.org/plugins/lnbank/builds/12"
}
```
