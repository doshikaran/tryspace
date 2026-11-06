using System.Collections.Generic;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Tryscape.Unity.Experience
{
    /// <summary>Source of "is any hand tracked right now" for <see cref="Tryscape.Application.Rehearsal.HandLossPolicy"/>.</summary>
    public interface IHandTrackingSource
    {
        bool AnyHandTracked { get; }
    }

    /// <summary>Reads the Interaction SDK hands in the rig (both handedness); polled once per frame, no allocation.</summary>
    public sealed class HandTrackingWatcher : MonoBehaviour, IHandTrackingSource
    {
        [SerializeField] List<Hand> _hands = new List<Hand>();

        public void Configure(IEnumerable<Hand> hands) => _hands = new List<Hand>(hands);

        public int HandCount => _hands.Count;

        public bool AnyHandTracked
        {
            get
            {
                for (var i = 0; i < _hands.Count; i++)
                {
                    var hand = _hands[i];
                    if (hand != null && hand.isActiveAndEnabled && hand.IsTrackedDataValid)
                        return true;
                }
                return false;
            }
        }
    }
}
