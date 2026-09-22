using Microsoft.Data.Sqlite;
using OpenCodeSpend.Data;

// Проверки, которые ломаются, если изоляция пользователей или миграция сломаны.
// Запуск: dotnet run --project tests/OpenCodeSpend.Tests

var ok = 0;
var fail = 0;
void Check(bool cond, string what)
{
    if (cond) { ok++; Console.WriteLine("  ok   " + what); }
    else { fail++; Console.WriteLine("  FAIL " + what); }
}

var dir = Path.Combine(Path.GetTempPath(), "ocspend-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
var epoch = DateTimeOffset.UnixEpoch;
var future = DateTimeOffset.UtcNow.AddDays(1);

static UsageRecord Usage(string id, string session, decimal cost) =>
    new(id, session, null, null, "agent", null, "prov", "model", null, DateTimeOffset.UtcNow, cost, 10, 20, 0, 0, 0);

static SessionRecord Session(string id, string title) =>
    new(id, null, null, "agent", title, "prov", "model", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 1, 1, 0, 0);

try
{
    // ---------- 1. миграция однопользовательской базы ----------
    Console.WriteLine("миграция v1 -> v2");
    var legacyPath = Path.Combine(dir, "legacy.db");
    using (var cn = new SqliteConnection($"Data Source={legacyPath}"))
    {
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            create table usage_record(id text primary key, session_id text not null, parent_session_id text, project_id text,
              agent text, mode text, provider_id text, model_id text, variant text, ts text not null, cost real not null default 0,
              input_tokens integer not null default 0, output_tokens integer not null default 0, reasoning_tokens integer not null default 0,
              cache_read integer not null default 0, cache_write integer not null default 0, user_id text);
            create table session_meta(id text primary key, parent_id text, project_id text, agent text, title text,
              provider_id text, model_id text, created text, updated text, cost real not null default 0,
              input_tokens integer not null default 0, output_tokens integer not null default 0,
              reasoning_tokens integer not null default 0, cache_read integer not null default 0, user_id text);
            create table sync_state(key text primary key, value text, updated text, user_id text);
            create table site_profile(id integer primary key check (id = 1), fetched_at text, email text, plan text, region text,
              rolling_usage real, rolling_limit real, rolling_pct real, rolling_reset_sec integer,
              weekly_usage real, weekly_limit real, weekly_pct real, weekly_reset_sec integer,
              monthly_usage real, monthly_limit real, monthly_pct real, monthly_reset_sec integer,
              balance real, use_balance integer, monthly_spend_limit real, lite_subscription_id text);
            create table site_usage(id text primary key, time_created text not null, model text, provider text, plan text,
              input_tokens integer not null default 0, output_tokens integer not null default 0, reasoning_tokens integer not null default 0,
              cache_read integer not null default 0, cost real not null default 0, session_id text, key_id text, synced_at text);
            create table site_payment(id text primary key, paid_at text, amount real not null default 0,
              refunded integer not null default 0, receipt_url text, synced_at text);
            create table control(key text primary key, value text, updated text);
            create table device(id text primary key, user_id text, name text, created text, last_seen text);
            create table zen_usage(period text not null, scope text not null, fetched_at text, row text not null);

            insert into usage_record(id,session_id,ts,cost) values('old1','ses_old','2026-09-01T00:00:00.000Z',3.5);
            insert into session_meta(id,title,updated) values('ses_old','старый чат','2026-09-01T00:00:00.000Z');
            insert into site_usage(id,time_created,cost) values('usg_old','2026-09-01T00:00:00.000Z',1.25);
            insert into site_profile(id,email,plan) values(1,'old@example.com','lite');
            insert into sync_state(key,value) values('profile_cookie','auth=old');
            """;
        cmd.ExecuteNonQuery();
    }
    var legacyDb = new Db(legacyPath);
    legacyDb.EnsureSchema();
    var legacySpend = new SpendStore(legacyDb);
    var legacyProfile = new ProfileStore(legacyDb);
    var legacyTotals = await legacySpend.TotalsAsync(epoch, future, Db.LocalUser);
    Check(legacyTotals.Cost == 3.5m && legacyTotals.Messages == 1, "старые записи сохранены и принадлежат local");
    Check((await legacyProfile.GetProfileAsync(Db.LocalUser))?.Email == "old@example.com", "старый профиль сохранён");
    Check(await legacySpend.GetStateAsync("profile_cookie", Db.LocalUser) == "auth=old", "старое состояние сохранено");
    Check(await legacySpend.TotalsAsync(epoch, future, "uA") is { Messages: 0 }, "мигрированные записи не видны чужому пользователю");

    // ---------- 2. изоляция пользователей ----------
    Console.WriteLine("изоляция пользователей");
    var db = new Db(Path.Combine(dir, "multi.db"));
    db.EnsureSchema();
    var spend = new SpendStore(db);
    var profile = new ProfileStore(db);

    await spend.UpsertUsageAsync(new[] { Usage("a1", "ses_a", 1.5m) }, "uA");
    await spend.UpsertUsageAsync(new[] { Usage("b1", "ses_b", 2.5m) }, "uB");
    var ta = await spend.TotalsAsync(epoch, future, "uA");
    var tb = await spend.TotalsAsync(epoch, future, "uB");
    Check(ta.Cost == 1.5m && ta.Messages == 1, "A видит только свои записи");
    Check(tb.Cost == 2.5m && tb.Messages == 1, "B видит только свои записи");

    // один и тот же id у разных пользователей не перетирает данные
    await spend.UpsertUsageAsync(new[] { Usage("same", "ses_x", 10m) }, "uA");
    await spend.UpsertUsageAsync(new[] { Usage("same", "ses_x", 99m) }, "uB");
    Check((await spend.TotalsAsync(epoch, future, "uA")).Cost == 11.5m, "чужой id не перезаписывает мою строку");
    Check((await spend.TotalsAsync(epoch, future, "uB")).Cost == 101.5m, "свой id обновляется у себя");

    await profile.UpsertUsageAsync(new List<SiteUsage> { new("usg_1", DateTimeOffset.UtcNow, "m", "p", null, 1, 1, 0, 0, 0.5m, "ses_a", null) }, "uA");
    await profile.UpsertUsageAsync(new List<SiteUsage> { new("usg_1", DateTimeOffset.UtcNow, "m", "p", null, 1, 1, 0, 0, 7m, "ses_b", null) }, "uB");
    var pa = await profile.PeriodAsync(epoch, epoch, epoch, "uA");
    var pb = await profile.PeriodAsync(epoch, epoch, epoch, "uB");
    Check(pa.today == 0.5m && pb.today == 7m, "история профиля изолирована");

    var sa = await profile.SummaryAsync("UTC", "uA");
    Check(sa.ContainsKey("byModel") && ((System.Collections.ICollection)sa["byModel"]).Count == 1, "свод профиля не смешивает пользователей");
    Check((await profile.BySessionAsync(50, "uB")).Count == 1, "чаты изолированы");
    Check((await spend.LiveAsync("uA")).Root is null, "локальный эфир пуст у пользователя без сессий");

    await spend.UpsertSessionsAsync(new[] { Session("ses_a", "чат A") }, "uA");
    Check((await spend.SessionsAsync(epoch, 10, "uA")).Count == 1, "сессии изолированы");
    Check((await spend.SessionsAsync(epoch, 10, "uB")).Count == 0, "чужие сессии не видны");

    // ---------- 3. пригласительные коды и устройства ----------
    Console.WriteLine("пригласительные коды и устройства");
    const string srv = "https://example.com";
    var devices = new DeviceService(db, new SpendConfig { DeviceToken = null });

    var round = InviteCode.Decode(InviteCode.Encode(srv, "SECRET12"));
    Check(round?.Url == srv && round?.Secret == "SECRET12", "адрес сервера восстанавливается из кода");
    Check(!InviteCode.Encode(srv, "SECRET12").Contains("example"), "адрес сервера не читается снаружи");
    Check(InviteCode.Decode("OCSP1.неверныйкод") is null, "битый код отклоняется");
    Check(InviteCode.Decode("") is null, "пустой код отклоняется");

    var (code, expires) = devices.CreatePairing("uA", srv);
    Check(expires - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(5.5), "код живёт 5 минут");
    Check(InviteCode.Decode(code)?.Url == srv, "в коде зашит адрес сервера");
    var claimed = devices.Claim(code, "PC-A");
    Check(claimed is not null, "код подключения принимается");
    Check(claimed!.Value.UserId == "uA", "токен принадлежит владельцу кода");
    Check(devices.Resolve(claimed.Value.Token)?.UserId == "uA", "токен распознаётся");
    Check(devices.Claim(code, "PC-A2") is null, "код одноразовый");
    Check(devices.Claim(InviteCode.Encode(srv, "NOSUCH"), "PC-X") is null, "чужой код отклоняется");

    var madeB = devices.Create("uB", "PC-B");
    Check(devices.Resolve(madeB.Token)?.UserId == "uB", "токен второго пользователя распознаётся");
    Check(devices.Resolve("ocsd_подделка") is null, "поддельный токен отклоняется");
    Check(!devices.Revoke("uA", madeB.Device.Id), "чужое устройство не отзывается");
    Check(devices.Revoke("uB", madeB.Device.Id), "своё устройство отзывается");
    Check(devices.Resolve(madeB.Token) is null, "отозванный токен больше не работает");

    // ---------- 4. очередь «стоп» по пользователям ----------
    Console.WriteLine("очередь команд «стоп»");
    var state = new ControlState(db);
    state.EnqueueStop("uA", "ses_1");
    state.SetSessions("uB", new object[] { new { id = "ses_2", active = true, children = Array.Empty<object>() } });
    Check(state.GetPending("uA").SequenceEqual(new[] { "ses_1" }), "команда A лежит у A");
    Check(state.GetPending("uB").Count == 0, "команда A не видна B");
    Check(state.GetSessionsRaw("uA").Contains("\"items\":[]"), "снимок B не виден A");

    // ---------- 5. гистерезис активности сессий ----------
    Console.WriteLine("SessionActivityGate");
    var gate = new SessionActivityGate(5000);
    Check(gate.Apply("s", true, 0), "gate: стартовое активно");
    Check(gate.Apply("s", false, 1000), "gate: в пределах 5с не меняем");
    Check(gate.Apply("s", false, 4000), "gate: всё ещё держим");
    Check(!gate.Apply("s", false, 5001), "gate: после 5с применяем неактивно");
    Check(!gate.Apply("s", true, 6000), "gate: обратно ждём 5с");
    Check(gate.Apply("s", true, 11002), "gate: после 5с применяем активно");
    gate.ForceInactive("s", 20000);
    Check(!gate.Apply("s", true, 20001), "gate: после стопа считаем завершённой");
    Check(!gate.Apply("s", true, 24000), "gate: держим 5с после стопа");
    Check(gate.Apply("s", true, 25001), "gate: после стопа снова реальный статус");
}
catch (Exception e)
{
    fail++;
    Console.WriteLine("  FAIL исключение: " + e);
}
finally
{
    try { Directory.Delete(dir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"проверок: {ok + fail}, успешно: {ok}, провалено: {fail}");
return fail == 0 ? 0 : 1;
