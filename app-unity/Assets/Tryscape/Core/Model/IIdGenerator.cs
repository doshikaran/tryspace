using System;

namespace Tryscape.Core.Model
{
    /// <summary>Supplies new entity/scene/command identifiers. Implemented outside Core (Core holds no randomness).</summary>
    public interface IIdGenerator
    {
        Guid NewId();
    }
}
