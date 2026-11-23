using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using audit.Objects.PipelineRuns;
using Cmf.CLI.Core;

namespace audit.Services;

/// <summary>Position of an upload batch within the original event stream, including resumed batches.</summary>
public sealed record UploadBatchProgress(int BatchNumber, int TotalBatches, int EventOffset, int TotalEvents, bool Concurrent = false);

/// <summary>A local extraction snapshot. Successful reads and acknowledged batches survive process failure.</summary>
public sealed class PipelineCheckpoint : IDisposable
{
    private const int Version = 1;
    private readonly string directory;
    private readonly FileStream lease;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();
    private int reused;
    public DateTime CreatedUtc { get; }
    internal string DirectoryPath => directory;
    public bool Resuming { get; }
    private record Manifest(int Version, string Fingerprint, DateTime CreatedUtc);
    private record Entry<T>(int Version, string Key, T Value);

    public PipelineCheckpoint(string directory, string fingerprint, bool resume, string? compatibleFingerprint = null)
    {
        this.directory = Path.GetFullPath(directory);
        Resuming = resume;
        if (resume && !Directory.Exists(this.directory)) throw new InvalidOperationException("Checkpoint directory does not exist; resume cannot start a new extraction");
        Directory.CreateDirectory(this.directory);
        lease = new FileStream(Path.Combine(this.directory, "checkpoint.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var path = Path.Combine(this.directory, "manifest.json");
            if (resume)
            {
                if (!File.Exists(path)) throw new InvalidDataException("Checkpoint manifest is missing");
                using var stream = File.OpenRead(path);
                var manifest = JsonSerializer.Deserialize<Manifest>(stream) ?? throw new InvalidDataException("Invalid checkpoint manifest");
                if (manifest.Version != Version || (manifest.Fingerprint != fingerprint &&
                    (compatibleFingerprint == null || manifest.Fingerprint != compatibleFingerprint)))
                    throw new InvalidOperationException("Checkpoint options or format do not match. Use the original command and destination, or a new directory");
                CreatedUtc = manifest.CreatedUtc;
            }
            else
            {
                if (Directory.EnumerateFileSystemEntries(this.directory).Any(p => Path.GetFileName(p) != "checkpoint.lock"))
                    throw new InvalidOperationException("Checkpoint directory is not empty. Use --resume or a new directory");
                CreatedUtc = DateTime.UtcNow;
                WriteAtomicAsync(path, new Manifest(Version, fingerprint, CreatedUtc)).GetAwaiter().GetResult();
            }
        }
        catch { lease.Dispose(); throw; }
    }

    public static string Fingerprint(PipelineRunsOptions options, bool? telemetryEvents = null)
    {
        // Authentication may be renewed between attempts. Never put credentials in the manifest or cache keys.
        var ignored = new HashSet<string> { "personalAccessToken", "securityAccessToken", "checkpointDirectory", "resume", "parallelism", "batchDelayMs", "uploadParallelism", "concurrentCollection", "streamEvents" };
        var values = typeof(PipelineRunsOptions).GetProperties().Where(p => !ignored.Contains(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => p.GetValue(options));
        if (telemetryEvents.HasValue) values["telemetryEvents"] = telemetryEvents.Value;
        if (!string.IsNullOrWhiteSpace(options.fieldsConfig)) values["fieldsConfigContentsHash"] = Hash(File.ReadAllText(options.fieldsConfig));
        return Hash(JsonSerializer.Serialize(values));
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public bool Contains(string key)
    {
        var name = Hash(key);
        return File.Exists(Path.Combine(directory, name[..2], name + ".json"));
    }

    public async Task BindUploadModeAsync(bool streaming)
    {
        // Old checkpoints use offset receipts over the final extracted list. Never reinterpret them as a spool.
        if (streaming && Resuming && !Contains("upload-mode"))
            throw new InvalidOperationException("A legacy checkpoint cannot switch to streaming; resume without --streamEvents or use a new directory");
        var mode = streaming ? "stream-v1" : "legacy";
        var saved = await GetOrCreateAsync("upload-mode", () => Task.FromResult(mode));
        if (saved != mode) throw new InvalidOperationException("Checkpoint upload mode does not match; keep the original --streamEvents setting");
    }

    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> collect)
    {
        var gate = gates.GetOrAdd(key, _ => new SemaphoreSlim(1));
        await gate.WaitAsync();
        try
        {
            var name = Hash(key);
            var shard = Path.Combine(directory, name[..2]);
            var path = Path.Combine(shard, name + ".json");
            if (File.Exists(path))
            {
                await using var input = File.OpenRead(path);
                var entry = await JsonSerializer.DeserializeAsync<Entry<T>>(input) ?? throw new InvalidDataException("Invalid checkpoint entry");
                if (entry.Version != Version || entry.Key != key) throw new InvalidDataException("Checkpoint entry does not match its request");
                var n = Interlocked.Increment(ref reused);
                if (n == 1 || n % 1000 == 0) Log.Information($"Checkpoint: reused {n} completed read(s)/batch receipt(s)");
                return entry.Value;
            }
            var value = await collect(); // Exceptions never produce a completed entry.
            Directory.CreateDirectory(shard);
            await WriteAtomicAsync(path, new Entry<T>(Version, key, value));
            return value;
        }
        finally { gate.Release(); }
    }

    internal static async Task WriteAtomicAsync<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, value);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task PostBatchesAsync<T>(string kind, List<T> events, int batchSize, Func<List<T>, Task> post, TimeSpan delay, int parallelism = 1) =>
        PostBatchesAsync(kind, events, batchSize, (batch, _) => post(batch), delay, parallelism);

    public async Task PostBatchesAsync<T>(string kind, List<T> events, int batchSize, Func<List<T>, UploadBatchProgress, Task> post, TimeSpan delay, int parallelism = 1)
    {
        batchSize = Math.Max(1, batchSize);
        var totalBatches = events.Count == 0 ? 0 : (events.Count - 1) / batchSize + 1;
        var completed = 0;
        async Task PostAsync(int index)
        {
            var offset = index * batchSize;
            var batch = events.GetRange(offset, Math.Min(batchSize, events.Count - offset));
            var progress = new UploadBatchProgress(index + 1, totalBatches, offset, events.Count, parallelism > 1);
            var hash = Hash(JsonSerializer.Serialize(batch));
            var sent = false;
            var receipt = await GetOrCreateAsync("acknowledged:" + kind + ":" + offset, async () =>
            {
                await post(batch, progress);
                sent = true;
                return hash;
            });
            if (receipt != hash) throw new InvalidDataException("Upload batch differs from its checkpoint receipt; refusing to skip data");
            var acknowledged = Interlocked.Add(ref completed, batch.Count);
            Log.Information($"Checkpoint {kind}: batch {progress.BatchNumber}/{progress.TotalBatches}, {acknowledged}/{events.Count} event(s) " +
                (sent ? "acknowledged" : "previously acknowledged; skipped"));
            if (sent && offset + batch.Count < events.Count && delay > TimeSpan.Zero) await Task.Delay(delay);
        }
        if (parallelism <= 1) for (var index = 0; index < totalBatches; index++) await PostAsync(index);
        else await Parallel.ForEachAsync(Enumerable.Range(0, totalBatches), new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            async (index, _) => await PostAsync(index));
    }

    public void Dispose()
    {
        lease.Dispose();
        foreach (var gate in gates.Values) gate.Dispose();
    }
}
