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
                        .Where(release => VersionMatches(biosVersion, release.Version))
                        .Select(release => new BiosOriginCatalogMatch(layout, release.Status)))
                    .ToList();
                if (candidates.Count > 1)
                    throw new InvalidDataException("Ambiguous origin layout catalog match.");
                return candidates.SingleOrDefault();
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

        private static bool VersionMatches(string running, string expected)
        {
            if (string.IsNullOrWhiteSpace(running)) return false;
            return Regex.IsMatch(running, @"(?:^|[^0-9.])" + Regex.Escape(expected)
                + @"(?:$|[^0-9.])", RegexOptions.CultureInvariant);
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
                if (entry.Releases.Any(r => string.IsNullOrWhiteSpace(r.Version)
                    || (r.Status != "verified" && r.Status != "derived" && r.Status != "ai-proposed"))
                    || entry.Releases.Select(r => r.Version).Distinct(StringComparer.Ordinal).Count() != entry.Releases.Count)
                    throw new InvalidDataException("Invalid origin layout release status/version.");
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
            [DataMember(Name = "status")] public string Status;
        }

        [DataContract]
        internal sealed class VariableFingerprint
        {
            [DataMember(Name = "name")] public string Name;
            [DataMember(Name = "guid")] public string Guid;
            [DataMember(Name = "size")] public int Size;
            [DataMember(Name = "headers")] public List<HeaderValue> Headers;
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

            internal BiosOriginCatalogMatch(LayoutEntry entry, string status)
            {
                this.entry = entry;
                Status = status;
            }

            public string LayoutId => entry.Id;
            public string Resolver => entry.Resolver;
            public string Status { get; }

            public IReadOnlyList<BiosOriginCatalogVariable> Variables =>
                entry.Variables.Select(v => new BiosOriginCatalogVariable(v.Name, v.Guid)).ToList();

            // Evaluates the full fingerprint, not just SMBIOS model and version.
            // Missing blobs are mismatches; never assume that a candidate is valid.
            public string CheckFingerprint(IDictionary<string, byte[]> blobs)
            {
                if (blobs == null) return "No UEFI variable data supplied.";
                foreach (VariableFingerprint variable in entry.Variables)
                {
                    byte[] bytes;
                    if (!blobs.TryGetValue(variable.Name, out bytes) || bytes == null)
                        return variable.Name + ": missing UEFI variable";
                    if (bytes.Length != variable.Size)
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
            internal BiosOriginCatalogVariable(string name, string guid)
            {
                Name = name;
                Guid = guid;
            }
            public string Name { get; }
            public string Guid { get; }
        }
    }
}
