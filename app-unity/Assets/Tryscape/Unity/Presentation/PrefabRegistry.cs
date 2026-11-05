using System;
using System.Collections.Generic;
using UnityEngine;

namespace Tryscape.Unity.Presentation
{
    /// <summary>
    /// Maps catalog prefab keys to curated local prefabs (S06.5). A scene or proposal can only name a key; it can
    /// never supply a path, mesh or shader. Unknown keys fail scene construction rather than spawning placeholders.
    /// </summary>
    [CreateAssetMenu(menuName = "Tryscape/Prefab Registry", fileName = "PrefabRegistry")]
    public sealed class PrefabRegistry : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            public string key;
            public GameObject prefab;
        }

        [SerializeField] List<Entry> _entries = new List<Entry>();

        public bool TryGet(string key, out GameObject prefab)
        {
            foreach (var entry in _entries)
                if (entry.key == key && entry.prefab != null)
                {
                    prefab = entry.prefab;
                    return true;
                }
            prefab = null;
            return false;
        }

        public void SetEntries(IEnumerable<Entry> entries)
        {
            _entries = new List<Entry>(entries);
        }
    }
}
