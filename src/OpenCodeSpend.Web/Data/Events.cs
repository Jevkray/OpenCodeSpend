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
                case "session.next.text.started":
                case "session.next.text.delta":
                    Set(sid, true, "печатает ответ", null); break;
                case "session.next.text.ended":
                    Set(sid, true, "ответ закончен", null); break;
                case "session.next.reasoning.started":
                    Set(sid, true, "размышляет", null); break;
                case "session.next.reasoning.ended":
                    Set(sid, true, "размышление закончено", null); break;
                case "session.next.step.started":
                    Set(sid, true, "работает", Str("agent")); break;
                case "session.next.step.ended":
                    Set(sid, true, "шаг завершён", null); break;
                case "session.next.step.failed":
                    Set(sid, true, "ошибка шага", null); break;
                case "session.next.tool.called":
                    Set(sid, true, "вызывает инструмент", Str("tool")); break;
                case "session.next.tool.success":
                    Set(sid, true, "инструмент выполнен", Str("tool")); break;
                case "session.next.tool.failed":
                    Set(sid, true, "инструмент не выполнился", Str("tool")); break;
                case "session.next.shell.started":
                    Set(sid, true, "выполняет команду", Short(Str("command"))); break;
                case "session.next.shell.ended":
                    Set(sid, true, "команда выполнена", null); break;
                case "session.idle":
                    Set(sid, false, "простаивает", null); break;
                case "session.deleted":
                    Forget(sid); break;
                default:
                    // session.updated / created / message.* и прочее — только отметка «была активность»
                    if (type.StartsWith("session.next.", StringComparison.Ordinal) || type == "session.active")
                        Set(sid, true, "работает", null);
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
