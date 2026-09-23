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
   permissions/whitelist and executor availability before submission. The broker
   validates the request and admits at most two leases, including retained results.
2. **Preparation.** The broker allocates a lease, private scratch directories,
   networks and a proxy, then starts execution automatically. There is no separate
   start authorization. Disabling new builds blocks submissions, not jobs already
   accepted by the broker (including jobs still preparing). A web-side job waiting
   for a submission slot is checked again before it is submitted.
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
6. **Cleanup and completion.** The broker copies the validated artifact into a
   private temporary file outside sandbox mounts, then disposes all sandbox
   resources. Only confirmed cleanup permits a successful result. The retained
   file is unlinked immediately and held only by the broker's open handle: no
   downloadable result survives a broker restart. At most two artifacts of
   256 MiB each can be retained, released after download or lease expiry (45 minutes
   from admission). A transfer that fails on the broker retains the file until
   consumption or expiry; the web client does not automatically retry it.
   A cleaned-up failed job is released when its final status/log page is returned,
   so retries do not require a client cleanup request. Unread failures expire.
   There is no HTTP cancellation endpoint; lease release is internal to the broker.
7. **Download.** The web application polls the authenticated broker API for
   status/logs and downloads the artifact into its own private staging area,
   checking its size and SHA-256.
8. **Publication.** The web application uploads its verified copy to Azure without
   requesting or coordinating sandbox cleanup. Once download and validation finish,
   broker unavailability or restart no longer interrupts publication; application
   shutdown still cancels the upload. Publication
   does not silently overwrite an existing artifact. After discarding its private
   download and awaiting best-effort log persistence, the web application commits
   the version mapping, artifact URL and successful build state in one database transaction, then emits
   the completion event. Log persistence failures are logged operationally but do not
   invalidate a completed build.

The private download separates publication from directories controlled by build
code. A hash verifies the transferred bytes; it does not prove the plugin is safe.

## Network isolation

Checkout and worker containers are attached only to the build's internal Docker
network, not the application network or an outbound network. The proxy is the
only build component attached to both internal and egress networks. Ignoring
`HTTP_PROXY` does not give the worker a direct internet route.

The proxy runs Canonical's `ubuntu/squid` image, pinned by digest in
[DockerBuildSandbox](../PluginBuilder.BuildBroker/Services/DockerBuildSandbox.cs).
The broker owns [the Squid policy](../PluginBuilder.BuildBroker/squid.conf) and
mounts it read-only into each proxy. It allows only HTTPS CONNECT on port 443 to
these exact hostnames:

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

- Checkout and artifact validation failures expose only fixed, safe diagnostics,
  not arbitrary Docker errors or host paths. Pending log pages are drained without
  the idle polling delay.
- Lease release allows up to ten minutes for cancellation and cleanup together;
  scratch deletion alone has a five-minute deadline. Successful cleanup is not
  reclassified as a cleanup failure if the executor is cancelled afterwards.
- The broker checks Docker every 15 seconds with a 10-second probe deadline.
  Three consecutive probe timeouts suspend new admission without cancelling
  accepted jobs. Probes continue; the first success resumes the same live
  generation. A successful probe resets the timeout count.
- Only probe timeouts recover automatically. A nonzero Docker exit (including
  connection failures), an execution error, or unconfirmed cleanup still blocks
  the executor until investigation and startup reconciliation. A late successful
  probe cannot undo that block or reopen admission during shutdown.
- This does not remove resource pressure: individual build operations and cleanup
  keep their own deadlines, and cleanup failure can still require intervention.
- A compilation failure or timeout fails the build and triggers cleanup. It does
  not inherently require disabling the whole executor.
- If resource cleanup cannot be confirmed, the executor becomes unavailable and
  refuses new builds. It must not accept another build while an earlier sandbox
  may still be active.
- Leases expire, and cleanup is not cancelled merely because an HTTP client
  disconnects. Startup reconciles leftover managed resources before readiness.
- Stopping the web client cancels its local polling/downloads, not the accepted
  broker job. Broker deadlines and shutdown own sandbox cancellation and cleanup.
  To interrupt accepted work operationally, stop the executor, not just the website.
- The web application monitors broker readiness for **new admission**. Any failed
  health probe, invalid response or "not ready" status immediately suspends new
  submissions without cancelling accepted builds. A ready response from the same
  instance restores admission without replacing their cancellation token. A POST
  already in flight may still be accepted after admission is suspended.
- A valid status confirming a different broker instance cancels polling/downloads
  tied to the old instance, even if the replacement is not ready yet. Changes to
  image IDs alone are not treated as a restart. There is no local Docker fallback.
- Each accepted build retains its own request deadlines, instance checks and
  45-minute lifetime. A failed build-status request or artifact download still fails
  that build, not all builds. The client does not retry submissions, build-status
  requests or downloads; a lost response can leave a broker slot held until lease
  expiry. Health recovery is not a guarantee of recovery from every network failure.

Investigate persistent unavailability through broker logs and Docker health.
Timeout-only suspension can recover without a restart. For a definitive failure,
resolve the underlying problem before restarting/reconciling the executor;
simply reenabling the application build flag does not establish safe cleanup.

Production deployment is documented in the
[infrastructure repository](https://github.com/btcpayserver/btcpayserver-plugin-builder-infra).

## Validation

For local setup, see the [local development guide](../PluginBuilder.Tests/README.md).

Unit/contract tests exercise admission, resource lifecycle, broker communication
and artifact handling. Docker command fakes test our orchestration and responses
to failures, not Docker's actual networking, runtime enforcement or resource removal.
Worker argument policy is checked in `BuildSandboxContractTests`; lifecycle tests
check how per-build resources are wired, sequenced and cleaned up.
CI separately runs `ExecutorIntegration` tests on Linux
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
| [squid.conf](../PluginBuilder.BuildBroker/squid.conf) | Outbound allowlist and IP restrictions |
| [PluginBuilder.Builds](../PluginBuilder.Builds) | Shared contracts and build policy |
