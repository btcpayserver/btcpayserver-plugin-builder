# Build isolation

Plugin builds execute repository-controlled code, including MSBuild targets and
commands invoked during compilation. The isolation boundary therefore treats a
build as untrusted execution, not just a compiler reading source files.

The web application no longer launches Docker containers. A private **build
broker** owns Docker access and runs each build in disposable sandboxes. Both
services can run on the same VM; this design does not start a VM per build.

## Components and trust boundaries

```text
Web application ─────────────────────────── PostgreSQL / Azure
      |
      | Authenticated internal HTTP API
      v
Build broker ────────────────────────────── Docker socket
      |
      | Creates resources for each build
      v
Internal build network                     Egress network
  ├── Checkout container                        |
  ├── Build worker                              |
  └── Squid proxy ───────────────────────────────┘
                                                |
                                      Allowed Git / NuGet hosts
```

| Component | Responsibility | Access |
| --- | --- | --- |
| Web application | User permissions, scheduling, persistent build status/logs and publication | PostgreSQL, Azure, broker token; no Docker socket |
| Build broker | Admission, container lifecycle, output staging and cleanup | Docker socket, private scratch directories, broker token; no application database or Azure credentials |
| Checkout / worker | Download source and compile the plugin | Assigned build files and restricted proxy access; no application credentials or Docker socket |
| Per-build proxy | Enforce outbound destination policy | Internal build network and egress network; no application credentials |

The broker is **trusted and privileged**: Docker access gives it control over
the host. It must not be publicly exposed. The authenticated API accepts build
parameters, not arbitrary Docker commands, host mounts or runtime options.
Those are selected by the broker.

The web application and broker authenticate with a shared token file, mounted
read-only into those two services only. The broker manages temporary build
leases; PostgreSQL remains the persistent source of build history. No separate
queue service or broker database is introduced.

## One build, from request to publication

1. **Admission.** The web application checks the build feature flag, user
   permissions/whitelist, rate limit and executor availability. The broker also
   validates the request and limits concurrent builds.
2. **Preparation.** The broker allocates a lease, private scratch directories,
   networks and a proxy. The web application rechecks whether the build is
   allowed before telling the prepared build to start.
3. **Checkout.** A disposable container fetches the repository through the proxy.
   Repository URLs must be anonymous HTTPS URLs on GitHub or GitLab. Git
   credentials are not passed into the sandbox.
4. **Compilation.** A separate worker receives read-only source and private
   writable build/output directories. Its writable NuGet cache belongs to this
   build, not to subsequent builds.
5. **Staging.** The worker is removed before output is handled by a separate
   staging container with no network. The staging path checks file types,
   paths and sizes; the broker validates manifest/build metadata and hashes.
   The artifact is still untrusted content, not executable broker code.
6. **Download.** The web application polls the authenticated broker API for
   status/logs and downloads the artifact into its own private staging area,
   checking its size and SHA-256.
7. **Cleanup, then publication.** The web application releases the broker lease
   and requires cleanup to be confirmed before uploading to Azure. Publication
   does not silently overwrite an existing artifact. The web application stores
   the final build result and discards its private download.

The private download separates publication from directories controlled by build
code. A hash verifies the transferred bytes; it does not prove the plugin is safe.

## Network isolation

Checkout and worker containers are attached only to the build's internal Docker
network, not the application network or an outbound network. The proxy is the
only build component attached to both internal and egress networks. Ignoring
`HTTP_PROXY` does not give the worker a direct internet route.

[The Squid policy](../PluginBuilder/squid.conf) allows only HTTPS CONNECT on port
443 to these exact hostnames:

- `github.com`, `www.github.com`
- `gitlab.com`, `www.gitlab.com`
- `api.nuget.org`, `globalcdn.nuget.org`, `www.nuget.org`

Unknown hostnames are rejected **before DNS resolution**, so the proxy does not
resolve arbitrary attacker-selected names. Allowed destinations are also checked
against blocked IP ranges, including private, loopback and link-local addresses
and cloud metadata addresses. There is no TLS interception: Git and NuGet still
validate HTTPS certificates.

Checkout/worker DNS is configured to an unreachable resolver. The proxy uses its
own resolver file, currently pointing to `1.1.1.1` and `1.0.0.1`. Those resolvers
must be reachable from the deployment network. A network that blocks them can
cause checkout/restore failures even if DNS works on the host.

The allowlist intentionally does not support arbitrary package feeds, self-hosted
Git servers or other build-time downloads. Extending it changes the security
policy and should be reviewed as such. It restricts destinations, not which
repository or content may be accessed on an allowed public service.

## What gVisor adds

Production uses Docker's `runsc` runtime for sandboxed execution. gVisor handles
the workload's system calls through its application kernel, reducing the host
kernel interface exposed to repository-controlled code. Docker still manages
container creation and removal; no full guest operating system is booted per build.

This is separate from network policy: **gVisor does not by itself block metadata
requests**. The internal network and proxy restrict destinations; gVisor adds a
boundary against container escape. Neither makes it safe to mount host secrets
or the Docker socket into a worker.

Workers also run as non-root with a read-only root filesystem, dropped Linux
capabilities, `no-new-privileges`, and CPU, memory, process and execution limits.
The broker admits at most two concurrent build leases. See
[BuildPolicy](../PluginBuilder.Builds/Services/BuildPolicy.cs) and
[DockerBuildSandbox](../PluginBuilder.BuildBroker/Services/DockerBuildSandbox.cs)
for the enforced bounds.

Local development explicitly permits `runc` for Docker Desktop. Production
rejects this option rather than silently falling back when gVisor is unavailable.
Use only trusted plugins in that development mode.

## Failures and recovery

- A compilation failure or timeout fails the build and triggers cleanup. It does
  not inherently require disabling the whole executor.
- If resource cleanup cannot be confirmed, the executor becomes unavailable and
  refuses new builds. It must not accept another build while an earlier sandbox
  may still be active.
- Leases expire, and cleanup is not cancelled merely because an HTTP client
  disconnects. Startup reconciles leftover managed resources before readiness.
- The web application monitors broker readiness. There is no fallback to running
  Docker locally when the broker is unavailable.

An unavailable executor should be investigated through broker logs and Docker
health. Resolve the underlying problem before restarting/reconciling it; simply
reenabling the application build flag does not establish safe cleanup.

Production deployment is documented in the
[infrastructure repository](https://github.com/btcpayserver/btcpayserver-plugin-builder-infra).

## Validation

For local setup, see the [local development guide](../PluginBuilder.Tests/README.md).

Unit/contract tests exercise admission, resource lifecycle, broker communication
and artifact handling. CI separately runs `ExecutorIntegration` tests on Linux
with gVisor, including real builds and negative network canaries. Successful local
`runc` builds are not evidence that production's `runsc` isolation works.

This design protects build infrastructure; it does **not** establish that a
generated plugin is safe to install. Plugin review is still necessary.

## Code map

| Entry point | What to read it for |
| --- | --- |
| [BuildService](../PluginBuilder/Services/BuildService.cs) | Application orchestration and publication |
| [RemoteBuildSandbox](../PluginBuilder/Services/RemoteBuildSandbox.cs) | Broker client, polling, download verification and lease release |
| [BuildBrokerApplication](../PluginBuilder.BuildBroker/BuildBrokerApplication.cs) | Internal HTTP endpoints and service wiring |
| [BrokerCoordinator](../PluginBuilder.BuildBroker/BrokerCoordinator.cs) | Admission, leases, execution and cleanup |
| [DockerBuildSandbox](../PluginBuilder.BuildBroker/Services/DockerBuildSandbox.cs) | Container options, networks, checkout and staging |
| [DockerStartupHostedService](../PluginBuilder.BuildBroker/HostedServices/DockerStartupHostedService.cs) | Runtime/image checks and startup reconciliation |
| [squid.conf](../PluginBuilder/squid.conf) | Outbound allowlist and IP restrictions |
| [PluginBuilder.Builds](../PluginBuilder.Builds) | Shared contracts and build policy |
