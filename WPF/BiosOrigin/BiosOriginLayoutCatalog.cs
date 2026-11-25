using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

namespace ZenTimings.BiosOrigin
{
    // Phase 2 catalog: selection is deterministic, read-only and fails closed.
    // A catalog match is a candidate, never a replacement for the resolver's
    // own IFR plausibility checks against the actual variable bytes.
    public static class BiosOriginLayoutCatalog
    {
        public const string ResourceName = "ZenTimings.Resources.BiosLayouts.catalog-v1.json";

        // An AI-proposed mapping is never automatically trusted, even if its
        // varstore sizes/header happen to match. A maintainer must promote it
        // after evidence review; there is no implicit opt-in.
        public static bool MayDisplayOrigins(string status) =>
            status == "verified" || status == "derived";

        public static BiosOriginCatalogMatch Select(string board, string biosVersion)
        {
            using (Stream stream = typeof(BiosOriginLayoutCatalog).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null) throw new InvalidDataException("Embedded origin layout catalog is missing.");
                var serializer = new DataContractJsonSerializer(typeof(Catalog));
                var catalog = (Catalog)serializer.ReadObject(stream);
                Validate(catalog);
                var candidates = catalog.Layouts
                    .Where(layout => BoardMatches(layout, board))
                    .SelectMany(layout => layout.Releases
                        .Where(release => VersionMatches(layout, biosVersion, release.Version)
                            || (release.Aliases != null && release.Aliases.Any(a =>
                                VersionMatches(layout, biosVersion, a))))
                        .Select(release => new BiosOriginCatalogMatch(layout, release)))
                    .ToList();
                if (candidates.Count > 1)
                    throw new InvalidDataException("Ambiguous origin layout catalog match.");
                return candidates.SingleOrDefault();
            }
        }

        // Diagnostics only: explains selection without authorizing any badges or
        // inferring that an unlisted firmware has compatible field offsets.
        public static string DescribeSelection(string board, string biosVersion)
        {
            using (Stream stream = typeof(BiosOriginLayoutCatalog).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null) return "catalog resource missing";
                var serializer = new DataContractJsonSerializer(typeof(Catalog));
                Catalog catalog = (Catalog)serializer.ReadObject(stream);
                Validate(catalog);
                List<LayoutEntry> boardMatches = catalog.Layouts
                    .Where(layout => BoardMatches(layout, board)).ToList();
                if (boardMatches.Count == 0)
                    return "No board token matched; all offset probes are UNVERIFIED reference data.";
                var versions = boardMatches.SelectMany(layout => layout.Releases
                    .Where(r => VersionMatches(layout, biosVersion, r.Version) ||
                        (r.Aliases != null && r.Aliases.Any(a => VersionMatches(layout, biosVersion, a))))
                    .Select(r => new { Layout = layout, Release = r })).ToList();
                if (versions.Count == 0)
                {
                    string known = string.Join(", ", boardMatches.SelectMany(l => l.Releases
                        .Select(r => r.Version)).Distinct(StringComparer.OrdinalIgnoreCase));
                    return "Board token matched: " +
                        string.Join(", ", boardMatches.Select(l => l.Id)) +
                        "; BIOS version is not registered (known: " + known +
                        "); no setting-origin badges permitted.";
                }
                if (versions.Count != 1)
                    return "Ambiguous layout candidates; no setting-origin badges permitted.";
                var match = versions[0];
                bool eligible = MayDisplayOrigins(match.Release.Status) &&
                    (match.Layout.Resolver == "drfxi-dragonrange-v1" ||
                     match.Layout.Resolver == "msi-mag-b650-raphael-v1e" ||
                      match.Layout.Resolver == AsrockSteelLegendSettingOrigins.Resolver ||
                       match.Layout.Resolver == LenovoM75nSettingOrigins.Resolver);
                return "Board and release matched: " + match.Layout.Id +
                    " / " + match.Release.Version + " (" + match.Release.Status +
                    "); resolver: " + match.Layout.Resolver +
                    (eligible ? "; candidate only — varstore fingerprint and IFR checks still required."
                        : "; resolver or evidence tier not trusted; no badges permitted.");
            }
        }

        // Board names are token-matched rather than arbitrary substrings:
        // e.g. "NOTDRFXI" must never receive the DRFXI layout.
        private static bool BoardMatches(LayoutEntry entry, string board)
        {
            if (string.IsNullOrWhiteSpace(board)) return false;
            return entry.BoardTokens.Any(token =>
                Regex.IsMatch(board, @"(?:^|[^a-zA-Z0-9])" + Regex.Escape(token)
                    + @"(?:$|[^a-zA-Z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }

        private static bool VersionMatches(LayoutEntry layout, string running, string expected)
        {
            if (string.IsNullOrWhiteSpace(running)) return false;
            if (layout.Resolver == AsrockSteelLegendSettingOrigins.Resolver || layout.Resolver == LenovoM75nSettingOrigins.Resolver)
                return string.Equals(running.Trim(), expected, StringComparison.OrdinalIgnoreCase);
            return Regex.IsMatch(running, @"(?:^|[^0-9.])" + Regex.Escape(expected)
                + @"(?:$|[^0-9.])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }

        private static void Validate(Catalog catalog)
        {
            if (catalog == null || catalog.SchemaVersion != 1 || catalog.Layouts == null
                || catalog.Layouts.Count == 0)
                throw new InvalidDataException("Unknown or empty origin catalog schema.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LayoutEntry entry in catalog.Layouts)
            {
                if (string.IsNullOrWhiteSpace(entry.Id) || !ids.Add(entry.Id)
                    || string.IsNullOrWhiteSpace(entry.Resolver)
                    || entry.BoardTokens == null || entry.BoardTokens.Count == 0
                    || entry.BoardTokens.Any(string.IsNullOrWhiteSpace)
                    || entry.Releases == null || entry.Releases.Count == 0
                    || entry.Variables == null || entry.Variables.Count == 0)
                    throw new InvalidDataException("Invalid origin layout entry.");
                if (entry.Releases.Any(r => r == null || string.IsNullOrWhiteSpace(r.Version)
                    || (r.Status != "verified" && r.Status != "derived" && r.Status != "ai-proposed")
                    || (r.Aliases != null && r.Aliases.Any(string.IsNullOrWhiteSpace))))
                    throw new InvalidDataException("Invalid origin layout release status/version.");
                var knownVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Release release in entry.Releases)
                {
                    if (!knownVersions.Add(release.Version)
                        || (release.Aliases != null && release.Aliases.Any(a => !knownVersions.Add(a))))
                        throw new InvalidDataException("Duplicate BIOS version or alias in a layout.");
                }
                var variableKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (VariableFingerprint variable in entry.Variables)
                {
                    Guid parsed;
                    if (variable == null || string.IsNullOrWhiteSpace(variable.Name)
                        || !variableKeys.Add(variable.Name) || !Guid.TryParse(variable.Guid, out parsed)
                        || variable.Size <= 0 || variable.Size > 65536 || variable.Headers == null
                        || variable.Headers.Any(h => h.Offset < 0 || h.Offset + 4 > variable.Size))
                        throw new InvalidDataException("Invalid origin variable fingerprint.");
                }

                // Each BIOS release may override the fingerprint of a named varstore.
                // Unknown names, duplicate overrides and impossible header offsets are errors.
                foreach (Release release in entry.Releases)
                {
                    if (release.FingerprintOverrides == null) continue;
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (VariableSizeOverride item in release.FingerprintOverrides)
                    {
                        if (item == null || string.IsNullOrWhiteSpace(item.Name)
                            || !variableKeys.Contains(item.Name) || !seen.Add(item.Name)
                            || item.Size <= 0 || item.Size > 65536)
                            throw new InvalidDataException("Invalid release fingerprint override.");
                        VariableFingerprint variable = entry.Variables.Single(v =>
                            string.Equals(v.Name, item.Name, StringComparison.OrdinalIgnoreCase));
                        if (variable.Headers.Any(h => h.Offset + 4 > item.Size))
                            throw new InvalidDataException("Release fingerprint is smaller than a header field.");
                    }
                }
            }
        }

        [DataContract]
        internal sealed class Catalog
        {
            [DataMember(Name = "schemaVersion")] public int SchemaVersion;
            [DataMember(Name = "layouts")] public List<LayoutEntry> Layouts;
        }

        [DataContract]
        internal sealed class LayoutEntry
        {
            [DataMember(Name = "id")] public string Id;
            [DataMember(Name = "resolver")] public string Resolver;
            [DataMember(Name = "boardTokens")] public List<string> BoardTokens;
            [DataMember(Name = "releases")] public List<Release> Releases;
            [DataMember(Name = "variables")] public List<VariableFingerprint> Variables;
        }

        [DataContract]
        internal sealed class Release
        {
            [DataMember(Name = "version")] public string Version;
            [DataMember(Name = "aliases")] public List<string> Aliases;
            [DataMember(Name = "status")] public string Status;
            [DataMember(Name = "fingerprintOverrides")] public List<VariableSizeOverride> FingerprintOverrides;
        }

        [DataContract]
        internal sealed class VariableSizeOverride
        {
            [DataMember(Name = "name")] public string Name;
            [DataMember(Name = "size")] public int Size;
        }

        [DataContract]
        internal sealed class VariableFingerprint
        {
            [DataMember(Name = "name")] public string Name;
            [DataMember(Name = "guid")] public string Guid;
            [DataMember(Name = "size")] public int Size;
            [DataMember(Name = "headers")] public List<HeaderValue> Headers;
            [DataMember(Name = "optional")] public bool Optional;
        }

        [DataContract]
        internal sealed class HeaderValue
        {
            [DataMember(Name = "offset")] public int Offset;
            [DataMember(Name = "u32")] public uint Value;
        }

        public sealed class BiosOriginCatalogMatch
        {
            private readonly LayoutEntry entry;
            private readonly Release release;

            internal BiosOriginCatalogMatch(LayoutEntry entry, Release release)
            {
                this.entry = entry;
                this.release = release;
            }

            public string LayoutId => entry.Id;
            public string Resolver => entry.Resolver;
            public string Status => release.Status;
            public string ReleaseVersion => release.Version;

            public int ExpectedSize(string name)
            {
                VariableFingerprint variable = entry.Variables.Single(v =>
                    string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
                VariableSizeOverride alternative = release.FingerprintOverrides?.SingleOrDefault(v =>
                    string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
                return alternative == null ? variable.Size : alternative.Size;
            }

            public IReadOnlyList<BiosOriginCatalogVariable> Variables =>
                entry.Variables.Select(v =>
                    new BiosOriginCatalogVariable(v.Name, v.Guid, v.Optional, ExpectedSize(v.Name))).ToList();

            // Evaluates the full fingerprint, not just SMBIOS model and version.
            // Missing blobs are mismatches; never assume that a candidate is valid.
            public string CheckFingerprint(IDictionary<string, byte[]> blobs)
            {
                if (blobs == null) return "No UEFI variable data supplied.";
                foreach (VariableFingerprint variable in entry.Variables)
                {
                    byte[] bytes;
                    if (!blobs.TryGetValue(variable.Name, out bytes) || bytes == null)
                    {
                        if (variable.Optional) continue;
                        return variable.Name + ": missing UEFI variable";
                    }
                    if (bytes.Length != ExpectedSize(variable.Name))
                        return variable.Name + ": unexpected variable size";
                    foreach (HeaderValue header in variable.Headers)
                    {
                        int offset = header.Offset;
                        uint actual = (uint)bytes[offset] | ((uint)bytes[offset + 1] << 8)
                            | ((uint)bytes[offset + 2] << 16) | ((uint)bytes[offset + 3] << 24);
                        if (actual != header.Value)
                            return variable.Name + ": header fingerprint differs at offset 0x"
                                + offset.ToString("X");
                    }
                }
                return null;
            }
        }

        public sealed class BiosOriginCatalogVariable
        {
            internal BiosOriginCatalogVariable(string name, string guid, bool optional, int size)
            {
                Name = name;
                Optional = optional;
                Size = size;
                // Win32 GetFirmwareEnvironmentVariableExW requires a GUID string
                // with braces, even though the catalog stores canonical UUIDs.
                Guid = "{" + System.Guid.Parse(guid).ToString("D") + "}";
            }
            public string Name { get; }
            public string Guid { get; }
            public bool Optional { get; }
            public int Size { get; }
        }
    }
}
