using System;
using System.Collections.Generic;
using System.Linq;

namespace Tryscape.Core.Model
{
    public enum CollisionParticipation
    {
        FloorObstruction,
        Payload,
        None,
    }

    /// <summary>Catalog entry (S06.5). Dimensions and collision participation come only from here.</summary>
    public sealed class CatalogEntry
    {
        public CatalogEntry(
            string catalogId,
            string label,
            int footprintWidthMm,
            int footprintDepthMm,
            int renderedHeightMm,
            CollisionParticipation collision,
            IEnumerable<EntityRole> allowedRoles,
            string prefabKey,
            int snapMm,
            IEnumerable<int> allowedYawDeg,
            string assetLicenseId)
        {
            CatalogId = catalogId ?? throw new ArgumentNullException(nameof(catalogId));
            Label = label ?? throw new ArgumentNullException(nameof(label));
            if (footprintWidthMm <= 0 || footprintDepthMm <= 0 || renderedHeightMm <= 0 || snapMm <= 0)
                throw new ArgumentOutOfRangeException(nameof(footprintWidthMm), "Catalog dimensions must be positive");
            FootprintWidthMm = footprintWidthMm;
            FootprintDepthMm = footprintDepthMm;
            RenderedHeightMm = renderedHeightMm;
            Collision = collision;
            AllowedRoles = allowedRoles.ToArray();
            PrefabKey = prefabKey ?? throw new ArgumentNullException(nameof(prefabKey));
            SnapMm = snapMm;
            AllowedYawDeg = allowedYawDeg.ToArray();
            if (AllowedYawDeg.Any(y => !Yaw.IsQuarterTurn(y)))
                throw new ArgumentException("Catalog yaw must be quarter turns", nameof(allowedYawDeg));
            AssetLicenseId = assetLicenseId ?? throw new ArgumentNullException(nameof(assetLicenseId));
        }

        public string CatalogId { get; }
        public string Label { get; }

        /// <summary>Extent along x at yaw 0.</summary>
        public int FootprintWidthMm { get; }

        /// <summary>Extent along z at yaw 0.</summary>
        public int FootprintDepthMm { get; }

        public int RenderedHeightMm { get; }
        public CollisionParticipation Collision { get; }
        public IReadOnlyList<EntityRole> AllowedRoles { get; }
        public string PrefabKey { get; }
        public int SnapMm { get; }
        public IReadOnlyList<int> AllowedYawDeg { get; }
        public string AssetLicenseId { get; }
    }

    public sealed class Catalog
    {
        readonly Dictionary<string, CatalogEntry> _entries;

        public Catalog(string scenarioPackId, string catalogVersion, IEnumerable<CatalogEntry> entries)
        {
            ScenarioPackId = scenarioPackId ?? throw new ArgumentNullException(nameof(scenarioPackId));
            CatalogVersion = catalogVersion ?? throw new ArgumentNullException(nameof(catalogVersion));
            _entries = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (_entries.ContainsKey(entry.CatalogId))
                    throw new ArgumentException($"Duplicate catalog id {entry.CatalogId}", nameof(entries));
                _entries.Add(entry.CatalogId, entry);
            }
        }

        public string ScenarioPackId { get; }
        public string CatalogVersion { get; }
        public IEnumerable<CatalogEntry> Entries => _entries.Values;

        public bool TryGet(string catalogId, out CatalogEntry entry) =>
            _entries.TryGetValue(catalogId ?? string.Empty, out entry);
    }
}
