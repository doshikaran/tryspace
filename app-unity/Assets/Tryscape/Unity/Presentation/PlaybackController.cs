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
        PlaybackTimeline _timeline;
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
            Apply();
        }

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
        }
    }
}
