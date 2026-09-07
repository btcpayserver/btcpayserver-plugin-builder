using Microsoft.Extensions.Logging.Abstractions;
using PluginBuilder.Services;
using Xunit;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class BuildScratchCleanerTests
{
    private const string WorkerImageId =
        "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public async Task HostileTreeIsDeletedOnlyThroughHardenedRunscContainer()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create();
        var scratch = CreateScratch(fakeDocker.Directory);
        var work = Path.Combine(scratch, "work");
        var current = work;
        for (var i = 0; i < 48; i++)
        {
            current = Path.Combine(current, $"level-{i:D2}");
            Directory.CreateDirectory(current);
        }

        await File.WriteAllTextAsync(Path.Combine(current, "attacker-controlled"), "payload");
        File.SetUnixFileMode(
            Path.GetDirectoryName(current)!,
            UnixFileMode.None);

        var cleaner = CreateCleaner();
        Assert.True(await cleaner.TryDeleteAsync(scratch, WorkerImageId));
        Assert.False(Directory.Exists(scratch));

        var commands = await fakeDocker.ReadCommands();
        var create = Assert.Single(
            commands,
            command => command.StartsWith("container create --name pb-scratch-clean-", StringComparison.Ordinal));
        Assert.Contains(
            $"--label {BuildExecutorDocker.ManagedResourceLabel}=scratch-cleanup",
            create,
            StringComparison.Ordinal);
        Assert.Contains("--runtime runsc", create, StringComparison.Ordinal);
        Assert.Contains("--network none", create, StringComparison.Ordinal);
        Assert.Contains("--read-only", create, StringComparison.Ordinal);
        Assert.Contains("--user 0:0", create, StringComparison.Ordinal);
        Assert.Contains("--cap-drop ALL", create, StringComparison.Ordinal);
        Assert.Contains("--cap-add DAC_OVERRIDE", create, StringComparison.Ordinal);
        Assert.Contains("--cap-add FOWNER", create, StringComparison.Ordinal);
        Assert.Equal(2, create.Split("--cap-add ", StringSplitOptions.None).Length - 1);
        Assert.Contains("--security-opt no-new-privileges:true", create, StringComparison.Ordinal);
        Assert.Contains("--memory 256m --memory-swap 256m", create, StringComparison.Ordinal);
        Assert.Contains("--cpus 2", create, StringComparison.Ordinal);
        Assert.Contains("--pids-limit 32", create, StringComparison.Ordinal);
        Assert.Contains("--ulimit nofile=128:128", create, StringComparison.Ordinal);
        Assert.Contains("--log-driver none", create, StringComparison.Ordinal);
        Assert.Contains(
            $"--mount type=bind,source={Path.Combine(scratch, "source")},target=/source",
            create,
            StringComparison.Ordinal);
        Assert.Contains(
            $"--mount type=bind,source={Path.Combine(scratch, "work")},target=/work",
            create,
            StringComparison.Ordinal);
        Assert.Contains(
            $"--mount type=bind,source={Path.Combine(scratch, "output")},target=/output",
            create,
            StringComparison.Ordinal);
        Assert.Contains(
            $"--mount type=bind,source={Path.Combine(scratch, "staging")},target=/staging",
            create,
            StringComparison.Ordinal);
        Assert.Equal(4, create.Split("--mount ", StringSplitOptions.None).Length - 1);
        Assert.Contains(
            $"--entrypoint /usr/bin/find {WorkerImageId} /source /work /output /staging -xdev -mindepth 1 -delete",
            create,
            StringComparison.Ordinal);
        Assert.DoesNotContain("/var/run/docker.sock", create, StringComparison.Ordinal);
        Assert.DoesNotContain("--env", create, StringComparison.Ordinal);

        var start = Assert.Single(
            commands,
            command => command.StartsWith("container start --attach pb-scratch-clean-", StringComparison.Ordinal));
        var remove = Assert.Single(
            commands,
            command => command.StartsWith("container rm --force pb-scratch-clean-", StringComparison.Ordinal));
        Assert.True(commands.IndexOf(create) < commands.IndexOf(start));
        Assert.True(commands.IndexOf(start) < commands.IndexOf(remove));
    }

    [Fact]
    public async Task CleanerStartFailureStillForceRemovesContainerAndLeavesScratch()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(failStart: true);
        var scratch = CreateScratch(fakeDocker.Directory);
        await File.WriteAllTextAsync(Path.Combine(scratch, "work", "file"), "payload");

        Assert.False(await CreateCleaner().TryDeleteAsync(scratch, WorkerImageId));
        Assert.True(Directory.Exists(scratch));
        Assert.True(File.Exists(Path.Combine(scratch, "work", "file")));

        var commands = await fakeDocker.ReadCommands();
        Assert.Contains(
            commands,
            command => command.StartsWith("container start --attach pb-scratch-clean-", StringComparison.Ordinal));
        Assert.Contains(
            commands,
            command => command.StartsWith("container rm --force pb-scratch-clean-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanerRemovalFailureMakesSuccessfulDirectoryDeletionFailClosed()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(failRemove: true);
        var scratch = CreateScratch(fakeDocker.Directory);

        Assert.False(await CreateCleaner().TryDeleteAsync(scratch, WorkerImageId));
        Assert.False(Directory.Exists(scratch));

        Assert.Contains(
            await fakeDocker.ReadCommands(),
            command => command.StartsWith("container rm --force pb-scratch-clean-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AmbiguousCreateRetriesRemovalWhenCleanupContainerAppearsAfterInitialNotFound()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(ambiguousCreate: true);
        var scratch = CreateScratch(fakeDocker.Directory);
        await File.WriteAllTextAsync(Path.Combine(scratch, "work", "file"), "payload");
        using var cancellation = new CancellationTokenSource();
        var cleanup = CreateCleaner().TryDeleteAsync(scratch, WorkerImageId, cancellation.Token);

        await fakeDocker.WaitForAmbiguousCreate();
        cancellation.Cancel();

        Assert.False(await cleanup);

        var commands = await fakeDocker.ReadCommands();
        var create = Assert.Single(
            commands,
            command => command.StartsWith("container create --name pb-scratch-clean-", StringComparison.Ordinal));
        var containerName = create.Split(' ', StringSplitOptions.RemoveEmptyEntries)[3];
        Assert.Equal(
            2,
            commands.Count(command => command == $"container rm --force {containerName}"));
        Assert.False(fakeDocker.AmbiguousContainerExists);
        Assert.DoesNotContain($"container start --attach {containerName}", commands);
        Assert.True(Directory.Exists(scratch));
        Assert.True(File.Exists(Path.Combine(scratch, "work", "file")));
    }

    [Fact]
    public async Task SymbolicLinkChildIsRejectedBeforeItCanBeMounted()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create();
        var scratch = Path.Combine(fakeDocker.Directory, $"pb-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        var outside = Path.Combine(fakeDocker.Directory, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(scratch, "work"), outside);
        Directory.CreateDirectory(Path.Combine(scratch, "output"));
        Directory.CreateDirectory(Path.Combine(scratch, "staging"));

        Assert.False(await CreateCleaner().TryDeleteAsync(scratch, WorkerImageId));

        var commands = await fakeDocker.ReadCommands();
        Assert.DoesNotContain(
            commands,
            command => command.StartsWith("container create ", StringComparison.Ordinal));
        Assert.Contains(
            commands,
            command => command.StartsWith("container rm --force pb-scratch-clean-", StringComparison.Ordinal));
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public async Task MissingScratchDirectoryNeedsNoDockerOperation()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create();
        var missing = Path.Combine(fakeDocker.Directory, "missing");

        Assert.True(await CreateCleaner().TryDeleteAsync(missing, WorkerImageId));
        Assert.Empty(await fakeDocker.ReadCommands());
    }

    private static BuildScratchCleaner CreateCleaner()
    {
        return new BuildScratchCleaner(
            NullLogger<BuildScratchCleaner>.Instance,
            new ProcessRunner(NullLogger<ProcessRunner>.Instance));
    }

    private static string CreateScratch(string root)
    {
        var scratch = Path.Combine(root, $"pb-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(scratch, "source"));
        Directory.CreateDirectory(Path.Combine(scratch, "work"));
        Directory.CreateDirectory(Path.Combine(scratch, "output"));
        Directory.CreateDirectory(Path.Combine(scratch, "staging"));
        return scratch;
    }

    private sealed class FakeDocker : IAsyncDisposable
    {
        private readonly Dictionary<string, string?> _originalEnvironment;

        private FakeDocker(string directory, Dictionary<string, string?> originalEnvironment)
        {
            Directory = directory;
            _originalEnvironment = originalEnvironment;
        }

        public string Directory { get; }

        public static async Task<FakeDocker> Create(
            bool failStart = false,
            bool failRemove = false,
            bool ambiguousCreate = false)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"plugin-builder-cleaner-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var dockerPath = Path.Combine(directory, "docker");
            await File.WriteAllTextAsync(dockerPath, """
                #!/bin/sh
                set -eu
                state="${PB_FAKE_DOCKER_STATE:?}"
                printf '%s\n' "$*" >> "$state/commands"

                case "$1:$2" in
                    container:create)
                        previous=""
                        name=""
                        for argument in "$@"; do
                            if [ "$previous" = "--name" ]; then name="$argument"; fi
                            case "$argument" in
                                type=bind,source=*,target=/work)
                                    source="${argument#type=bind,source=}"
                                    printf '%s\n' "${source%,target=/work}" >> "$state/mounts.$name"
                                    ;;
                                type=bind,source=*,target=/source)
                                    source="${argument#type=bind,source=}"
                                    printf '%s\n' "${source%,target=/source}" >> "$state/mounts.$name"
                                    ;;
                                type=bind,source=*,target=/output)
                                    source="${argument#type=bind,source=}"
                                    printf '%s\n' "${source%,target=/output}" >> "$state/mounts.$name"
                                    ;;
                                type=bind,source=*,target=/staging)
                                    source="${argument#type=bind,source=}"
                                    printf '%s\n' "${source%,target=/staging}" >> "$state/mounts.$name"
                                    ;;
                            esac
                            previous="$argument"
                        done
                        [ -n "$name" ]
                        if [ "${PB_FAKE_CLEANER_AMBIGUOUS_CREATE:-false}" = "true" ]; then
                            : > "$state/ambiguous-create-started"
                            sleep 60
                        fi
                        ;;
                    container:start)
                        target=""
                        for argument in "$@"; do target="$argument"; done
                        if [ "${PB_FAKE_CLEANER_START_FAIL:-false}" = "true" ]; then
                            exit 19
                        fi
                        while IFS= read -r source; do
                            chmod -R u+rwx "$source"
                            find "$source" -xdev -mindepth 1 -delete
                        done < "$state/mounts.$target"
                        ;;
                    container:rm)
                        if [ "${PB_FAKE_CLEANER_AMBIGUOUS_CREATE:-false}" = "true" ]; then
                            if [ ! -f "$state/ambiguous-remove-attempted" ]; then
                                : > "$state/ambiguous-remove-attempted"
                                : > "$state/ambiguous-container-exists"
                                printf '%s\n' 'Error response from daemon: No such container' >&2
                                exit 1
                            fi
                            rm -f "$state/ambiguous-container-exists"
                        fi
                        if [ "${PB_FAKE_CLEANER_REMOVE_FAIL:-false}" = "true" ]; then
                            printf '%s\n' 'simulated removal failure' >&2
                            exit 23
                        fi
                        ;;
                    *)
                        exit 2
                        ;;
                esac
                """);
            File.SetUnixFileMode(
                dockerPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            string[] keys =
            [
                "PATH",
                "PB_FAKE_DOCKER_STATE",
                "PB_FAKE_CLEANER_START_FAIL",
                "PB_FAKE_CLEANER_REMOVE_FAIL",
                "PB_FAKE_CLEANER_AMBIGUOUS_CREATE"
            ];
            var originalEnvironment = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
            Environment.SetEnvironmentVariable(
                "PATH",
                directory + Path.PathSeparator + originalEnvironment["PATH"]);
            Environment.SetEnvironmentVariable("PB_FAKE_DOCKER_STATE", directory);
            Environment.SetEnvironmentVariable(
                "PB_FAKE_CLEANER_START_FAIL",
                failStart ? "true" : "false");
            Environment.SetEnvironmentVariable(
                "PB_FAKE_CLEANER_REMOVE_FAIL",
                failRemove ? "true" : "false");
            Environment.SetEnvironmentVariable(
                "PB_FAKE_CLEANER_AMBIGUOUS_CREATE",
                ambiguousCreate ? "true" : "false");

            return new FakeDocker(directory, originalEnvironment);
        }

        public bool AmbiguousContainerExists =>
            File.Exists(Path.Combine(Directory, "ambiguous-container-exists"));

        public async Task WaitForAmbiguousCreate()
        {
            var marker = Path.Combine(Directory, "ambiguous-create-started");
            for (var attempt = 0; attempt < 500; attempt++)
            {
                if (File.Exists(marker))
                    return;
                await Task.Delay(10);
            }

            throw new TimeoutException("Fake docker create was not reached");
        }

        public async Task<List<string>> ReadCommands()
        {
            var path = Path.Combine(Directory, "commands");
            return File.Exists(path)
                ? (await File.ReadAllLinesAsync(path)).ToList()
                : [];
        }

        public ValueTask DisposeAsync()
        {
            foreach (var (key, value) in _originalEnvironment)
                Environment.SetEnvironmentVariable(key, value);

            System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
