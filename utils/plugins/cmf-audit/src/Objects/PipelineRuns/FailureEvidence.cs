using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace audit.Objects.PipelineRuns;

/// <summary>Only redacted, bounded evidence leaves the scanner. Category is an observed failure mode, not vendor attribution.</summary>
public record FailureEvidence(string Code, string Message, string Category, string Ecosystem, string Fingerprint,
    string Source, string ScanStatus, int LogId = 0, int LogLine = 0, int LinesScanned = 0);

/// <summary>Deterministic extraction from a failed task's own log; successful task logs are never searched.</summary>
public sealed class FailureLogScanner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);
    private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Prefix = R(@"^(?:\d{4}-\d\d-\d\dT[\d:.]+Z\s+)?(?:##\[(?:error|warning|debug)\])?\s*");
    private static readonly Regex Ansi = R(@"\x1B\[[0-?]*[ -/]*[@-~]");
    private static readonly Regex Command = R(@"^(?:##\[command\]|[+>]\s|(?:echo|printf|Write-Host|Write-Output)\s)");
    private static readonly Regex Generic = R(@"(?:exited|completed|returned|exit)\s+(?:with\s+)?(?:exit\s+)?code\s*['\"" ]?-?\d+|Bash failed|script failed with exit");
    private static readonly Regex Http = R(@"\b(?:HTTP(?:/\d(?:\.\d)?)?\s*[:=]?\s*|status(?: code)?\s*[:=(]?\s*|response(?: status)?(?: code)?\s*[:=(]?\s*|\()(401|403|404|408|429|500|502|503|504|520|524)\b|\b(401|403|404|429|500|502|503|504|520|524)\s+\(?(?:Unauthorized|Forbidden|Not Found|Too Many|Internal Server|Bad Gateway|Service Unavailable|Gateway|Error)|\bE(401|403|404|429|500|502|503|504)\b");
    private static readonly Regex Failure = R(@"\b(?:error|failed|failure|fatal|exception|unable|could not|cannot|denied|unauthorized|forbidden|timed?\s*out|timeout|ECONNRESET|ECONNREFUSED|ENOTFOUND|EAI_AGAIN|ETIMEDOUT|EINTEGRITY|ERESOLVE)\b");
    private static readonly Regex Compile = R(@"\berror\s+(?:CS|TS|MSB)\d{3,5}\b|\b(?:CS|TS)\d{3,5}:\s");
    private static readonly Regex Network = R(@"\b(ECONNRESET|ECONNREFUSED|ETIMEDOUT|ESOCKETTIMEDOUT|ENOTFOUND|EAI_AGAIN)\b|connection (?:reset|refused)|could not resolve host|no such host|name or service not known|temporary failure in name resolution|timed?\s*out|timeout|TLS|certificate|SSL connection");
    private static readonly Regex Version = R(@"\b\d+\.\d+\.\d+(?:[-+][\w.-]+)?\b");
    private FailureEvidence? best;
    private int bestScore;
    private string ecosystem = "";
    private bool skipped;
    public int LinesScanned { get; private set; }

    public void Observe(string raw, int lineNumber)
    {
        LinesScanned++;
        if (raw.Length > 16384) { skipped = true; return; }
        try
        {
            var line = Ansi.Replace(raw, "").Trim();
            line = Regex.Replace(line, @"^\d{4}-\d\d-\d\dT[\d:.]+Z\s+", "", RegexOptions.None, Timeout);
            if (Command.IsMatch(line) || line.Contains("##[warning]", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(line, @"\bwarning\s+(?:CS|TS|MSB|NU)\d{3,5}\b", RegexOptions.IgnoreCase, Timeout)) return;
            line = Prefix.Replace(line, "");
            var lower = line.ToLowerInvariant();
            if (lower.Contains("nuget") || lower.Contains(".nupkg") || lower.Contains("error nu")) ecosystem = "NuGet";
            else if (lower.Contains("npm ") || lower.Contains("npm/") || lower.Contains("yarn ") || lower.Contains("pnpm ")) ecosystem = "npm";
            else if (lower.Contains("docker") || lower.Contains("manifest unknown") || lower.Contains("error response from daemon")) ecosystem = "Docker/OCI";
            if (!Failure.IsMatch(line) && !Network.IsMatch(line) && !lower.Contains("manifest unknown") && !lower.Contains("no space left on device")) return;
            var code = StepErrorScrubber.ErrorCode(line);
            var category = "Unknown";
            var score = Generic.IsMatch(line) ? 5 : 25;
            if (Compile.IsMatch(line)) { category = "Compilation"; score = 100; }
            else if (lower.Contains("assert.") || lower.Contains("assertion failed") || lower.Contains("test run failed")) { category = "Test failure"; score = 90; }
            else if (lower.Contains("is empty") && (lower.Contains("variable") || lower.Contains("system_") || lower.Contains("cannot determine"))) { category = "Configuration"; score = 100; }
            else if (lower.Contains("no space left on device") || lower.Contains("disk full") || lower.Contains("lost communication with the server")) { category = "Agent infrastructure"; score = 100; }
            else if (lower.Contains("checksum") || lower.Contains("integrity checksum failed") || lower.Contains("eintegrity")) { category = "Dependency integrity"; score = 95; code = "EINTEGRITY"; }
            else if (lower.Contains("eresolve") || lower.Contains("unable to resolve dependency")) { category = "Dependency resolution"; score = 90; code = code.Length > 0 ? code : "ERESOLVE"; }
            else
            {
                var http = Http.Match(line);
                var status = http.Success ? Enumerable.Range(1, http.Groups.Count - 1).Select(i => http.Groups[i].Value).FirstOrDefault(v => v.Length > 0) : null;
                if (status != null)
                {
                    code = "HTTP" + status;
                    category = status is "401" or "403" ? "Authentication / authorization" : status == "404" ? "Missing dependency / endpoint" : status == "429" ? "Throttling" : "Remote service / gateway";
                    score = 95;
                }
                else if (Network.IsMatch(line))
                {
                    var network = Network.Match(line).Value;
                    code = Regex.IsMatch(network, @"^[A-Z_]+$", RegexOptions.None, Timeout) ? network : code;
                    category = lower.Contains("certificate") || lower.Contains("tls") || lower.Contains("ssl connection") ? "TLS / certificate" : lower.Contains("resolv") || lower.Contains("enotfound") || lower.Contains("eai_again") || lower.Contains("no such host") ? "DNS" : lower.Contains("time") ? "Timeout" : "Connection failure";
                    score = 85;
                }
                else if (lower.Contains("manifest unknown") || lower.Contains("manifest for") && lower.Contains("not found")) { category = "Missing dependency / endpoint"; score = 95; code = "MANIFEST_UNKNOWN"; }
                else if (code.StartsWith("NU", StringComparison.Ordinal)) { category = "Dependency restore"; score = 70; }
                else if (lower.Contains("pipeline #") && lower.Contains("failed")) { category = "Upstream pipeline failure"; score = 40; }
                else if (lower.Contains("exception:") || lower.StartsWith("error:") || lower.Contains("npm err!") || lower.Contains("npm error")) score = 60;
            }
            if (score < bestScore || score == 5) return;
            var safe = StepErrorScrubber.Scrub(line);
            if (safe.Length == 0) return;
            // Fingerprints contain no raw values. A generic process exit is never promoted to a known cause.
            var key = category is "Authentication / authorization" or "Remote service / gateway" or "Throttling" or "DNS" or "Timeout" or "Connection failure"
                ? category + "|" + ecosystem + "|" + code
                : category + "|" + code + "|" + Version.Replace(safe, "<version>");
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant()))).ToLowerInvariant();
            best = new(code, safe, category, ecosystem, fingerprint, "TaskLog", "Pending", LogLine: lineNumber);
            bestScore = score;
        }
        catch (RegexMatchTimeoutException) { skipped = true; }
    }

    public FailureEvidence Finish(string status, int logId, string timelineMessage)
    {
        if (skipped && status == "Complete") status = "Truncated";
        if (best != null) return best with { ScanStatus = status, LogId = logId, LinesScanned = LinesScanned };
        var safe = StepErrorScrubber.Scrub(timelineMessage);
        return new(StepErrorScrubber.ErrorCode(safe), safe, "Unknown", "", "", "Timeline", status, logId, LinesScanned: LinesScanned);
    }
}
