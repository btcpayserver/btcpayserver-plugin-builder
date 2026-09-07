# Build isolation deployment requirements

Plugin repositories and their build logic are untrusted. A build is split into
disposable gVisor containers for source checkout, compilation, artifact
staging, and cleanup. After both the worker and the trusted stager have been
removed, the application reads the bounded canonical metadata locally and uploads
the staged artifact through the Azure SDK. The worker can reach only a per-build Squid proxy; the
proxy permits anonymous HTTPS CONNECT requests to public GitHub, GitLab, and
the official NuGet endpoints. The worker never receives the Azure credential,
application data, PostgreSQL access, or the Docker socket.

The application deliberately keeps builds unavailable if any prerequisite or
startup smoke test fails.

## Host prerequisites

Before permitting any production builds, including whitelist exceptions:

1. Install and configure the Docker `runsc` runtime. Do not configure a fallback
   to `runc`; all checkout, worker, staging, and cleanup helpers use
   `--runtime runsc` explicitly.
2. Provision two dedicated scratch filesystems under a parent directory:
   `slot-0` and `slot-1`. Set the parent path as `PB_BUILD_SCRATCH_ROOT` and
   expose the parent **and both mounted children** inside the application
   container at exactly the same absolute paths as on the Docker host. Mount
   both filesystems before creating the application container, and verify its
   view includes both mounts. Creating ordinary directories, or bind-mounting
   two directories from one filesystem, does not provide isolation between slots.
3. Each slot must have a different filesystem device ID from the other slot
   and `PB_DATADIR`. Each must be at most 16 GiB, have at least 4 GiB free,
   at most 65,536 total inodes, and at least 8,192 free inodes. An 8–10 GiB
   filesystem per slot is the intended production size. A build exclusively
   owns one slot until its scratch cleanup succeeds, so exhausting that slot's
   blocks or inodes cannot consume the other build's capacity. Do not store any
   persistent or secret data in these filesystems. If using loopback images,
   fully preallocate both backing files before use (not sparse `truncate` files),
   reserving their combined capacity on the backing filesystem. Disable discard
   when formatting (`mkfs.ext4 -E nodiscard`) and do not TRIM these loopback
   filesystems, since discard can turn the reserved files sparse again. Distinct device
   IDs alone do not prevent shared backing storage from being overcommitted.
4. At the host firewall layer, deny container traffic to OpenStack metadata
   (`169.254.0.0/16`) and other link-local destinations. Also deny routed Docker
   traffic to host/LAN/service networks and the public IPs of sensitive BTCPay
   services, while preserving the per-build worker-to-proxy connection on TCP
   3128 and the proxy's required public HTTPS egress. Only the proxy is given a
   read-only resolver file that points directly to `1.1.1.1` and `1.0.0.1`;
   Docker's embedded resolver is deliberately bypassed because it is not
   reachable from `runsc` on a user-defined bridge. Permit UDP and TCP port 53
   to exactly those two addresses from the proxy's per-build egress bridge.
   Deny other DNS egress from the build networks. This is also required defense
   in depth if the proxy is ever compromised. Checkout and worker containers
   use only the proxy's numeric internal address and must not receive public
   DNS access. Scope these rules to build networks: the trusted application
   still needs its own DNS and HTTPS access to Azure for SDK uploads, and its
   other configured services. There is no Azure CLI or upload helper container.
5. Keep PostgreSQL, the Docker API, and internal application ports unpublished.
   Only the reverse proxy should expose the public HTTP(S) ports.

For a read-only check on the Linux host, substitute the actual paths below:

```sh
pb_scratch_root='/path/on/host/configured/as/PB_BUILD_SCRATCH_ROOT'
pb_data_dir='/path/on/host/to/application-data'

mountpoint -- "$pb_scratch_root/slot-0"
mountpoint -- "$pb_scratch_root/slot-1"
stat --printf='%n device=%d\n' -- \
  "$pb_scratch_root/slot-0" "$pb_scratch_root/slot-1" "$pb_data_dir"
df -B1 -- "$pb_scratch_root/slot-0" "$pb_scratch_root/slot-1"
df -i -- "$pb_scratch_root/slot-0" "$pb_scratch_root/slot-1"
```

Both `mountpoint` commands must succeed and the three device IDs must differ.
Also verify the application container sees the mounted slot filesystems; a
parent bind mount established before the child mounts may not include them.
Startup runs a read-only gVisor probe against each slot to verify the Docker
host shares the path and the capacity/inode bounds are met. Startup does not
install or validate the host firewall rules; deployment must do that separately.

The public web application currently still mounts the Docker socket because it
orchestrates the disposable sandboxes. Treat removal of that socket in favor of
a small privileged broker as required follow-up hardening; it is not exposed to
any build container.

## Required canary

The executor CI includes a negative network canary with benign, controlled
local DNS/TCP fixtures and no Internet route from its fixture network. It uses
the actual worker/proxy network arguments to check direct-access denial, proxy
ACLs, blocked-destination DNS queries, and cleanup; it does not contact real
metadata services or public target hosts. The positive GitLab shell test uses
a fake `git` executable to validate checkout arguments. These checks do not prove
that production firewall rules are installed, or that real GitLab checkout,
public DNS/NuGet egress, and Azure upload work on the deployment host. The
Linux/runsc executor tests also need to be run; ordinary local unit tests do
not substitute for them.

Use a benign repository controlled by the project. Do not reuse incident or
attacker code. Before enabling production builds, confirm all of the following:

- a normal GitHub checkout and official NuGet restore succeed;
- a normal GitLab checkout succeeds;
- among build containers, only the proxy can resolve through `1.1.1.1` and `1.0.0.1`; checkout and
  worker containers cannot resolve names or reach either resolver directly;
- OpenStack metadata, the Docker host gateway, PostgreSQL, the Plugin
  Builder application, Mattermost, RFC1918 networks, and an arbitrary public
  host are unreachable from the worker, and the proxy rejects loopback destinations;
- a denied `CONNECT` to a nonce under a controlled domain does not produce a
  DNS query at its authoritative server or resolver (a failed HTTP request
  alone does not prove that the DNS exfiltration path is closed);
- the Azure credential is absent from checkout and worker environments;
- a valid `.btcpay` is staged and uploaded only after the worker is removed;
- the trusted application's Azure SDK upload succeeds with the production
  firewall active;
- a benign build exhausting one scratch slot's block or inode limit cannot
  exhaust the other slot; the other build still completes and cleanup recovers
  the exhausted slot;
- the recorded Git commit matches the pristine checkout and the artifact hash
  matches the uploaded bytes;
- worker, checkout, proxy, staging, and cleanup containers, both job
  networks, and the job scratch directory are gone after success, failure, and
  timeout;
- stopping the application terminates active build resources immediately.

Only then permit production builds through the whitelist or by setting
`NewBuildsEnabled=true`. With `NewBuildsEnabled=false`, whitelisted accounts can
still submit builds; that flag alone is not a global stop. Whitelist exceptions
do not bypass executor readiness, sandbox isolation, or resource/rate limits.
Registration is controlled independently by `RegistrationEnabled`.
