using System;

namespace Servitium.Domain.Primitives;

/// <summary>
/// 为领域实体提供稳定标识与基于标识的相等性。
/// </summary>
public abstract class Entity : IEquatable<Entity>
{
    /// <summary>
    /// 初始化实体。
    /// </summary>
    protected Entity(Guid id)
    {
        _ = Guard.AgainstEmpty(id, nameof(id));
        Id = id;
    }

    /// <summary>
    /// 获取实体标识。
    /// </summary>
    public Guid Id { get; }

    /// <inheritdoc />
    public bool Equals(Entity? other)
    {
        return other is not null && other.GetType() == GetType() && other.Id == Id;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is Entity other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(GetType(), Id);
    }
}
