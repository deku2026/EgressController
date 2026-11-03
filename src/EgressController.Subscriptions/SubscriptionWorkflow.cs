using System.Security.Cryptography;
using System.Text.Json.Serialization;
using EgressController.Core.Storage;

namespace EgressController.Subscriptions;

public sealed record PendingFormat(SourceFormat Format, string Sha256);
public sealed record PendingUpload(string Batch, string Provider, string Client, string Server, long Version, DateTimeOffset CapturedAt, PendingFormat[] Formats);
[JsonSerializable(typeof(PendingUpload))]
internal sealed partial class PendingJson : JsonSerializerContext;

public sealed class SubscriptionWorkflow(ISubscriptionProvider provider, ISubscriptionValidator validator, ISubscriptionUploader uploader, string directory)
{
    public async Task<SubscriptionResult[]> RefreshAsync(SubscriptionSettings settings, ProviderCredentials credentials,
        IProgress<SubscriptionProgress> progress, CancellationToken token)
    {
        PrivateFile.EnsureDirectory(directory);
        // GUI and Agent share this OS-owned lock. A crash releases ownership without PID guessing.
        await using var ownership = new FileStream(Path.Combine(directory, "refresh.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string pendingPath = Path.Combine(directory, "pending.json");
        var pending = await PrivateFile.ReadJsonAsync(pendingPath, PendingJson.Default.PendingUpload, token).ConfigureAwait(false);
        if (pending is not null && (pending.Provider != settings.ProviderId || pending.Client != settings.ClientId || pending.Server != settings.ServerEndpoint))
            throw new InvalidOperationException("有待确认的上传属于此前的服务器或账户；请恢复原设置重试，避免把订阅发往其他目标。");
        if (pending is null)
        {
            PruneOldBatches();
            progress.Report(new("获取订阅", "复用已保存的专属 Chrome 会话"));
            var captures = await provider.CaptureAsync(settings, credentials, Path.Combine(directory, "chrome-profile"), progress, token).ConfigureAwait(false);
            if (captures.Count == 0 || captures.Any(c => !Enum.IsDefined(c.Format)) || captures.Select(c => c.Format).Distinct().Count() != captures.Count)
                throw new InvalidDataException("未获取到独立有效的订阅格式。");
            string batch = Guid.NewGuid().ToString("N");
            string batchDirectory = Path.Combine(directory, "batches", batch); PrivateFile.EnsureDirectory(batchDirectory);
            var formats = new List<PendingFormat>();
            foreach (var capture in captures)
            {
                if (capture.Content.Length is 0 or > 4 * 1024 * 1024) throw new InvalidDataException("订阅大小无效。");
                string candidate = Path.Combine(batchDirectory, FileName(capture.Format));
                await PrivateFile.WriteAsync(candidate, capture.Content, token).ConfigureAwait(false);
                progress.Report(new("校验订阅", $"正在检查 {capture.Format} 原始文件"));
                await validator.ValidateAsync(capture.Format, candidate, token).ConfigureAwait(false);
                formats.Add(new(capture.Format, Convert.ToHexStringLower(SHA256.HashData(capture.Content))));
            }
            var now = DateTimeOffset.UtcNow;
            var receipts = await PrivateFile.ReadJsonAsync(Path.Combine(directory, "receipts.json"), SubscriptionJson.Default.SubscriptionResultArray, token).ConfigureAwait(false) ?? [];
            long version = Math.Max(now.ToUnixTimeMilliseconds(), receipts.Select(r => r.Version).DefaultIfEmpty(0).Max() + 1);
            pending = new(batch, settings.ProviderId, settings.ClientId, settings.ServerEndpoint, version, now, formats.ToArray());
            // Publish the immutable batch only after every format validates. Retries preserve bytes, hash and version.
            await PrivateFile.WriteJsonAsync(pendingPath, pending, PendingJson.Default.PendingUpload, token).ConfigureAwait(false);
        }
        if (!Guid.TryParseExact(pending.Batch, "N", out _) || pending.Formats.Length is < 1 or > 2 || pending.Formats.Any(f => !Enum.IsDefined(f.Format)))
            throw new InvalidDataException("待上传记录损坏；已保留原文件。");
        var results = new List<SubscriptionResult>();
        foreach (var format in pending.Formats)
        {
            string path = Path.Combine(directory, "batches", pending.Batch, FileName(format.Format));
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > 4 * 1024 * 1024) throw new InvalidDataException("待上传文件缺失或大小无效。");
            byte[] bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != format.Sha256) throw new InvalidDataException("待上传文件哈希不匹配；未发送修改后的内容。");
            // Raw subscription is separate from the private, generated Surge runtime profile.
            await PrivateFile.WriteAsync(Path.Combine(directory, "latest." + (format.Format == SourceFormat.Surge ? "conf" : "yaml")), bytes, token).ConfigureAwait(false);
            progress.Report(new("上传订阅", $"{format.Format} 等待服务器内容确认；失败后可原样重试"));
            results.Add(await uploader.UploadAsync(pending.Provider, pending.Client, format.Format, bytes, pending.Version, pending.CapturedAt, token).ConfigureAwait(false));
            await PrivateFile.WriteJsonAsync(Path.Combine(directory, "last-attempt-receipts.json"), results.ToArray(), SubscriptionJson.Default.SubscriptionResultArray, token).ConfigureAwait(false);
        }
        await PrivateFile.WriteJsonAsync(Path.Combine(directory, "receipts.json"), results.ToArray(), SubscriptionJson.Default.SubscriptionResultArray, token).ConfigureAwait(false);
        File.Delete(pendingPath); // A crash before deletion repeats an idempotent request.
        progress.Report(new("端到端上传成功", $"服务器已确认 {results.Count} 种格式的哈希与版本"));
        return results.ToArray();
    }
    private static string FileName(SourceFormat format) => format == SourceFormat.Surge ? "source.conf" : "source.yaml";
    private void PruneOldBatches()
    {
        string batches = Path.Combine(directory, "batches");
        if (!Directory.Exists(batches)) return;
        // Called only without a pending upload, under refresh.lock. Keep a small recovery history.
        foreach (var old in new DirectoryInfo(batches).EnumerateDirectories()
                     .Where(d => Guid.TryParseExact(d.Name, "N", out _) && !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                     .OrderByDescending(d => d.CreationTimeUtc).Skip(3))
        {
            try { old.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
