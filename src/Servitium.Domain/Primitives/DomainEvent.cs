using System;

namespace Servitium.Domain.Primitives;

/// <summary>
/// 表示在 UTC 时间发生的领域事件。
/// </summary>
public abstract record DomainEvent : ValueObject
{
    /// <summary>初始化领域事件。</summary>
    protected DomainEvent(Guid eventId, DateTimeOffset occurredAt)
    {
        EventId = Guard.AgainstEmpty(eventId, nameof(eventId));
        OccurredAt = Guard.AsUtc(occurredAt);
    }

    /// <summary>获取事件标识。</summary>
    public Guid EventId { get; }

    /// <summary>获取 UTC 发生时间。</summary>
    public DateTimeOffset OccurredAt { get; }
}
