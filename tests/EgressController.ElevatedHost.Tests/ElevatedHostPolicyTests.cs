using EgressController.Core.Ipc;
using EgressController.ElevatedHost;

namespace EgressController.ElevatedHost.Tests;

public sealed class ElevatedHostPolicyTests
{
    [Fact]
    public void Only_sing_box_under_core_root_and_json_under_data_root_are_allowed()
    {
        string root = Path.Combine(Path.GetTempPath(), "EgressController.HostPolicyTests", Guid.NewGuid().ToString("N"));
        try
        {
            var policy = new ElevatedHostPathPolicy { DataRoot = root };

            Assert.True(policy.IsAllowedCorePath(Path.Combine(root, "core", "1.13.19", "sing-box.exe")));
            Assert.False(policy.IsAllowedCorePath(Path.Combine(root, "core", "1.13.19", "other.exe")));
            Assert.True(policy.IsAllowedConfigPath(Path.Combine(root, "config.next.json")));
            Assert.False(policy.IsAllowedConfigPath(Path.Combine(root, "..", "config.json")));
            Assert.False(policy.IsAllowedConfigPath(Path.Combine(root, "config.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Start_validation_rejects_paths_outside_policy_before_hash_or_process_start()
    {
        string root = Path.Combine(Path.GetTempPath(), "EgressController.HostPolicyTests", Guid.NewGuid().ToString("N"));
        var policy = new ElevatedHostPathPolicy { DataRoot = root };
        ElevatedIpcMessage request = ElevatedIpcMessage.Request(ElevatedIpcKind.Start, Environment.ProcessId) with
        {
            CorePath = @"C:\Windows\System32\sing-box.exe",
            ConfigPath = Path.Combine(root, "config.json"),
            CoreSha256 = new('a', 64),
            ConfigSha256 = new('b', 64),
        };

        ElevatedHostValidationException exception = Assert.Throws<ElevatedHostValidationException>(
            () => SingBoxProcessHost.ValidateStartRequest(policy, request));

        Assert.Equal("core.path", exception.Code);
    }
}
