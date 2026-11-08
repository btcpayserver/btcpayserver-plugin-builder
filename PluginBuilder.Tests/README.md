# Local development

With .NET 10 and Docker running Linux containers, start dependencies from the repository root:

```sh
docker compose -f PluginBuilder.Tests/docker-compose.yml up --build -d
```

Then run the `PluginBuilder` application's `Debug Profile` in Rider. Its broker URL and development token are already configured.

The administrative API and its OpenAPI contract can be tested separately:

```sh
dotnet test PluginBuilder.Tests --filter 'FullyQualifiedName~AdminAgentApiTests|FullyQualifiedName~AdminApiDocumentationTests'
```

These tests create disposable databases in the test PostgreSQL instance. Application-log cases use temporary fictional files and cover admin-token access/revocation, audit, file selection, bounded UTF-8 tail pagination, growing/truncated files and unavailable logging.

The local broker uses `runc`, so gVisor is not required. Checkout, builds, proxy access and artifact handling use the real build pipeline, but **only build trusted plugins in this mode**: it does not provide production's gVisor isolation. Production rejects `PBB_USE_RUNC=true`.

Scratch files live in a Docker volume, including on Docker Desktop. Worker, broker and storage images use `linux/amd64`, and the broker pulls the pinned upstream Squid proxy image for the same platform; ARM machines need Docker's amd64 emulation enabled.

To test gVisor on a Linux Docker host with `runsc` installed, set `PBB_USE_RUNC=false` when starting Compose. Linux/runsc isolation tests remain separate from ordinary local development.
