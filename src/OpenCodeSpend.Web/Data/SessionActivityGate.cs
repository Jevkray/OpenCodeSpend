using System.Collections.Concurrent;

namespace OpenCodeSpend.Data;

/// <summary>Гистерезис активности сессии: показанное состояние меняется не чаще раза в holdMs,
/// чтобы индикатор не мигал. Подтверждённая остановка применяет «завершена» сразу и держит holdMs.</summary>
public sealed class SessionActivityGate(int holdMs = 5000)
{
    private sealed class Entry { public bool Active; public long At; public long ForceUntil; }
    private readonly ConcurrentDictionary<string, Entry> _state = new();

    /// <summary>Стабильное состояние сессии: raw — «сырое» из очередного опроса, nowMs — текущее время в мс.</summary>
    public bool Apply(string id, bool raw, long nowMs)
    {
        var e = _state.GetOrAdd(id, _ => new Entry { Active = raw, At = nowMs });
        if (e.ForceUntil > 0)
        {
            if (nowMs < e.ForceUntil) return false;      // держим «завершена» после стопа
            e.ForceUntil = 0;
            e.Active = raw; e.At = nowMs;                // снова смотрим реальный статус
            return raw;
        }
        if (raw == e.Active) return e.Active;
        if (nowMs - e.At < holdMs) return e.Active;      // не меняем чаще раза в 5с
        e.Active = raw; e.At = nowMs;
        return raw;
    }

    /// <summary>Подтверждённая остановка: сейчас завершена, и держим так holdMs.</summary>
    public void ForceInactive(string id, long nowMs)
        => _state[id] = new Entry { Active = false, At = nowMs, ForceUntil = nowMs + holdMs };
}
