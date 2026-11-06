using System;
using Tryscape.Application.Editing;
using Tryscape.Core.Evaluation;
using Tryscape.Core.Model;
using UnityEngine;

namespace Tryscape.Unity.Presentation
{
    /// <summary>
    /// Replays a finished evaluation (S08.3). The result is fixed before playback starts; this only moves the payload's
    /// view along the core <see cref="PlaybackTimeline"/>. Each frame the position is recomputed from elapsed logical
    /// time (no velocity integration), so frame rate and hitches change smoothness, never the stop position or result.
    /// </summary>
    public sealed class PlaybackController : MonoBehaviour
    {
        /// <summary>Clearance from the payload footprint edge to each schematic mover's centre (m).</summary>
        const float MoverClearance = 0.15f;
        bool _travelAlongX = true;
        Vector3 _lastPosition;

        PlaybackTimeline _timeline;
        readonly Transform[] _movers = new Transform[2];
        Transform _payload;
        RoomSpec _room;
        double _elapsed;

        public bool IsPlaying { get; private set; }
        public bool HasTimeline => _timeline != null;
        public bool IsFinished => _timeline != null && _elapsed >= _timeline.DurationSeconds;
        public double ElapsedSeconds => _elapsed;
        public double DurationSeconds => _timeline?.DurationSeconds ?? 0;

        /// <summary>Raised once when playback reaches the stop position.</summary>
        public event Action Finished;

        public void Begin(PlaybackTimeline timeline, Transform payloadView, RoomSpec room)
        {
            _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
            _payload = payloadView ? payloadView : throw new ArgumentNullException(nameof(payloadView));
            _room = room ?? throw new ArgumentNullException(nameof(room));
            _elapsed = 0;
            IsPlaying = true;
            _travelAlongX = true;
            _lastPosition = new Vector3(_payload.localPosition.x, 0f, _payload.localPosition.z);
            CreateMovers();
            Apply();
        }

        /// <summary>The two schematic movers shown during replay (D07): narrative context only, never part of a result.</summary>
        public bool MoversVisible => _movers[0] != null;

        public Vector3 MoverLocalPosition(int index) => _movers[index] != null ? _movers[index].localPosition : Vector3.zero;

        public void Pause() => IsPlaying = false;

        public void Resume()
        {
            if (_timeline == null)
                return;
            if (IsFinished)
                _elapsed = 0; // replay from the start
            IsPlaying = true;
            Apply();
        }

        /// <summary>Jumps to a logical time; presentation is recomputed, nothing is re-simulated.</summary>
        public void Seek(double seconds)
        {
            if (_timeline == null)
                return;
            var wasFinished = IsFinished;
            _elapsed = Math.Max(0, Math.Min(seconds, _timeline.DurationSeconds));
            Apply();
            if (!wasFinished && IsFinished)
                Complete();
        }

        /// <summary>Stops playback without changing the committed scene; the caller re-renders the model.</summary>
        public void Stop()
        {
            DestroyMovers();
            _timeline = null;
            _payload = null;
            IsPlaying = false;
            _elapsed = 0;
        }

        void Update()
        {
            if (!IsPlaying || _timeline == null)
                return;
            _elapsed = Math.Min(_elapsed + Time.deltaTime, _timeline.DurationSeconds);
            Apply();
            if (IsFinished)
                Complete();
        }

        void OnApplicationPause(bool paused)
        {
            if (paused)
                Pause();
        }

        // S05.3: replay pauses on focus loss too; it never resumes by itself.
        void OnApplicationFocus(bool focused)
        {
            if (!focused)
                Pause();
        }

        void Complete()
        {
            IsPlaying = false;
            Finished?.Invoke();
        }

        void Apply()
        {
            if (_payload == null || _timeline == null)
                return;
            var (xMm, zMm) = _timeline.DisplayPositionAt(_elapsed);
            var (x, z) = ModelSpaceMapping.ToLocal(_room, xMm, zMm);
            _payload.localPosition = new Vector3(x, _payload.localPosition.y, z);
            // Movers walk beside the payload, perpendicular to the direction of travel, so they never stand ahead of it where a
            // modelled contact happens. They follow the payload exactly and carry no physics.
            var moved = new Vector3(x, 0f, z) - _lastPosition;
            if (moved.sqrMagnitude > 1e-8f)
                _travelAlongX = Mathf.Abs(moved.x) >= Mathf.Abs(moved.z);
            _lastPosition = new Vector3(x, 0f, z);
            var size = _payload.GetComponentInChildren<Renderer>() != null ? _payload.GetComponentInChildren<Renderer>().transform.localScale : new Vector3(1.8f, 0.9f, 0.8f);
            for (var i = 0; i < _movers.Length; i++)
            {
                if (_movers[i] == null)
                    continue;
                var sign = i == 0 ? 1f : -1f;
                _movers[i].localPosition = _travelAlongX
                    ? new Vector3(x, 0f, z + sign * (size.z / 2f + MoverClearance))
                    : new Vector3(x + sign * (size.x / 2f + MoverClearance), 0f, z);
            }
        }

        void CreateMovers()
        {
            DestroyMovers();
            var parent = _payload.parent;
            for (var i = 0; i < _movers.Length; i++)
            {
                var root = new GameObject(i == 0 ? "Mover north (schematic)" : "Mover south (schematic)").transform;
                root.SetParent(parent, false);
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Body";
                Destroy(body.GetComponent<Collider>());
                body.transform.SetParent(root, false);
                body.transform.localScale = new Vector3(0.3f, 0.45f, 0.3f);
                body.transform.localPosition = new Vector3(0, 0.45f, 0);
                var renderer = body.GetComponent<Renderer>();
                var payloadRenderer = _payload.GetComponentInChildren<Renderer>();
                if (payloadRenderer != null)
                    renderer.sharedMaterial = payloadRenderer.sharedMaterial;
                var label = new GameObject("Label").AddComponent<TextMesh>();
                label.transform.SetParent(root, false);
                label.transform.localPosition = new Vector3(0, 1.05f, 0);
                label.text = "Mover (schematic)";
                label.anchor = TextAnchor.LowerCenter;
                label.characterSize = 0.03f;
                label.fontSize = 48;
                _movers[i] = root;
            }
        }

        void DestroyMovers()
        {
            for (var i = 0; i < _movers.Length; i++)
            {
                if (_movers[i] != null)
                    Destroy(_movers[i].gameObject);
                _movers[i] = null;
            }
        }

        void OnDestroy() => DestroyMovers();
    }
}
