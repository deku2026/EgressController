using System.Text.Json;
using EgressController.Core.Profile;
using Microsoft.Data.Sqlite;

namespace EgressController.Guardian.Tests;

public sealed class GuardPersistenceTests
{
    [Fact]
    public void Sqlite_reopens_policy_and_orphan_identities_but_never_a_ready_lease()
    {
        using var directory = new TemporaryDirectory();
        var first = new GuardStore(directory.Path);
        Assert.Null(first.LoadPolicy());
        first.SavePolicy(Fixture.Policy);
        first.SaveCheckpoint(new([new(Fixture.Process().Key, Fixture.Policy.Executables[0])], []));
        var second = new GuardStore(directory.Path);
        Assert.Equal(Fixture.Policy.Revision, second.LoadPolicy()!.Revision);
        Assert.Single(second.LoadCheckpoint().Processes);
        var processes = new FakeProcesses { Snapshot = [Fixture.Process()] };
        Assert.True(new GuardRuntime(second, processes, new FakeFacts(), []).Tick().Protecting);
        Assert.Single(processes.Killed);
    }

    [Fact]
    public void Invalid_policy_cannot_replace_committed_policy()
    {
        using var directory = new TemporaryDirectory();
        var store = new GuardStore(directory.Path);
        store.SavePolicy(Fixture.Policy);
        Assert.Throws<InvalidDataException>(() => store.SavePolicy(Fixture.Policy with { Executables = [new("relative.exe", "AI")] }));
        Assert.Equal(Fixture.Policy.Revision, store.LoadPolicy()!.Revision);
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("checkpoint")]
    public void Corrupt_persisted_json_is_not_a_fabricated_empty_selection(string key)
    {
        using var directory = new TemporaryDirectory();
        var store = new GuardStore(directory.Path);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory.Path, "guard.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO guard_state(name,value) VALUES($name,'broken json');";
        command.Parameters.AddWithValue("$name", key);
        command.ExecuteNonQuery();
        Assert.Throws<JsonException>(() => { if (key == "policy") store.LoadPolicy(); else store.LoadCheckpoint(); });
    }

    [Fact]
    public async Task Atomic_mailbox_and_registration_support_concurrent_readers()
    {
        using var directory = new TemporaryDirectory();
        var mailbox = new GuardPolicyMailbox(directory.Path);
        var registrations = new GuardRegistrationStore(Path.Combine(directory.Path, "registration.json"));
        var registration = new GuardRegistration { OwnerSid = "S-1-5-21-1-2-3-1001", MainExecutable = Fixture.Policy.MainExecutable,
            InstallDirectory = @"C:\mock\installed", Installed = true };
        mailbox.Save(Fixture.Policy); registrations.Save(registration);
        Task reader = Task.Run(() =>
        {
            for (int i = 0; i < 150; i++)
            {
                Assert.NotNull(mailbox.Load());
                Assert.Equal(registration.OwnerSid, registrations.Load()!.OwnerSid);
            }
        }, TestContext.Current.CancellationToken);
        for (int i = 0; i < 40; i++)
        {
            mailbox.Save(Fixture.Policy with { Revision = Guid.NewGuid().ToString("N") });
            registrations.Save(registration with { AutoStart = i % 2 == 0 });
        }
        await reader;
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void Last_desired_selection_survives_service_stop_and_replaces_old_db_on_restart()
    {
        using var directory = new TemporaryDirectory();
        var store = new GuardStore(directory.Path);
        store.SavePolicy(Fixture.Policy);
        var changed = Fixture.Policy with { Revision = Guid.NewGuid().ToString("N"), Executables = [new(@"C:\new\new.exe", "new")] };
        new GuardPolicyMailbox(directory.Path).Save(changed);
        var mailboxAfterRestart = new GuardPolicyMailbox(directory.Path);
        store.SavePolicy(mailboxAfterRestart.Load()!);
        Assert.Equal(@"C:\new\new.exe", Assert.Single(store.LoadPolicy()!.Executables).Path);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\app\..\")]
    [InlineData("relative")]
    public void Disk_roots_and_relative_directories_are_rejected(string root)
        => Assert.Throws<InvalidDataException>(() => (Fixture.Policy with { Directories = [new(root, "AI")] }).Validate());

    [Fact]
    public void Policy_hash_tracks_selection_and_directories_without_network_or_route_state()
    {
        var selection = new EgressApplicationSelection { DiscoveryKey = @"exe:C:\apps\AI\ai.exe",
            ExecutablePaths = [@"C:\apps\AI\ai.exe"], DirectoryRoots = [@"C:\apps\AI"], DisplayName = "AI" };
        var profile = EgressProfileDocument.Default with { Applications = [selection] };
        var original = GuardPolicyMailbox.FromProfile(profile, Fixture.Policy.MainExecutable, []);
        var changedRoute = GuardPolicyMailbox.FromProfile(profile with
        {
            Applications = [selection with { Target = EgressRouteTarget.ForAdapter(Guid.NewGuid().ToString()) }],
            UpstreamPorts = [7890, 7891],
        }, Fixture.Policy.MainExecutable, []);
        Assert.Equal(original.Revision, changedRoute.Revision);
        Assert.Single(original.Executables);
        Assert.Single(original.Directories);
        var changedDirectory = GuardPolicyMailbox.FromProfile(profile with
            { Applications = [selection with { DirectoryRoots = [@"C:\new"] }] }, Fixture.Policy.MainExecutable, []);
        Assert.NotEqual(original.Revision, changedDirectory.Revision);
    }

    [Fact]
    public void Failed_install_does_not_manufacture_unsecured_mailbox_directory()
    {
        using var directory = new TemporaryDirectory();
        string missing = Path.Combine(directory.Path, "not-installed");
        var paths = new GuardPaths("mock", "mock", "mock", missing, @"C:\mock\installed");
        Assert.Throws<InvalidOperationException>(() => new WindowsMainGuardEnvironment(paths).SaveDesired(Fixture.Policy));
        Assert.False(Directory.Exists(missing));
    }
}
