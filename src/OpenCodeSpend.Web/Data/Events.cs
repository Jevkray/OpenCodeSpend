using System.Collections.Concurrent;
using System.Text.Json;

namespace OpenCodeSpend.Data;

/// <summary>Живое состояние сессий из потока событий opencode (SSE /event).
/// Даёт моментальный отклик: видно, что агент печатает, вызывает инструмент или завершился.</summary>
public sealed class OpencodeEvents
{
    public sealed record Live(bool Active, string Phase, string? Detail, DateTimeOffset At);

    private readonly ConcurrentDictionary<string, Live> _live = new();

    /// <summary>Что-то изменилось в состоянии сессий (для мгновенной отправки на сервер).</summary>
    public event Action? Changed;

    public Live? Get(string sessionId) => _live.TryGetValue(sessionId, out var v) ? v : null;
    public void Forget(string sessionId) => _live.TryRemove(sessionId, out _);

    /// <summary>Разбирает строку события SSE и обновляет состояние.</summary>
    public void Apply(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(type)) return;
            if (!doc.RootElement.TryGetProperty("properties", out var p) || p.ValueKind != JsonValueKind.Object) return;
            var sid = p.TryGetProperty("sessionID", out var s) ? s.GetString() : null;
            if (string.IsNullOrEmpty(sid)) return;
            string? Str(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            switch (type)
            {
                // реальный статус сессии из opencode: busy/retry/idle
                case "session.status":
                {
                    var status = p.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object && st.TryGetProperty("type", out var stt)
                        ? stt.GetString() : null;
                    if (string.Equals(status, "idle", StringComparison.OrdinalIgnoreCase)) Set(sid, false, "done", null);
                    else Set(sid, true, string.Equals(status, "retry", StringComparison.OrdinalIgnoreCase) ? "retry" : "working", null);
                    break;
                }
                case "session.idle":
                    Set(sid, false, "done", null); break;
                case "session.deleted":
                    Forget(sid); break;
                // детали работы (что именно делает агент) — фаза при этом остаётся «работает»
                case "session.next.text.started":
                case "session.next.text.delta":
                    Set(sid, true, "working", "печатает ответ"); break;
                case "session.next.reasoning.started":
                case "session.next.reasoning.delta":
                    Set(sid, true, "working", "размышляет"); break;
                case "session.next.step.started":
                    Set(sid, true, "working", Str("agent")); break;
                case "session.next.tool.called":
                case "session.next.tool.progress":
                    Set(sid, true, "working", Str("tool")); break;
                case "session.next.tool.failed":
                    Set(sid, true, "working", Str("tool")); break;
                case "session.next.shell.started":
                    Set(sid, true, "working", Short(Str("command"))); break;
                default:
                    if (type.StartsWith("session.next.", StringComparison.Ordinal))
                        Set(sid, true, "working", null);
                    break;
            }

            Changed?.Invoke();
        }
        catch { }
    }

    private void Set(string sid, bool active, string phase, string? detail)
        => _live[sid] = new Live(active, phase, detail, DateTimeOffset.UtcNow);

    private static string? Short(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : (s.Length > 60 ? s[..60] + "…" : s);
}
