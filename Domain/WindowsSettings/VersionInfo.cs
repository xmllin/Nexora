using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Nexora.Models
{
    /// <summary>
    /// Structured numeric application version. Supports 1, 1.2, 1.2.3 and
    /// longer versions such as 1.2.3.4 without truncating components.
    /// </summary>
    public sealed class VersionInfo : IComparable<VersionInfo>
    {
        private static readonly Regex NumericVersionRegex = new Regex(
            @"(?<![0-9])v?([0-9]+(?:\.[0-9]+)*)(?![0-9])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private VersionInfo(string original, string normalized, IReadOnlyList<int> parts)
        {
            Original = original ?? string.Empty;
            Value = normalized ?? string.Empty;
            Parts = parts ?? Array.Empty<int>();
        }

        public string Original { get; }
        public string Value { get; }
        public IReadOnlyList<int> Parts { get; }
        public bool IsValid => Parts.Count > 0;

        public static VersionInfo Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Empty(value);

            var match = NumericVersionRegex.Match(value);
            return match.Success ? Create(value, match.Groups[1].Value) : Empty(value);
        }

        public static IEnumerable<VersionInfo> FindAll(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                yield break;

            foreach (Match match in NumericVersionRegex.Matches(value))
            {
                if (!match.Success) continue;
                var candidate = match.Groups[1].Value;
                // Ignore long standalone numeric values such as CDN cache timestamps,
                // Unix timestamps, hashes and Chromium-style revisions. Real product
                // versions are allowed to have up to four numeric components here.
                if (candidate.IndexOf('.') < 0 && candidate.Length > 4)
                    continue;
                if (candidate.Length <= 2 && (candidate == "32" || candidate == "64"))
                    continue;
                yield return Create(value, candidate);
            }
        }

        public int CompareTo(VersionInfo other)
        {
            if (other == null) return 1;
            if (!IsValid && !other.IsValid) return 0;
            if (!IsValid) return -1;
            if (!other.IsValid) return 1;
            var count = Math.Max(Parts.Count, other.Parts.Count);
            for (var i = 0; i < count; i++)
            {
                var left = i < Parts.Count ? Parts[i] : 0;
                var right = i < other.Parts.Count ? other.Parts[i] : 0;
                if (left != right) return left.CompareTo(right);
            }
            return 0;
        }

        public override string ToString() => Value;

        private static VersionInfo Create(string original, string normalized)
        {
            var parts = normalized.Split('.')
                .Select(part =>
                {
                    int result;
                    return int.TryParse(part, out result) ? result : 0;
                })
                .ToList();
            // Preserve every component exactly as published. In particular,
            // 1.1.1.1 and 1.1.1.0 must not be silently reduced.
            var canonical = string.Join(".", parts);
            return new VersionInfo(original, canonical, parts);
        }

        private static VersionInfo Empty(string original)
        {
            return new VersionInfo(original, string.Empty, Array.Empty<int>());
        }
    }
}
