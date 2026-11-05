using System;
using Tryscape.Application.Editing;
using Tryscape.Core.Model;
using UnityEngine;

namespace Tryscape.Unity.Presentation
{
    /// <summary>
    /// Presentation of one entity. Its transform mirrors the committed model pose, except while a drag preview moves
    /// it; it is then reset from the model on release. Carries the domain ID so input adapters never need Unity
    /// instance IDs to address entities (S11).
    /// </summary>
    public sealed class EntityView : MonoBehaviour
    {
        [SerializeField] TextMesh _label;
        [SerializeField] Renderer _body;
        [SerializeField] Material _selectedMaterial;

        Material _baseMaterial;

        public Guid EntityId { get; private set; }
        public EntityRole Role { get; private set; }
        public bool Editable { get; private set; }
        public string Label { get; private set; }
        public CatalogEntry Entry { get; private set; }

        public void Bind(EntitySpec entity, CatalogEntry entry)
        {
            EntityId = entity.Id;
            Role = entity.Role;
            Editable = entity.Editable;
            Label = entity.Label;
            Entry = entry;
            if (_label != null)
                _label.text = entity.Editable ? $"{entity.Label} (movable)" : entity.Label;
            if (_body != null)
                _baseMaterial = _body.sharedMaterial;
        }

        public void ShowCommitted(EntitySpec entity, RoomSpec room)
        {
            var (x, z) = ModelSpaceMapping.ToLocal(room, entity.Pose.XMm, entity.Pose.ZMm);
            transform.localPosition = new Vector3(x, 0f, z);
            transform.localRotation = Quaternion.Euler(0f, ModelSpaceMapping.ToLocalYaw(entity.Pose.YawDeg), 0f);
        }

        public void SetSelected(bool selected)
        {
            if (_body != null && _selectedMaterial != null)
                _body.sharedMaterial = selected ? _selectedMaterial : _baseMaterial;
        }

        public void Configure(TextMesh label, Renderer body, Material selected)
        {
            _label = label;
            _body = body;
            _selectedMaterial = selected;
        }
    }
}
