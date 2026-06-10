using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// An <see cref="ILspMessageInterceptor"/> that writes every LSP message to a shared log
/// file in the format consumed by the
/// <see href="https://microsoft.github.io/language-server-protocol/inspector/">LSP Inspector</see>.
/// Always returns <see cref="LspInterceptorResult.PassThrough"/>.
/// </summary>
internal sealed class LspInspectorLogger : ILspMessageInterceptor, IDisposable
{
    private readonly string _logFilePath;
    private readonly TraceSource _traceSource;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    // Tracks in-flight request timestamps keyed by JSON-RPC id (as string) so that responses
    // can report round-trip latency via the "latencyMs" envelope field.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pendingRequests = new();

    private StreamWriter? _writer;
    private bool _disposed;

    /// <summary>
    /// Initialises the logger and creates (or truncates) the log file at
    /// <paramref name="logFilePath"/>.
    /// </summary>
    public LspInspectorLogger(string logFilePath, TraceSource traceSource)
    {
        _logFilePath = logFilePath ?? throw new ArgumentNullException(nameof(logFilePath));
        _traceSource = traceSource ?? throw new ArgumentNullException(nameof(traceSource));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
            _writer = new StreamWriter(
                new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read),
                Encoding.UTF8,
                bufferSize: 4096,
                leaveOpen: false);
            _writer.AutoFlush = true;
            _traceSource.TraceInformation("LspInspectorLogger: Writing LSP Inspector log to '{0}'.", logFilePath);
        }
        catch (Exception ex)
        {
            _traceSource.TraceEvent(TraceEventType.Error, 0,
                "LspInspectorLogger: Failed to open log file '{0}': {1}", logFilePath, ex.Message);
        }
    }

    /// <inheritdoc/>
    public async Task<LspInterceptorResult> InterceptAsync(LspMessage message, CancellationToken cancellationToken)
    {
        if (_writer is null || _disposed)
            return LspInterceptorResult.PassThrough;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.WriteAsync(FormatEntry(message)).ConfigureAwait(false);
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _traceSource.TraceEvent(TraceEventType.Warning, 0,
                "LspInspectorLogger: Error writing log entry: {0}", ex.Message);
        }
        finally
        {
            _gate.Release();
        }

        return LspInterceptorResult.PassThrough;
    }

    // ── Formatting ─────────────────────────────────────────────────────────

    private string FormatEntry(LspMessage msg)
    {
        // lsp-viewer format: [LSP   - HH:mm:ss] <JSON>\n
        // JSON: {"isLSPMessage":true,"type":"<type>","message":{...},"timestamp":<unix-ms>}
        // Extended fields (ignored by the tool, useful for grep):
        //   "latencyMs" — round-trip ms on response entries
        //   "traceId"   — W3C trace-context trace ID from VS-originated requests

        string type;
        bool isSend = msg.Direction == LspMessageDirection.Send;
        if (msg.IsRequest)
            type = isSend ? "send-request" : "receive-request";
        else if (msg.IsResponse)
            type = isSend ? "send-response" : "receive-response";
        else
            type = isSend ? "send-notification" : "receive-notification";

        long timestampMs = msg.Timestamp.ToUnixTimeMilliseconds();
        string timeLabel = msg.Timestamp.LocalDateTime.ToString("HH:mm:ss");

        var entry = new JObject
        {
            ["isLSPMessage"] = true,
            ["type"]         = type,
            ["message"]      = msg.Body,
            ["timestamp"]    = timestampMs,
        };

        // Track request timestamps for latency computation on matching responses.
        if (msg.IsRequest && msg.Id is not null)
            _pendingRequests[msg.Id.ToString()] = msg.Timestamp;

        // Annotate responses with round-trip latency.
        if (msg.IsResponse && msg.Id is not null &&
            _pendingRequests.TryRemove(msg.Id.ToString(), out var requestTime))
        {
            entry["latencyMs"] = (long)(msg.Timestamp - requestTime).TotalMilliseconds;
        }

        // Extract the W3C trace ID from VS-originated requests so the entry is greppable
        // alongside the server-side debug log (one-way handle: request → server handling).
        var traceparent = msg.Body["traceparent"]?.Value<string>();
        if (traceparent is not null)
        {
            var traceId = ExtractTraceId(traceparent);
            if (traceId is not null)
                entry["traceId"] = traceId;
        }

        // Compact single-line JSON to match the lsp-viewer expectation.
        string json = JsonConvert.SerializeObject(entry, Formatting.None);
        return $"[LSP   - {timeLabel}] {json}\n";
    }

    private static string? ExtractTraceId(string traceparent)
    {
        // W3C traceparent format: "00-<trace-id>-<parent-id>-<flags>"
        var parts = traceparent.Split('-');
        return parts.Length >= 2 ? parts[1] : null;
    }

    // ── IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Wait(millisecondsTimeout: 500);
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch { /* best-effort */ }
        _gate.Release();
        _gate.Dispose();
    }
}
