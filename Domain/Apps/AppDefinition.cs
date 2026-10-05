using System.Collections.Generic;

namespace Nexora.Models
{
    public class AppDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string LongDescription { get; set; }
        public string Category { get; set; }
        public string Icon { get; set; }
        public string Website { get; set; }
        public bool IsBlockedInRussia { get; set; }
        public bool ShowVpnBadge { get; set; }
        public DownloadDefinition Download { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public List<string> RecommendationTags { get; set; } = new List<string>();
        public bool IsRecommended { get; set; }
    }

    public class DownloadDefinition
    {
        public string Type { get; set; }
        public string Url { get; set; }
        public string Repository { get; set; }
        public string Owner { get; set; }
        public string AssetPattern { get; set; }
        public string FileName { get; set; }
        public string Architecture { get; set; }
        public string Sha256 { get; set; }
        public long? ExpectedSize { get; set; }
        public Dictionary<string, string> Assets { get; set; } = new Dictionary<string, string>();
        public bool AllowArchitectureFallback { get; set; }
        public bool PreferNativeArchitecture { get; set; } = true;
        public int? MinimumWindowsBuild { get; set; }
        public int? MaximumWindowsBuild { get; set; }
        public string PackageId { get; set; }
        public string PackageSource { get; set; }
        public string InstallerType { get; set; }
    }
}
