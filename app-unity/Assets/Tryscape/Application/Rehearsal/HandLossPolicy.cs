using System;

namespace Tryscape.Application.Rehearsal
{
    /// <summary>
    /// S05.3: pause replay when hand tracking is lost. Fires once when both hands have been untracked for
    /// <see cref="GraceSeconds"/> after having been tracked, so a brief dropout or a hand moving out of view and straight
    /// back does not interrupt the replay. It never resumes anything itself, because resuming would be unexpected
    /// movement; the user resumes by hand. If hands were never tracked (Editor, controllers only), it never fires.
    /// </summary>
    public sealed class HandLossPolicy
    {
        public const double GraceSeconds = 0.5;

        bool _seenTracked;
        bool _fired;
        double _lostFor;

        /// <summary>True while both hands are untracked after having been tracked.</summary>
        public bool HandsLost => _seenTracked && _lostFor > 0;

        /// <summary>Advances by one frame. Returns true exactly once per loss, when the grace period has elapsed.</summary>
        public bool Update(bool anyHandTracked, double deltaSeconds)
        {
            if (deltaSeconds < 0 || double.IsNaN(deltaSeconds))
                throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
            if (anyHandTracked)
            {
                _seenTracked = true;
                _lostFor = 0;
                _fired = false;
                return false;
            }
            if (!_seenTracked)
                return false;
            _lostFor += deltaSeconds;
            if (_fired || _lostFor < GraceSeconds)
                return false;
            _fired = true;
            return true;
        }
    }
}
