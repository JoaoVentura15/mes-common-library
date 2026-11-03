using System.Text.RegularExpressions;

namespace audit.Objects.PipelineRuns
{
    /// <summary>
    /// Makes the error text of a failed pipeline step safe to send to the Data Platform: keeps the error code and the shape of the
    /// message, removes who and where (paths, accounts, emails, hosts, IPs, URLs, secrets, identifiers and values).
    /// Scrubbing is best effort: the error code is the only part that is safe by construction.
    /// </summary>
    public static class StepErrorScrubber
    {
        public const int MaxLength = 300;

        private const RegexOptions Opt = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

        /// <summary>Compiler, NuGet, npm, MSBuild and similar codes: MSB3202, NU1301, CS0246, TS2307, ERR_PNPM_FETCH_404, ENOENT.</summary>
        private static readonly Regex Code = new(@"\b(?:[A-Z]{2,5}[0-9]{3,5}|ERR_[A-Z0-9_]{3,40}|E(?:NOENT|ACCES|PERM|ADDRINUSE|CONNREFUSED|TIMEDOUT|NOTFOUND))\b", Opt, Timeout);

        // Ordered: most specific first, so later rules never see the pieces an earlier rule already replaced.
        private static readonly (Regex Pattern, string Replacement)[] Rules =
        {
            // URLs (feeds, repositories, internal services)
            (new Regex(@"\b[a-z][a-z0-9+.\-]{1,15}://[^\s""'<>)\]]+", Opt | RegexOptions.IgnoreCase, Timeout), "<url>"),
            // secrets with a known shape
            (new Regex(@"\b(?:ghp|gho|ghs|ghu|github_pat|glpat|xox[abprs]|AKIA|ASIA|AIza|sk)[-_A-Za-z0-9]{12,}|\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-.]+", Opt, Timeout), "<secret>"),
            // key=value secrets (connection strings, query strings, headers)
            (new Regex(@"(?i)\b(pass(?:word)?|pwd|pat|token|secret|api[-_]?key|account[-_]?key|access[-_]?key|sig|sas|authorization)\b\s*[=:]\s*(?:Bearer\s+|Basic\s+)?[^\s;,&""'<>]{4,}", Opt, Timeout), "$1=<redacted>"),
            // bearer / basic credentials sent as header values
            (new Regex(@"(?i)\b(?:bearer|basic)\s+[A-Za-z0-9+/=._\-]{8,}", Opt, Timeout), "<redacted>"),
            // people: "canceled by Jane Doe", "queued by ..." (name runs to the end of the sentence)
            (new Regex(@"(?i)\b(canceled|cancelled|queued|triggered|started|requested|approved|rejected|authored|created|run|invoked) by [^\r\n]+?(?=\.?\s*$|\.\s)", Opt, Timeout), "$1 by <user>"),
            // build agent names: "agent build-agent-001"
            (new Regex(@"(?i)\b(agent|machine|runner)\s+(?!<)[\w.\-]{3,60}", Opt, Timeout), "$1 <agent>"),
            // emails
            (new Regex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+", Opt, Timeout), "<email>"),
            // UNC paths: \\host\share\dir\file.ext -> <path>\file.ext
            (new Regex(@"\\\\[^\\\s""'<>|]+(?:\\[^\\\s""'<>|]+)*\\([^\\\s""'<>|]*)", Opt, Timeout), "<path>\\$1"),
            // Windows drive paths: C:\Users\x\src\Foo.csproj -> <path>\Foo.csproj (also with forward slashes)
            (new Regex(@"\b[A-Za-z]:[\\/](?:[^\\/\s""'<>|:*?]+[\\/])*([^\\/\s""'<>|:*?]*)", Opt, Timeout), "<path>\\$1"),
            // Unix and URL-less paths: /home/x/work/Foo.cs -> <path>/Foo.cs (needs at least one directory)
            (new Regex(@"(?<![\w.<>:/\\])~?/(?:[\w.@+~\-]+/)+([\w.@+~\-]*)", Opt, Timeout), "<path>/$1"),
            // DOMAIN\user and service accounts (paths are gone by now)
            (new Regex(@"\b[A-Za-z][\w\-]{1,30}\\[A-Za-z][\w.$\-]{1,40}", Opt, Timeout), "<account>"),
            // IPv4 with optional port
            (new Regex(@"\b\d{1,3}(?:\.\d{1,3}){3}(?::\d{2,5})?\b", Opt, Timeout), "<ip>"),
            // internal and public host names (lower case only, so .NET namespaces such as System.Net.Http are untouched)
            (new Regex(@"\b[a-z0-9][a-z0-9\-]*(?:\.[a-z0-9\-]+)*\.(?:local|lan|corp|internal|intranet|localdomain|com|net|org|io|dev|cloud|azure|windows)\b(?::\d{2,5})?", Opt, Timeout), "<host>"),
            // GUIDs
            (new Regex(@"\b[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\b", Opt, Timeout), "<guid>"),
            // long opaque strings (hashes, keys, base64); must contain a digit so long identifiers such as test method names survive
            (new Regex(@"\b(?=[A-Za-z0-9+/=_\-]*\d)[A-Za-z0-9+/=_\-]{32,}\b", Opt, Timeout), "<secret>"),
            // values quoted by databases and validators: key value is (LOT-883412), Key (Id)=(42)
            (new Regex(@"(?i)(key value is |key \([^)]{1,40}\)=|value is |with value |Cannot insert (?:the )?value )\(?[^)\r\n]{1,80}\)?", Opt, Timeout), "$1(<value>)"),
            // identifiers made of 4+ digits: build ids, lots, work orders (HTTP codes and short line numbers stay)
            (new Regex(@"(?<![A-Za-z_\d])\d{4,}\b", Opt, Timeout), "#"),
        };

        private static readonly Regex Spaces = new(@"\s+", Opt, Timeout);

        /// <summary>First error code found in the message, or an empty string.</summary>
        public static string ErrorCode(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "";
            try { return Code.Match(message).Value; }
            catch (RegexMatchTimeoutException) { return ""; }
        }

        /// <summary>First line of the message with paths, accounts, emails, hosts, IPs, URLs, secrets and identifiers removed, capped at <see cref="MaxLength"/>.</summary>
        public static string Scrub(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "";
            var line = message.Trim().Split('\n')[0].Trim();
            try
            {
                foreach (var (pattern, replacement) in Rules) line = pattern.Replace(line, replacement);
                line = Spaces.Replace(line, " ").Trim();
            }
            catch (RegexMatchTimeoutException)
            {
                // Never send what could not be scrubbed.
                return "";
            }
            return line.Length > MaxLength ? line.Substring(0, MaxLength) : line;
        }
    }
}
