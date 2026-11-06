using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Meta.XR.MRUtilityKit;
using Tryscape.Application.Placement;
using UnityEngine;

namespace Tryscape.Unity.MixedReality
{
    /// <summary>Result of one room-data request: status, the horizontal surfaces found and the floor height.</summary>
    public sealed class SurfaceScan
    {
        public SurfaceScan(SurfaceScanStatus status, IReadOnlyList<SurfaceCandidate> candidates, double floorY)
        {
            Status = status;
            Candidates = candidates ?? Array.Empty<SurfaceCandidate>();
            FloorY = floorY;
        }

        public SurfaceScanStatus Status { get; }
        public IReadOnlyList<SurfaceCandidate> Candidates { get; }
        public double FloorY { get; }

        public static SurfaceScan Failed(SurfaceScanStatus status) => new SurfaceScan(status, null, 0);
    }

    /// <summary>Port to the headset's scene model. PlayMode tests inject a fixed room.</summary>
    public interface ISurfaceSource
    {
        /// <param name="allowSpaceSetup">When true and no room exists, the system Space Setup flow may open.</param>
        Task<SurfaceScan> ScanAsync(bool allowSpaceSetup);
    }

    /// <summary>
    /// Meta MR Utility Kit adapter. MRUK handles the spatial-data permission check; a missing permission, a headset
    /// without a room and a load error each map to a typed status, never an exception reaching the UI.
    /// Plane anchors face along their local +z, so the surface normal is the anchor's forward vector.
    /// </summary>
    public sealed class MrukSurfaceSource : ISurfaceSource
    {
        public async Task<SurfaceScan> ScanAsync(bool allowSpaceSetup)
        {
            var mruk = MRUK.Instance;
            if (mruk == null)
                return SurfaceScan.Failed(SurfaceScanStatus.Unavailable);
            MRUK.LoadDeviceResult result;
            try
            {
                result = await mruk.LoadSceneFromDevice(requestSceneCaptureIfNoDataFound: allowSpaceSetup);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tryscape] Room data load failed: {e.GetType().Name}");
                return SurfaceScan.Failed(SurfaceScanStatus.Unavailable);
            }
            switch (result)
            {
                case MRUK.LoadDeviceResult.Success:
                    break;
                case MRUK.LoadDeviceResult.NoScenePermission:
                    return SurfaceScan.Failed(SurfaceScanStatus.NoPermission);
                case MRUK.LoadDeviceResult.NoRoomsFound:
                    return SurfaceScan.Failed(SurfaceScanStatus.NoRoom);
                default:
                    Debug.LogWarning($"[Tryscape] Room data load result: {result}");
                    return SurfaceScan.Failed(SurfaceScanStatus.Unavailable);
            }

            var room = mruk.GetCurrentRoom();
            if (room == null)
                return SurfaceScan.Failed(SurfaceScanStatus.NoRoom);
            var floorY = room.FloorAnchor != null ? room.FloorAnchor.transform.position.y : 0f;
            var candidates = new List<SurfaceCandidate>();
            foreach (var anchor in room.Anchors)
            {
                if (anchor == null || !anchor.PlaneRect.HasValue)
                    continue;
                var label = PrimaryLabel(anchor);
                if (label == null)
                    continue;
                var t = anchor.transform;
                var size = anchor.PlaneRect.Value.size;
                candidates.Add(new SurfaceCandidate($"{label}-{candidates.Count}", label, t.position.x, t.position.y,
                    t.position.z, t.forward.y, size.x, size.y));
            }
            return new SurfaceScan(candidates.Count > 0 ? SurfaceScanStatus.Found : SurfaceScanStatus.NoSuitableSurface,
                candidates, floorY);
        }

        static string PrimaryLabel(MRUKAnchor anchor)
        {
            if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.TABLE)) return "TABLE";
            if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.COUCH)) return "COUCH";
            if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.STORAGE)) return "STORAGE";
            if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.BED)) return "BED";
            if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.OTHER)) return "OTHER";
            return null;
        }
    }
}
