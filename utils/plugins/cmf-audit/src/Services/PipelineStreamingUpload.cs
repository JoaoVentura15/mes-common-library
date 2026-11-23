using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using audit.Objects.PipelineRuns;
using Cmf.CLI.Core;

namespace audit.Services;

/// <summary>
/// Durable, bounded typed-event spool. Payloads are saved before publication; acknowledgements are
/// independent of completion order. An interrupted extraction is remapped from cached ADO reads,
/// suppressing only exact source payloads already in the spool. Delivery remains at-least-once.
/// </summary>
public sealed class PipelineStreamingUpload
{
    private sealed record Payload(int Version, int Index, int TypedOffset, List<PostTelemetry> Source, List<TypedEvent> Typed);
    private sealed record Chunk(Payload Payload, string Hash);
    private sealed record Seal(int Version, int Chunks, int Sources, int Typed, string Hash);
    public sealed record Summary(int SourceEvents, int TypedEvents, int Batches);

    private readonly PipelineCheckpoint checkpoint;
    private readonly int batchSize;
    private readonly int parallelism;
    private readonly string directory;
    private readonly SemaphoreSlim appendGate = new(1);
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> runTimes = new(StringComparer.Ordinal);
    private readonly List<PostTelemetry> pending = new();
    private readonly List<string> hashes = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<Chunk> queue;
    private readonly Func<List<TypedEvent>, UploadBatchProgress, Task> post;
    private readonly TimeSpan delay;
    public CancellationToken CancellationToken => stop.Token;
    private Exception? uploadFailure;
    private int sourceCount;
    private int typedCount;
    private int acknowledged;

    public PipelineStreamingUpload(PipelineCheckpoint checkpoint, int batchSize, int parallelism,
        Func<List<TypedEvent>, UploadBatchProgress, Task> post, TimeSpan delay)
    {
        this.checkpoint = checkpoint;
        this.batchSize = Math.Max(1, batchSize);
        this.parallelism = Math.Max(1, parallelism);
        this.post = post;
        this.delay = delay;
        directory = Path.Combine(checkpoint.DirectoryPath, "stream-v1");
        queue = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(this.parallelism * 2)
        {
            SingleWriter = false, SingleReader = this.parallelism == 1,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private string ChunkPath(int index) => Path.Combine(directory, index.ToString("D10") + ".json");
    private static async Task<T> ReadAsync<T>(string path)
    {
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(input) ?? throw new InvalidDataException("Invalid streaming checkpoint payload");
    }

    /// <summary>collect is never called after the extraction seal has been persisted.</summary>
    public async Task<Summary> RunAsync(Func<Func<IReadOnlyList<PostTelemetry>, Task>, CancellationToken, Task> collect)
    {
        try { return await RunCoreAsync(collect); }
        finally { stop.Dispose(); appendGate.Dispose(); }
    }

    private async Task<Summary> RunCoreAsync(Func<Func<IReadOnlyList<PostTelemetry>, Task>, CancellationToken, Task> collect)
    {
        await checkpoint.BindUploadModeAsync(streaming: true);
        Directory.CreateDirectory(directory);
        // Validate every existing chunk and receipt before making any new external write.
        var files = Directory.EnumerateFiles(directory, "*.json").Where(p => Path.GetFileName(p) != "complete.json")
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        foreach (var path in files)
        {
            if (path != ChunkPath(hashes.Count)) throw new InvalidDataException("Streaming checkpoint has a missing or unexpected chunk");
            var chunk = await ReadAsync<Chunk>(path);
            Validate(chunk);
            if (checkpoint.Contains("stream-ack:" + chunk.Payload.Index))
            {
                var receipt = await checkpoint.GetOrCreateAsync<string>("stream-ack:" + chunk.Payload.Index,
                    () => throw new InvalidOperationException("Missing stream receipt"));
                if (receipt != chunk.Hash) throw new InvalidDataException("Streaming batch differs from its acknowledgement");
            }
            Remember(chunk);
        }
        var completePath = Path.Combine(directory, "complete.json");
        var sealedExtraction = File.Exists(completePath);
        if (sealedExtraction)
        {
            var seal = await ReadAsync<Seal>(completePath);
            if (seal != new Seal(1, hashes.Count, sourceCount, typedCount, Hash(hashes)))
                throw new InvalidDataException("Streaming extraction seal does not match its chunks");
        }

        var workers = Enumerable.Range(0, parallelism).Select(_ => UploadAsync()).ToArray();
        Exception? collectionFailure = null;
        try
        {
            // Replay frozen payloads first. A cached acknowledgement performs no network request.
            for (var index = 0; index < hashes.Count; index++)
                await queue.Writer.WriteAsync(await ReadAsync<Chunk>(ChunkPath(index)), stop.Token);
            if (!sealedExtraction)
            {
                await collect(AppendAsync, stop.Token);
                await appendGate.WaitAsync(stop.Token);
                try { await FlushAsync(); }
                finally { appendGate.Release(); }
                await PipelineCheckpoint.WriteAtomicAsync(completePath, new Seal(1, hashes.Count, sourceCount, typedCount, Hash(hashes)));
            }
        }
        catch (Exception ex) { collectionFailure = ex; }
        finally { queue.Writer.TryComplete(); }
        try { await Task.WhenAll(workers); }
        catch (Exception ex) { uploadFailure ??= ex; }
        if (uploadFailure != null) ExceptionDispatchInfo.Capture(uploadFailure).Throw();
        if (collectionFailure != null) ExceptionDispatchInfo.Capture(collectionFailure).Throw();
        Log.Information($"Streaming complete: {sourceCount} source event(s), {typedCount} typed IoT event(s), {hashes.Count} durable batch(es) acknowledged or previously acknowledged");
        return new Summary(sourceCount, typedCount, hashes.Count);
    }

    private void Validate(Chunk chunk)
    {
        if (chunk.Payload.Version != 1 || chunk.Payload.Index != hashes.Count || chunk.Payload.TypedOffset != typedCount ||
            chunk.Payload.Source.Count == 0 || chunk.Payload.Source.Count > batchSize || chunk.Hash != Hash(chunk.Payload))
            throw new InvalidDataException("Streaming checkpoint payload failed validation");
    }

    private void Remember(Chunk chunk)
    {
        foreach (var e in chunk.Payload.Source)
            if (!seen.Add(Hash(e))) throw new InvalidDataException("Duplicate source payload in streaming checkpoint");
        // Rebuild only the small conversion context, not an in-memory copy of the full extraction.
        AdoTelemetryEvents.Convert(chunk.Payload.Source, checkpoint.CreatedUtc.Date, runTimes);
        sourceCount += chunk.Payload.Source.Count;
        typedCount += chunk.Payload.Typed.Count;
        hashes.Add(chunk.Hash);
    }

    private async Task AppendAsync(IReadOnlyList<PostTelemetry> source)
    {
        await appendGate.WaitAsync(stop.Token);
        try
        {
            foreach (var e in source)
            {
                stop.Token.ThrowIfCancellationRequested();
                if (!seen.Add(Hash(e))) continue;
                pending.Add(e);
                if (pending.Count == batchSize) await FlushAsync();
            }
        }
        finally { appendGate.Release(); }
    }

    private async Task FlushAsync()
    {
        if (pending.Count == 0) return;
        var source = pending.ToList();
        var typed = AdoTelemetryEvents.Convert(source, checkpoint.CreatedUtc.Date, runTimes);
        var payload = new Payload(1, hashes.Count, typedCount, source, typed);
        var chunk = new Chunk(payload, Hash(payload));
        // Freeze exact conversion output, event times and ordering before publishing to the queue.
        await PipelineCheckpoint.WriteAtomicAsync(ChunkPath(payload.Index), chunk);
        pending.Clear();
        sourceCount += source.Count;
        typedCount += typed.Count;
        hashes.Add(chunk.Hash);
        await queue.Writer.WriteAsync(chunk, stop.Token); // Bounded backpressure; never an unbounded payload queue.
    }

    private async Task UploadAsync()
    {
        try
        {
            await foreach (var chunk in queue.Reader.ReadAllAsync(stop.Token))
            {
                var sent = false;
                var receipt = await checkpoint.GetOrCreateAsync("stream-ack:" + chunk.Payload.Index, async () =>
                {
                    if (chunk.Payload.Typed.Count > 0)
                        await post(chunk.Payload.Typed, new UploadBatchProgress(chunk.Payload.Index + 1, -1,
                            chunk.Payload.TypedOffset, -1, Concurrent: true));
                    sent = true;
                    return chunk.Hash;
                });
                if (receipt != chunk.Hash) throw new InvalidDataException("Streaming upload receipt does not match its payload");
                var count = Interlocked.Add(ref acknowledged, chunk.Payload.Typed.Count);
                Log.Information($"Stream typed batch {chunk.Payload.Index + 1}: {(sent ? "acknowledged" : "previously acknowledged; skipped")}; {count} event(s) completed this invocation (out-of-order completion supported)");
                if (sent && delay > TimeSpan.Zero) await Task.Delay(delay, stop.Token);
            }
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref uploadFailure, ex, null);
            stop.Cancel(); // Unblock producers waiting on a full queue and cancel ADO requests.
            throw;
        }
    }
}
