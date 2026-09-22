using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using OpenCodeSpend.Data;

namespace OpenCodeSpend;

public static class Server
{
    public static WebApplication Build(string[] args, string? url = null)
    {
        // content root — папка сборки, иначе при запуске из другого каталога не читается appsettings.json
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        if (url is not null) builder.WebHost.UseUrls(url);

        var config = SpendConfig.Load(builder.Configuration);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(new Db(config.DatabasePath));
        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton<SpendStore>();
        builder.Services.AddSingleton(new OpencodeReader(config.OpencodeDbPath));
        builder.Services.AddSingleton<Budgets>();
        builder.Services.AddSingleton<ZenStore>();
        builder.Services.AddSingleton<ZenClient>();
        builder.Services.AddSingleton<ProfileClient>();
        builder.Services.AddSingleton<ProfileStore>();
        builder.Services.AddSingleton<DeviceService>();
        builder.Services.AddSingleton<LinkStore>();
        builder.Services.AddSingleton<GoogleAuth>();
        builder.Services.AddSingleton<GitHubAuth>();
        builder.Services.AddSingleton<SyncService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncService>());
        builder.Services.AddSingleton<ProfileSyncService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ProfileSyncService>());
        builder.Services.AddSingleton<OpencodeControl>();
        builder.Services.AddSingleton<ControlState>();

        var app = builder.Build();

        // за reverse-proxy (Caddy/nginx) видим настоящую схему и хост — нужно для ссылок в приглашении
        // приложение доступно только через наши прокси (Nginx Proxy Manager → nginx → контейнер),
        // поэтому доверяем заголовкам X-Forwarded-* от любого источника
        var fwd = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
        };
        fwd.KnownIPNetworks.Clear();
        fwd.KnownProxies.Clear();
        app.UseForwardedHeaders(fwd);

        var store = app.Services.GetRequiredService<SpendStore>();
        try
        {
            // урезанный режим (Mode=collector) базы не имеет
            if (!config.SkipLocalDb)
            {
                app.Services.GetRequiredService<Db>().EnsureSchema();

                var cookie = store.GetStateAsync("profile_cookie", null).GetAwaiter().GetResult();
                if (string.IsNullOrWhiteSpace(cookie)) cookie = TryCaptureCookie();
                if (!string.IsNullOrWhiteSpace(cookie))
                {
                    config.ProfileCookie = cookie;
                    store.SetStateAsync("profile_cookie", cookie, null).GetAwaiter().GetResult();
                }
            }
        }
        catch (Exception e) { app.Logger.LogError(e, "Не удалось инициализировать схему БД"); }

        // --- авторизация: Google/GitHub (если настроены) или по коду доступа к аккаунту ---
        var sessions = app.Services.GetRequiredService<SessionStore>();
        var links = app.Services.GetRequiredService<LinkStore>();

        var deviceService = app.Services.GetRequiredService<DeviceService>();

        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "/";
            var isPublic = path.StartsWith("/favicon") || path.StartsWith("/api/health")
                           || path.StartsWith("/api/device/sync")
                           || path.StartsWith("/api/device/revoke")
                           || path.StartsWith("/api/devices/redeem") || path.StartsWith("/api/devices/me")
                           || path.StartsWith("/link") || path.StartsWith("/signin-")
                           || path.StartsWith("/app.js") || path.StartsWith("/styles.css") || path.StartsWith("/lib/");

            if (isPublic) { await next(); return; }

            // СЕРВЕР: без настоящего входа доступна только страница входа.
            if (config.IsServer)
            {
                var token = CurrentUser.Read(ctx);
                var uid = sessions.Resolve(token, config.SessionDays);
                var user = uid is null ? null : await store.GetUserAsync(uid, ctx.RequestAborted);
                if (user is null)
                {
                    if (path == "/login" || path == "/login-code" || path.StartsWith("/signin-") || path.StartsWith("/auth/"))
                    { await next(); return; }
                    ctx.Response.Redirect("/login");
                    return;
                }
                ctx.Items["userId"] = uid;
                if (path == "/login") { ctx.Response.Redirect("/"); return; }

                // пока ни один компьютер не подключён — показываем экран «Подключить ПК»
                if ((path == "/" || path == "/index.html") && deviceService.List(uid!).Count == 0)
                {
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    await ctx.Response.WriteAsync(PairPage.Replace("TTLMIN", config.PairingTtlMinutes + " мин"));
                    return;
                }
                await next();
                return;
            }

            // ПРИЛОЖЕНИЕ: локальная панель работает без входа
            ctx.Items["userId"] = CurrentUser.LocalId;
            if (config.SkipLocalDb && links.Get() is null && (path == "/" || path == "/index.html"))
            {
                ctx.Response.Redirect("/login");
                return;
            }
            await next();
        });

        // Область данных: на сервере — аккаунт пользователя, на сборщике — локальная машина.
        string? Scope(HttpContext ctx) => config.IsServer ? CurrentUser.Uid(ctx) : null;

        app.MapGet("/login", (HttpContext ctx, GoogleAuth g, GitHubAuth gh) =>
        {
            // СЕРВЕР: вход через Google/GitHub или по коду доступа к аккаунту
            if (config.IsServer)
            {
                var prov = (g.Enabled ? GoogleButton : "") + (gh.Enabled ? GitHubButton : "");
                var html = LoginProviders
                    .Replace("PROVIDERS", prov)
                    .Replace("ERRPLACEHOLDER", LoginError(ctx.Request.Query["e"]));
                return Results.Content(html, "text/html; charset=utf-8");
            }

            // СБОРЩИК: экран ввода кода подключения этого ПК к серверу
            var err = ctx.Request.Query.ContainsKey("e") ? LinkError(ctx.Request.Query["e"]) : "";
            return Results.Content(LinkPage.Replace("ERRPLACEHOLDER", err), "text/html; charset=utf-8");
        });

        // вход через провайдеров (Google/GitHub) — с любого браузера
        app.MapGet("/auth/google", (HttpContext ctx, GoogleAuth g) =>
        {
            var redirect = $"{ctx.Request.Scheme}://{ctx.Request.Host}/signin-google";
            return !config.IsServer ? Results.Redirect("/")
                : !g.Enabled ? Results.Redirect("/login")
                : Results.Redirect(g.AuthorizeUrl(NewState(ctx), redirect));
        });

        app.MapGet("/signin-google", async (HttpContext ctx, GoogleAuth g, SpendStore s, CancellationToken ct) =>
        {
            if (!config.IsServer) return Results.Redirect("/");
            if (ctx.Request.Query["state"].ToString() != ctx.Request.Cookies["ocspend_state"]) return Results.Redirect("/login?e=state");
            ctx.Response.Cookies.Delete("ocspend_state");
            var code = ctx.Request.Query["code"].ToString();
            if (string.IsNullOrEmpty(code)) return Results.Redirect("/login?e=denied");
            var redirect = $"{ctx.Request.Scheme}://{ctx.Request.Host}/signin-google";
            var (email, name, picture, err) = await g.ExchangeAsync(code, redirect, ct);
            if (string.IsNullOrEmpty(email)) return Results.Redirect("/login?e=auth");
            var user = await s.UpsertUserAsync(email, name, picture, "google", ct);
            CurrentUser.Write(ctx, sessions.Create(user.Id, config.SessionDays, ctx.Request.Headers.UserAgent.ToString(), ctx.Connection.RemoteIpAddress?.ToString()), config.SessionDays, ctx.Request.IsHttps);
            return Results.Redirect("/");
        });

        app.MapGet("/auth/github", (HttpContext ctx, GitHubAuth gh) =>
        {
            var redirect = $"{ctx.Request.Scheme}://{ctx.Request.Host}/signin-github";
            return !config.IsServer ? Results.Redirect("/")
                : !gh.Enabled ? Results.Redirect("/login")
                : Results.Redirect(gh.AuthorizeUrl(NewState(ctx), redirect));
        });

        app.MapGet("/signin-github", async (HttpContext ctx, GitHubAuth gh, SpendStore s, CancellationToken ct) =>
        {
            if (!config.IsServer) return Results.Redirect("/");
            if (ctx.Request.Query["state"].ToString() != ctx.Request.Cookies["ocspend_state"]) return Results.Redirect("/login?e=state");
            ctx.Response.Cookies.Delete("ocspend_state");
            var code = ctx.Request.Query["code"].ToString();
            if (string.IsNullOrEmpty(code)) return Results.Redirect("/login?e=denied");
            var redirect = $"{ctx.Request.Scheme}://{ctx.Request.Host}/signin-github";
            var (email, name, picture, err) = await gh.ExchangeAsync(code, redirect, ct);
            if (string.IsNullOrEmpty(email)) return Results.Redirect("/login?e=auth");
            var user = await s.UpsertUserAsync(email, name, picture, "github", ct);
            CurrentUser.Write(ctx, sessions.Create(user.Id, config.SessionDays, ctx.Request.Headers.UserAgent.ToString(), ctx.Connection.RemoteIpAddress?.ToString()), config.SessionDays, ctx.Request.IsHttps);
            return Results.Redirect("/");
        });

        // вход по коду доступа к аккаунту
        app.MapPost("/login-code", async (HttpContext ctx, SpendStore s, CancellationToken ct) =>
        {
            if (!config.IsServer) return Results.Redirect("/");
            var form = await ctx.Request.ReadFormAsync(ct);
            var user = await s.FindByLoginCodeAsync(form["code"].ToString(), ct);
            if (user is null) return Results.Redirect("/login?e=code");
            CurrentUser.Write(ctx, sessions.Create(user.Id, config.SessionDays, ctx.Request.Headers.UserAgent.ToString(), ctx.Connection.RemoteIpAddress?.ToString()), config.SessionDays, ctx.Request.IsHttps);
            return Results.Redirect("/");
        });

        // Сборщик: обмен кода подключения на доступ к серверу — без аккаунта, пароля и второго окна
        app.MapPost("/link", async (HttpContext ctx, LinkStore store, CancellationToken ct) =>
        {
            var form = await ctx.Request.ReadFormAsync(ct);
            var code = form["code"].ToString().Trim();
            var decoded = InviteCode.Decode(code);
            if (decoded is null) return Results.Redirect("/login?e=bad");

            string body;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
                var payload = JsonSerializer.Serialize(new { code, name = Environment.MachineName, machineId = LinkStore.MachineId() });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var resp = await http.PostAsync(decoded.Value.Url + "/api/devices/redeem", content, ct);
                body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) return Results.Redirect("/login?e=reject");
            }
            catch { return Results.Redirect("/login?e=net"); }

            try
            {
                using var doc = JsonDocument.Parse(body);
                var token = doc.RootElement.GetProperty("token").GetString();
                var deviceId = doc.RootElement.TryGetProperty("deviceId", out var d) ? d.GetString() : null;
                store.Save(new Link
                {
                    ServerUrl = decoded.Value.Url,
                    Token = token,
                    DeviceId = deviceId,
                    Email = doc.RootElement.TryGetProperty("email", out var em) ? em.GetString() : null,
                    Name = doc.RootElement.TryGetProperty("name", out var nm) ? nm.GetString() : null,
                    Picture = doc.RootElement.TryGetProperty("picture", out var pc) ? pc.GetString() : null,
                });
                return Results.Redirect("/");
            }
            catch { return Results.Redirect("/login?e=bad"); }
        });

        // Локальная страница-заглушка: показывается вместо технических страниц, ничего не перекрывает
        app.MapGet("/wait", (HttpContext ctx) =>
        {
            var text = ctx.Request.Query["t"].ToString();
            var login = ctx.Request.Query["login"].ToString() == "1";
            var html = WaitPage
                .Replace("WAIT_TEXT", System.Net.WebUtility.HtmlEncode(text))
                .Replace("LOGIN_BLOCK", login ? "<a class=\"b\" href=\"/opencode\">Войти в opencode</a>" : "");
            return Results.Content(html, "text/html; charset=utf-8");
        });

        // Сборщик: вход в аккаунт opencode.
        app.MapGet("/opencode", (HttpContext ctx) =>
        {
            // opencode переехал на консоль: вход теперь через неё, там же выдаётся сессия консоли
            return Results.Redirect("https://opencode.ai/console/login");
        });

        // Выход. На сервере — отзываем сессию и отключаем компьютеры. В приложении — отвязываемся от сервера.
        app.MapGet("/auth/logout", (HttpContext ctx, DeviceService d, LinkStore links) =>
        {
            if (config.IsServer)
            {
                var uid = CurrentUser.Uid(ctx);
                sessions.Delete(CurrentUser.Read(ctx));
                if (uid is not null) foreach (var dev in d.List(uid)) d.Revoke(uid, dev.Id);
                CurrentUser.Clear(ctx);
                return Results.Redirect("/login");
            }
            links.Clear();
            CurrentUser.Clear(ctx);
            return Results.Redirect("/");
        });

        // Выход из аккаунта opencode на компьютере: забываем сессию opencode и заново показываем вход
        app.MapGet("/auth/opencode-logout", (HttpContext ctx) =>
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config", "opencode-spend", "ocspend-capture.json");
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
            // не редиректим сразу: приложение должно увидеть этот адрес и очистить cookie opencode
            return Results.Content("""
                <!doctype html><html lang="ru"><head><meta charset="utf-8">
                <meta http-equiv="refresh" content="1;url=/opencode">
                <style>:root{color-scheme:dark}body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0b0e14;color:#8b93a7;font:15px system-ui}</style>
                </head><body>Выхожу из аккаунта opencode…</body></html>
                """, "text/html; charset=utf-8");
        });

        app.MapGet("/auth/unlink", (LinkStore links) => { links.Clear(); return Results.Redirect("/"); });


        // ---------- устройства: подключение компьютеров ----------

        app.MapGet("/api/devices", (HttpContext ctx, DeviceService d) =>
        {
            var uid = CurrentUser.Uid(ctx) ?? CurrentUser.LocalId;
            var list = d.List(uid).Select(x => new { id = x.Id, name = x.Name, lastSeen = x.LastSeen, created = x.Created });
            return Results.Ok(new { devices = list, serverMode = config.IsServer });
        });

        // пригласительный код: в нём зашифрован адрес этого сервера
        app.MapPost("/api/devices/pairing", (HttpContext ctx, DeviceService d) =>
        {
            var uid = CurrentUser.Uid(ctx);
            if (uid is null) return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var (code, expires) = d.CreatePairing(uid, baseUrl);
            return Results.Ok(new { code, expiresAt = expires, ttlMinutes = config.PairingTtlMinutes, serverUrl = baseUrl });
        });

        // обмен кода на токен устройства: приложение показывает локальную панель, авторизация ему не нужна
        app.MapPost("/api/devices/redeem", async (HttpContext ctx, DeviceService d, SpendStore s, CancellationToken ct) =>
        {
            var dto = await ReadJsonAsync<ClaimDto>(ctx, ct);
            var claimed = d.Claim(dto?.Code ?? "", dto?.Name, dto?.MachineId);
            if (claimed is null) return Results.Json(new { error = "код неверный или истёк" }, statusCode: 400);
            var owner = await s.GetUserAsync(claimed.Value.UserId, ct);
            return Results.Ok(new
            {
                token = claimed.Value.Token,
                deviceId = claimed.Value.DeviceId,
                email = owner?.Email,
                name = owner?.Name,
                picture = owner?.Picture,
            });
        });

        // аккаунт владельца этого устройства: приложение показывает аватар и имя
        app.MapGet("/api/devices/me", async (HttpContext ctx, DeviceService devices, SpendStore s, CancellationToken ct) =>
        {
            var who = DeviceOf(ctx, devices);
            if (who is null) return Results.Json(new { error = "invalid device token" }, statusCode: 401);
            var u = await s.GetUserAsync(who.Value.uid, ct);
            return Results.Ok(new { email = u?.Email, name = u?.Name, picture = u?.Picture });
        });

        app.MapDelete("/api/devices/{id}", (HttpContext ctx, string id, DeviceService d) =>
        {
            var uid = CurrentUser.Uid(ctx);
            if (uid is null) return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            return Results.Ok(new { ok = d.Revoke(uid, id) });
        });

        // ---------- аккаунт: код доступа для входа с другого устройства ----------

        app.MapPost("/api/account/code", async (HttpContext ctx, SpendStore s, CancellationToken ct) =>
        {
            if (!config.IsServer) return Results.NotFound();
            var uid = CurrentUser.Uid(ctx);
            if (uid is null) return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            var code = await s.NewLoginCodeAsync(uid, ct);
            return Results.Ok(new { code });
        });

        // ---------- управление: активные сессии и остановка ----------

        app.MapGet("/api/control/sessions", async (HttpContext ctx, OpencodeControl ctl, ControlState state, CancellationToken ct) =>
        {
            if (config.IsServer)
            {
                var uid = CurrentUser.Uid(ctx) ?? CurrentUser.LocalId;
                return Results.Ok(new { available = true, relayed = true, raw = state.GetSessionsRaw(uid) });
            }
            var (ok, tree, active, recent, err) = await ctl.SessionsAsync(ct);
            return Results.Ok(new { available = ok, relayed = false, tree, active, recent, reason = err, port = ctl.Port });
        });

        app.MapPost("/api/control/sessions/{id}/stop", async (HttpContext ctx, string id, OpencodeControl ctl, ControlState state, CancellationToken ct) =>
        {
            if (config.IsServer)
            {
                var uid = CurrentUser.Uid(ctx) ?? CurrentUser.LocalId;
                state.EnqueueStop(uid, id);
                return Results.Ok(new { ok = true, queued = true });
            }
            var (ok, stopped, err) = await ctl.StopAsync(id, ct);
            return Results.Ok(new { ok, queued = false, stopped, error = err });
        });

        app.MapGet("/api/control/status", async (HttpContext ctx, OpencodeControl ctl, ControlState state, CancellationToken ct) =>
        {
            if (config.IsServer)
            {
                var uid = CurrentUser.Uid(ctx) ?? CurrentUser.LocalId;
                var n = CountActive(state.GetSessionsRaw(uid));
                return Results.Ok(new { available = true, relayed = true, active = n, port = (int?)null });
            }
            var (ok, _, active, _, err) = await ctl.SessionsAsync(ct);
            return Results.Ok(new { available = ok, reason = err, active, port = ctl.Port, checkedAt = ctl.CheckedAt });
        });

        // поток обновлений для браузера: сообщаем, когда меняется снимок сессий
        app.MapGet("/api/events", async (HttpContext ctx, ControlState state, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var uid = config.IsServer ? (CurrentUser.Uid(ctx) ?? CurrentUser.LocalId) : CurrentUser.LocalId;
            var last = "";
            var beat = DateTimeOffset.UtcNow;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var snap = state.GetSessionsRaw(uid);
                    if (snap != last)
                    {
                        last = snap;
                        await ctx.Response.WriteAsync("data: {\"changed\":true}\n\n", ct);
                        await ctx.Response.Body.FlushAsync(ct);
                        beat = DateTimeOffset.UtcNow;
                    }
                    else if ((DateTimeOffset.UtcNow - beat).TotalSeconds > 15)
                    {
                        await ctx.Response.WriteAsync(": ping\n\n", ct);
                        await ctx.Response.Body.FlushAsync(ct);
                        beat = DateTimeOffset.UtcNow;
                    }
                    await Task.Delay(300, ct);
                }
            }
            catch { }
        });

        app.MapPost("/api/control/stop-all", async (HttpContext ctx, OpencodeControl ctl, ControlState state, CancellationToken ct) =>
        {
            if (config.IsServer)
            {
                var uid = CurrentUser.Uid(ctx) ?? CurrentUser.LocalId;
                var ids = ActiveIds(state.GetSessionsRaw(uid));
                foreach (var id in ids) state.EnqueueStop(uid, id);
                return Results.Ok(new { ok = true, queued = true, aborted = ids.Count });
            }
            var (ok, aborted, err) = await ctl.StopAllAsync(ct);
            return Results.Ok(new { ok, aborted, error = err ?? ctl.LastError });
        });

        // ---------- единый канал: данные и команды одним запросом (по токену устройства) ----------

        app.MapPost("/api/device/sync", async (HttpContext ctx, SpendStore store, ProfileStore ps, ControlState state, DeviceService devices, CancellationToken ct) =>
        {
            var who = DeviceOf(ctx, devices);
            if (who is null) return Results.Json(new { error = "invalid device token" }, statusCode: 401);
            var dto = await ReadJsonAsync<DeviceSyncDto>(ctx, ct);
            if (dto is null) return Results.BadRequest();
            var uid = who.Value.uid;

            var n1 = await store.UpsertUsageAsync(dto.Usage ?? new(), uid, ct);
            var n2 = await store.UpsertSessionsAsync(dto.Sessions ?? new(), uid, ct);
            if (!string.IsNullOrWhiteSpace(dto.Device))
                await store.TouchDeviceAsync(who.Value.deviceId, uid, dto.Device, ct);

            var n3 = 0;
            if (dto.Site is { Count: > 0 })
            {
                var rows = dto.Site.Select(d =>
                {
                    var id = string.IsNullOrWhiteSpace(d.Id)
                        ? "dom_" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                            System.Text.Encoding.UTF8.GetBytes($"{d.Time:O}|{d.Model}|{d.Provider}|{d.Input}|{d.Output}|{d.Reasoning}|{d.CacheRead}|{d.Cost}|{d.SessionId}"))).ToLowerInvariant()
                        : d.Id!;
                    return new SiteUsage(id, d.Time ?? DateTimeOffset.UtcNow, d.Model, d.Provider, null,
                        d.Input, d.Output, d.Reasoning, d.CacheRead, d.Cost, d.SessionId, null);
                }).ToList();
                n3 = await ps.UpsertUsageAsync(rows, uid, ct);
            }

            var profileOk = false;
            if (dto.Profile is not null)
            {
                var p = dto.Profile;
                if (p.Limits is not null) await ps.SaveProfileAsync(p.Limits, uid, ct);
                if (p.Usage is { Count: > 0 })
                {
                    var rows = p.Usage.Select(d => new SiteUsage(
                        d.Id ?? Guid.NewGuid().ToString("N"), d.Time ?? DateTimeOffset.UtcNow, d.Model, d.Provider, null,
                        d.Input, d.Output, d.Reasoning, d.CacheRead, d.Cost, d.SessionId, null)).ToList();
                    await ps.UpsertUsageAsync(rows, uid, ct);
                }
                if (p.Payments is { Count: > 0 } || !string.IsNullOrWhiteSpace(p.LiteSubId))
                    await ps.SavePaymentsAsync(p.Payments ?? new(), p.LiteSubId, uid, ct);
                profileOk = true;
            }

            if (dto.Control is not null)
                state.SetSessions(uid, dto.Control.Sessions ?? new List<object>(), dto.Control.Device);

            var ids = state.TakePending(uid);
            var owner = await store.GetUserAsync(uid, ct);

            return Results.Ok(new
            {
                ok = true,
                usage = n1,
                sessions = n2,
                site = n3,
                profile = profileOk,
                commands = ids.Select(id => new { type = "stop", id }),
                devices = devices.List(uid).Select(x => new { id = x.Id, name = x.Name, lastSeen = x.LastSeen }),
                account = new { email = owner?.Email, name = owner?.Name, picture = owner?.Picture },
            });
        });

        // отключить компьютер из того же аккаунта (по токену устройства)
        app.MapPost("/api/device/revoke", async (HttpContext ctx, DeviceService devices, CancellationToken ct) =>
        {
            var who = DeviceOf(ctx, devices);
            if (who is null) return Results.Json(new { error = "invalid device token" }, statusCode: 401);
            var dto = await ReadJsonAsync<RevokeDto>(ctx, ct);
            if (dto is null || string.IsNullOrWhiteSpace(dto.Id)) return Results.BadRequest();
            return Results.Ok(new { ok = devices.Revoke(who.Value.uid, dto.Id!) });
        });

        app.MapGet("/api/me", async (HttpContext ctx, SpendStore s, ProfileStore ps, DeviceService d, CancellationToken ct) =>
        {
            // СЕРВЕР: настоящий аккаунт; данные принадлежат пользователю, ПК привязаны к нему
            if (config.IsServer)
            {
                var uid = CurrentUser.Uid(ctx);
                var user = uid is null ? null : await s.GetUserAsync(uid, ct);
                var devs = uid is null ? new List<Device>() : d.List(uid);
                return Results.Ok(new
                {
                    authenticated = user is not null,
                    mode = "server",
                    linked = devs.Count > 0,
                    hasCode = uid is not null && await s.HasLoginCodeAsync(uid, ct),
                    account = user is null ? null : new { email = user.Email, name = user.Name ?? user.Email, picture = user.Picture },
                    devices = devs.Select(x => new { id = x.Id, name = x.Name, lastSeen = x.LastSeen }),
                });
            }

            // ПРИЛОЖЕНИЕ: аккаунт = вход в opencode; «Выйти» разлогинивает opencode
            var limits = await ps.GetProfileAsync(null, ct);
            var link = links.Get();
            if (link is not null) link = await FillAccountAsync(link, links, ct);
            var acc = link is not null
                ? new { email = link.Email, name = link.Name ?? link.Email, picture = link.Picture }
                : (limits?.Email is { Length: > 0 } mail
                    ? new { email = (string?)mail, name = (string?)mail, picture = (string?)null }
                    : null);
            var (srvDevices, srvSelf) = ReadServerDevices();
            return Results.Ok(new
            {
                authenticated = false,
                mode = "collector",
                linked = link is not null,
                serverUrl = link?.ServerUrl,
                devices = srvDevices,
                selfDeviceId = srvSelf,
                account = acc,
            });
        });

        // локальный прокси отключения чужого ПК (только сборщик)
        app.MapPost("/api/server/revoke", async (HttpContext ctx, LinkStore links, CancellationToken ct) =>
        {
            if (config.IsServer) return Results.NotFound();
            var link = links.Get();
            if (link is null) return Results.BadRequest();
            var dto = await ReadJsonAsync<RevokeDto>(ctx, ct);
            if (dto is null || string.IsNullOrWhiteSpace(dto.Id)) return Results.BadRequest();
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var req = new HttpRequestMessage(HttpMethod.Post, link.ServerUrl!.TrimEnd('/') + "/api/device/revoke");
                req.Headers.TryAddWithoutValidation("X-Device-Token", link.Token);
                req.Content = new StringContent(JsonSerializer.Serialize(new { id = dto.Id }), Encoding.UTF8, "application/json");
                using var resp = await http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                return Results.Content(body, "application/json", statusCode: (int)resp.StatusCode);
            }
            catch { return Results.Json(new { error = "net" }, statusCode: 502); }
        });

        var files = new ManifestEmbeddedFileProvider(typeof(Server).Assembly, "wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });

        app.MapGet("/api/health", async (SpendStore s, SyncService sync, SpendConfig cfg, CancellationToken ct) =>
        {
            // сборщик базы не имеет — проверять нечего
            if (cfg.SkipLocalDb)
                return Results.Ok(new { dbOk = true, dbError = (string?)null, lastSync = (DateTimeOffset?)null, syncError = (string?)null, mode = "collector", tz = cfg.TimeZone });

            var ok = true;
            string? err = null;
            try { await s.TotalsAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1), null, ct); }
            catch (Exception e) { ok = false; err = e.Message; }
            return Results.Ok(new { dbOk = ok, dbError = err, lastSync = sync.LastSync, syncError = sync.LastError, mode = cfg.IsServer ? "server" : "collector", tz = cfg.TimeZone });
        });

        app.MapGet("/api/summary", async (HttpContext ctx, string? range, SpendStore s, Budgets b, SyncService sync, SpendConfig cfg, CancellationToken ct) =>
        {
            var uid = Scope(ctx);
            var tz = ResolveTz(cfg.TimeZone);
            var now = DateTimeOffset.UtcNow;
            var (label, from) = ResolveRange(range, now);

            var localNow = TimeZoneInfo.ConvertTime(now, tz);
            var dayStart = new DateTimeOffset(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0, tz.GetUtcOffset(localNow)).ToUniversalTime();
            var monday = localNow.Date.AddDays(-(((int)localNow.DayOfWeek + 6) % 7));
            var weekStart = new DateTimeOffset(monday, tz.GetUtcOffset(localNow)).ToUniversalTime();
            var monthStart = new DateTimeOffset(new DateTime(localNow.Year, localNow.Month, 1), tz.GetUtcOffset(localNow)).ToUniversalTime();
            var end = now.AddDays(1);

            var todayT = await s.TotalsAsync(dayStart, end, uid, ct);
            var weekT = await s.TotalsAsync(weekStart, end, uid, ct);
            var monthT = await s.TotalsAsync(monthStart, end, uid, ct);
            var allT = await s.TotalsAsync(DateTimeOffset.UnixEpoch, end, uid, ct);
            var rangeT = await s.TotalsAsync(from, end, uid, ct);

            var byModel = await s.ByModelAsync(from, end, uid, ct);
            var series = await s.SeriesAsync(from, cfg.TimeZone, uid, ct);
            var sessions = await s.SessionsAsync(from, 200, uid, ct);
            var budgets = await b.BuildAsync(s, tz, uid, ct);
            var (root, subs) = await s.LiveAsync(uid, ct);

            return Results.Ok(new
            {
                generatedAt = now,
                tz = cfg.TimeZone,
                lastSync = sync.LastSync,
                syncError = sync.LastError,
                dbOk = sync.LastError is null,
                range = new { label, from },
                periods = new { today = todayT, week = weekT, month = monthT, all = allT },
                totals = rangeT,
                byModel,
                series,
                sessions,
                budgets,
                live = new { root, subs, total = subs.Sum(x => x.Cost) + (root?.Cost ?? 0) },
            });
        });

        app.MapGet("/api/live", async (HttpContext ctx, SpendStore s, SyncService sync, CancellationToken ct) =>
        {
            var (root, subs) = await s.LiveAsync(Scope(ctx), ct);
            return Results.Ok(new { lastSync = sync.LastSync, syncError = sync.LastError, root, subs, total = subs.Sum(x => x.Cost) + (root?.Cost ?? 0) });
        });

        app.MapPost("/api/sync", async (SyncService sync, CancellationToken ct) =>
        {
            var (usage, sessions) = await sync.SyncAsync(ct);
            return Results.Ok(new { usage, sessions, lastSync = sync.LastSync, error = sync.LastError });
        });

        app.MapGet("/api/settings", async (HttpContext ctx, SpendStore s, CancellationToken ct) =>
        {
            var uid = Scope(ctx);
            var key = await s.GetStateAsync("zen_key", uid, ct) ?? "";
            var scope = await s.GetStateAsync("zen_scope", uid, ct) ?? "organization";
            var email = await s.GetStateAsync("zen_email", uid, ct) ?? "";
            var cookie = await s.GetStateAsync("profile_cookie", uid, ct) ?? "";
            return Results.Ok(new
            {
                hasZenKey = key.Length > 0,
                zenKeyMasked = key.Length > 10 ? key[..6] + "…" + key[^4..] : (key.Length > 0 ? "••••••" : ""),
                zenScope = scope,
                zenEmail = email,
                hasProfileCookie = cookie.Length > 0,
            });
        });

        app.MapPost("/api/settings", async (HttpContext ctx, SpendStore s, SpendConfig cfg, CancellationToken ct) =>
        {
            var uid = Scope(ctx);
            var dto = await ReadJsonAsync<SettingsDto>(ctx, ct);
            if (dto is null) return Results.BadRequest();
            if (dto.ZenKey is not null)
            {
                var k = dto.ZenKey.Trim();
                await s.SetStateAsync("zen_key", k, uid, ct);
                if (!config.IsServer) cfg.ZenServiceKey = k.Length > 0 ? k : null;
            }
            if (dto.ZenScope is not null) await s.SetStateAsync("zen_scope", dto.ZenScope, uid, ct);
            if (dto.ZenEmail is not null) await s.SetStateAsync("zen_email", dto.ZenEmail, uid, ct);
            if (dto.ProfileCookie is not null)
            {
                var c = dto.ProfileCookie.Trim();
                await s.SetStateAsync("profile_cookie", c, uid, ct);
                if (!config.IsServer) cfg.ProfileCookie = c.Length > 0 ? c : null;
            }
            if (!string.IsNullOrWhiteSpace(dto.ZenWorkspace)) cfg.ZenWorkspace = dto.ZenWorkspace.Trim();
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/zen", async (HttpContext ctx, ZenStore z, CancellationToken ct) => Results.Ok(await z.GetAsync(Scope(ctx), ct)));

        app.MapPost("/api/zen/sync", async (HttpContext ctx, SpendStore s, ZenStore z, ZenClient c, CancellationToken ct) =>
        {
            var uid = Scope(ctx);
            var scope = await s.GetStateAsync("zen_scope", uid, ct) ?? "organization";
            var email = await s.GetStateAsync("zen_email", uid, ct);
            var results = new List<object>();
            foreach (var range in ZenClient.Ranges)
            {
                var (ok, err, rows) = await c.FetchAsync(range, scope, email, ct);
                if (ok) await z.ReplaceAsync(range, scope, rows, uid, ct);
                results.Add(new { range, ok, error = err, count = rows.Count });
            }
            return Results.Ok(new { results });
        });

        // выгрузка локальных записей opencode.db для отправки на сервер
        app.MapGet("/api/export/usage", (OpencodeReader reader, long? sinceMs, CancellationToken ct) =>
        {
            if (!reader.Available) return Results.Ok(new { usage = new List<UsageRecord>(), sessions = new List<SessionRecord>() });
            return Results.Ok(new
            {
                usage = reader.ReadUsage(sinceMs ?? 0),
                sessions = reader.ReadSessions(sinceMs ?? 0),
            });
        });

        app.MapGet("/api/export/profile", async (ProfileStore ps, CancellationToken ct) => Results.Ok(new ProfilePushDto
        {
            Limits = await ps.GetProfileAsync(null, ct),
            Usage = await ps.ExportSinceAsync(DateTimeOffset.UnixEpoch, null, ct),
            Payments = await ps.PaymentRecordsAsync(null, ct),
            LiteSubId = await ps.LiteSubscriptionIdAsync(null, ct),
        }));

        // выгрузка истории профиля для отправки на сервер
        app.MapGet("/api/export/site-usage", async (HttpContext ctx, ProfileStore ps, long? sinceMs, CancellationToken ct) =>
        {
            var from = sinceMs is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(sinceMs.Value) : DateTimeOffset.UnixEpoch;
            return Results.Ok(await ps.ExportSinceAsync(from, Scope(ctx), ct));
        });

        app.MapGet("/api/profile", async (HttpContext ctx, SpendStore s, ProfileStore ps, SpendConfig cfg, CancellationToken ct) =>
        {
            var uid = Scope(ctx);
            var tz = ResolveTz(cfg.TimeZone);
            var now = DateTimeOffset.UtcNow;
            var localNow = TimeZoneInfo.ConvertTime(now, tz);
            var dayStart = new DateTimeOffset(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0, tz.GetUtcOffset(localNow)).ToUniversalTime();
            var monday = localNow.Date.AddDays(-(((int)localNow.DayOfWeek + 6) % 7));
            var weekStart = new DateTimeOffset(monday, tz.GetUtcOffset(localNow)).ToUniversalTime();
            var monthStart = new DateTimeOffset(new DateTime(localNow.Year, localNow.Month, 1), tz.GetUtcOffset(localNow)).ToUniversalTime();
            var per = await ps.PeriodAsync(dayStart, weekStart, monthStart, uid, ct);
            var summary = await ps.SummaryAsync(cfg.TimeZone, uid, ct);
            var limits = await ps.GetProfileAsync(uid, ct);

            return Results.Ok(new
            {
                hasData = limits is not null,
                workspace = cfg.ZenWorkspace,
                limits,
                periods = new { today = per.today, week = per.week, month = per.month, todayCount = per.todayCount, totalCount = per.totalCount },
                bySession = await ps.BySessionAsync(100, uid, ct),
                payments = await ps.PaymentsAsync(uid, ct),
                liteSubscriptionId = await ps.LiteSubscriptionIdAsync(uid, ct),
                summary,
            });
        });

        return app;
    }

    private static DateTimeOffset _accountTried = DateTimeOffset.MinValue;

    /// <summary>Дополняет привязку данными аккаунта с сервера (для старых привязок).</summary>
    private static async Task<Link> FillAccountAsync(Link link, LinkStore store, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _accountTried < TimeSpan.FromSeconds(30)) return link;
        _accountTried = DateTimeOffset.UtcNow;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            using var req = new HttpRequestMessage(HttpMethod.Get, link.ServerUrl!.TrimEnd('/') + "/api/devices/me");
            req.Headers.TryAddWithoutValidation("X-Device-Token", link.Token);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return link;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            link.Email = doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
            link.Name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            link.Picture = doc.RootElement.TryGetProperty("picture", out var p) ? p.GetString() : null;
            store.Save(link);
        }
        catch { }
        return link;
    }

    private static string LinkError(string? e) => e switch
    {
        "bad" => "<div class=\"err\">Код не распознан — скопируйте его целиком</div>",
        "reject" => "<div class=\"err\">Сервер отклонил код: он истёк или уже использован</div>",
        "net" => "<div class=\"err\">Не удалось связаться с сервером</div>",
        _ => "<div class=\"err\">Не удалось подключиться, попробуйте снова</div>",
    };

    private static string LoginError(string? e) => e switch
    {
        "state" => "<div class=\"err\">Сессия входа устарела — попробуйте ещё раз</div>",
        "denied" => "<div class=\"err\">Вход отменён</div>",
        "auth" => "<div class=\"err\">Не удалось получить аккаунт у провайдера</div>",
        "code" => "<div class=\"err\">Код доступа не подошёл</div>",
        _ => "",
    };

    private const string WaitPage = """
        <!doctype html><html lang="ru"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>OpenCode Spend</title>
        <style>
        :root{color-scheme:dark}
        body{margin:0;min-height:100vh;display:grid;place-items:center;background:radial-gradient(900px 500px at 20% -10%,#16203a,transparent 60%),#0b0e14;color:#e7ebf3;font:15px/1.5 "Segoe UI",system-ui,sans-serif}
        .box{text-align:center;max-width:520px;padding:24px}
        h1{margin:0 0 10px;font-size:24px}
        p{color:#8b93a7;font-size:14px;margin:0}
        a.b{display:inline-block;margin-top:24px;padding:13px 26px;border-radius:10px;background:#4f8cff;color:#fff;text-decoration:none;font-weight:600}
        a.b:hover{filter:brightness(1.08)}
        </style></head><body>
        <div class="box">
          <h1>OpenCode Spend</h1>
          <p>WAIT_TEXT</p>
          LOGIN_BLOCK
        </div></body></html>
        """;

    /// <summary>Стартовый экран сервера: код, которым компьютер подключается к этой сессии.</summary>
    private const string PairPage = """
        <!doctype html><html lang="ru"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>OpenCode Spend — подключение</title>
        <style>
        :root{color-scheme:dark}
        body{margin:0;min-height:100vh;display:grid;place-items:center;background:radial-gradient(900px 500px at 20% -10%,#16203a,transparent 60%),#0b0e14;color:#e7ebf3;font:15px/1.5 "Segoe UI",system-ui,sans-serif}
        .box{width:min(94vw,620px);background:#131824;border:1px solid #212a3b;border-radius:16px;padding:30px;box-shadow:0 20px 50px -30px #000;text-align:center}
        h1{margin:0 0 8px;font-size:22px}
        p{color:#8b93a7;font-size:13px;margin:0 0 22px}
        .code{font:600 13px/1.6 Consolas,monospace;color:#cfe0ff;background:#1b2740;border:1px solid #2f4370;border-radius:12px;padding:16px;word-break:break-all;user-select:all;min-height:56px}
        .row{display:flex;gap:12px;align-items:center;justify-content:center;margin-top:14px;flex-wrap:wrap}
        button{padding:11px 16px;border:0;border-radius:10px;background:#4f8cff;color:#fff;font:inherit;font-weight:600;cursor:pointer}
        .hint{color:#5f6880;font-size:12px;margin-top:10px}
        .ttl{margin-top:8px;color:#cfe0ff;font:600 13px/1.5 Consolas,monospace}
        .ttl-bar{height:6px;border-radius:999px;background:#1b2230;overflow:hidden;margin-top:10px}
        .ttl-bar i{display:block;height:100%;width:100%;background:#35c88a;transition:width .95s linear,background .3s}
        .ttl-bar.warn i{background:#f5a524}
        .ttl-bar.bad i{background:#f2555a}
        .wait{margin-top:18px;color:#8b93a7;font-size:13px}
        .b{display:inline-block;margin-top:10px;padding:11px 20px;border-radius:10px;background:#4f8cff;color:#fff;text-decoration:none;font-weight:600}
        .b:hover{filter:brightness(1.08)}
        </style></head><body>
        <div class="box">
          <h1>Подключить ПК</h1>
          <p>Скопируйте код и вставьте его в приложении OpenCode Spend на компьютере, где стоит opencode.
             В коде зашит адрес этого сервера, вводить его не нужно.</p>
          <div class="code" id="code">получаю код…</div>
          <div class="row"><button id="copy">Скопировать код</button><button id="again">Новый код</button></div>
          <div class="hint">Код действует TTLMIN и сгорает после подключения.</div>
          <div class="ttl-bar"><i id="ttlFill"></i></div>
          <div class="ttl" id="ttl">—</div>
          <div class="wait" id="wait"></div>
        </div>
        <script>
          var current = "", expiresAt = 0, ttlMs = 0;
          async function gen() {
            try {
              const r = await fetch("/api/devices/pairing", { method: "POST" }).then(function (x) { return x.json(); });
              current = r.code || "";
              expiresAt = r.expiresAt ? new Date(r.expiresAt).getTime() : 0;
              ttlMs = r.expiresAt ? new Date(r.expiresAt).getTime() - Date.now() : 0;
              document.getElementById("code").textContent = current || "не удалось получить код";
              tick();
            } catch (e) { document.getElementById("code").textContent = "ошибка: " + e.message; }
          }
          function tick() {
            var left = Math.max(0, expiresAt - Date.now());
            var share = ttlMs > 0 ? Math.max(0, Math.min(100, left / ttlMs * 100)) : 0;
            var fill = document.getElementById("ttlFill");
            var bar = document.querySelector(".ttl-bar");
            if (fill) fill.style.width = share + "%";
            if (bar) bar.className = "ttl-bar" + (share <= 15 ? " bad" : share <= 40 ? " warn" : "");
            var el = document.getElementById("ttl");
            if (el) el.textContent = "новый код через " + Math.round(left / 1000) + " с";
            if (left <= 0) { expiresAt = 0; gen(); }
          }
          async function poll() {
            try {
              const d = await fetch("/api/devices").then(function (x) { return x.json(); });
              if (d.devices && d.devices.length) { document.getElementById("wait").textContent = "Компьютер подключён, открываю панель…"; location.reload(); }
            } catch (e) { }
          }
          document.getElementById("again").onclick = gen;
          document.getElementById("copy").onclick = function () {
            navigator.clipboard.writeText(current).then(function () { document.getElementById("wait").textContent = "Код скопирован"; });
          };
          gen();
          setInterval(tick, 1000);
          setInterval(poll, 2000);
        </script></body></html>
        """;

    private const string LinkPage = """
        <!doctype html><html lang="ru"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Подключение — OpenCode Spend</title>
        <style>
        :root{color-scheme:dark}
        body{margin:0;min-height:100vh;display:grid;place-items:center;background:radial-gradient(900px 500px at 20% -10%,#16203a,transparent 60%),#0b0e14;color:#e7ebf3;font:15px/1.5 "Segoe UI",system-ui,sans-serif}
        .box{width:min(92vw,440px);background:#131824;border:1px solid #212a3b;border-radius:16px;padding:28px;box-shadow:0 20px 50px -30px #000;text-align:center}
        h1{margin:0 0 6px;font-size:20px}
        p{color:#8b93a7;font-size:13px;margin:0 0 20px}
        textarea{width:100%;box-sizing:border-box;padding:12px;border-radius:10px;border:1px solid #2a3548;background:#0f131c;color:#cfe0ff;font:13px/1.5 Consolas,monospace;resize:vertical}
        textarea:focus{outline:none;border-color:#4f8cff}
        button{width:100%;margin-top:14px;padding:12px;border:0;border-radius:10px;background:#4f8cff;color:#fff;font:inherit;font-weight:600;cursor:pointer}
        button:hover{filter:brightness(1.06)}
        .err{color:#f2555a;font-size:13px;margin-top:12px}
        .back{display:inline-block;margin-top:18px;color:#8b93a7;text-decoration:none;font-size:13px}
        .back:hover{color:#e7ebf3}
        </style></head><body>
        <div class="box">
          <h1>OpenCode Spend</h1>
          <p>Вставьте код подключения из веб-панели сервера<br>раздел «Добавить ПК» → «Получить код»</p>
          <form method="post" action="/link">
            <textarea name="code" rows="4" autofocus placeholder="OCSP1.…"></textarea>
            <button type="submit">Подключить</button>
          </form>
          ERRPLACEHOLDER
          <a class="back" href="/">← Назад</a>
        </div></body></html>
        """;

    /// <summary>Одноразовый state для OAuth: кладём в cookie, сверяем на возврате.</summary>
    private static string NewState(HttpContext ctx)
    {
        var state = Guid.NewGuid().ToString("N");
        ctx.Response.Cookies.Append("ocspend_state", state, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            MaxAge = TimeSpan.FromMinutes(10),
        });
        return state;
    }

    /// <summary>Чтение JSON-тела: битый или не тот формат — не 500, а null (400).</summary>
    private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx, CancellationToken ct) where T : class
    {
        try { return await ctx.Request.ReadFromJsonAsync<T>(cancellationToken: ct); }
        catch { return null; }
    }

    /// <summary>Компьютеры аккаунта, сохранённые приложением после синхронизации. Битый файл — пусто.</summary>
    private static (JsonElement? Devices, string? Self) ReadServerDevices()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenCodeSpend", "server-devices.json");
            if (!File.Exists(path)) return (null, null);
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var devices = doc.RootElement.TryGetProperty("devices", out var d) ? d.Clone() : (JsonElement?)null;
            var self = doc.RootElement.TryGetProperty("self", out var s) ? s.GetString() : null;
            return (devices, self);
        }
        catch { return (null, null); }
    }

    private static (string uid, string deviceId)? DeviceOf(HttpContext ctx, DeviceService devices)
    {
        var r = devices.Resolve(ctx.Request.Headers["X-Device-Token"].ToString());
        if (r is null) return null;
        devices.Touch(r.Value.DeviceId);
        return (r.Value.UserId, r.Value.DeviceId);
    }

    private static int CountActive(string sessionsRaw) => ActiveIds(sessionsRaw).Count;

    /// <summary>Активные сессии из снимка устройства (с учётом вложенности).</summary>
    private static List<string> ActiveIds(string sessionsRaw)
    {
        var ids = new List<string>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(sessionsRaw);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != System.Text.Json.JsonValueKind.Array)
                return ids;
            void Walk(System.Text.Json.JsonElement e)
            {
                if (e.ValueKind != System.Text.Json.JsonValueKind.Object) return;
                if (e.TryGetProperty("active", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.True
                    && e.TryGetProperty("id", out var i) && i.GetString() is { Length: > 0 } id)
                    ids.Add(id);
                if (e.TryGetProperty("children", out var ch) && ch.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var c in ch.EnumerateArray()) Walk(c);
            }
            foreach (var e in items.EnumerateArray()) Walk(e);
        }
        catch { }
        return ids;
    }

    private static TimeZoneInfo ResolveTz(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }

    private static string? TryCaptureCookie()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "opencode-spend", "ocspend-capture.json");
            if (!File.Exists(path)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            // новый формат: одна строка cookie
            if (doc.RootElement.TryGetProperty("cookie", out var one)
                && one.GetString() is { Length: > 0 } single) return single;
            // старый формат: массив cookies
            if (!doc.RootElement.TryGetProperty("cookies", out var cookies)) return null;
            foreach (var c in cookies.EnumerateArray())
            {
                var name = Get(c, "Name", "name");
                var val = Get(c, "Value", "value");
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(val)) return name + "=" + val;
            }
        }
        catch { }
        return null;

        static string? Get(System.Text.Json.JsonElement e, string a, string b)
            => e.TryGetProperty(a, out var v) ? v.GetString() : (e.TryGetProperty(b, out var v2) ? v2.GetString() : null);
    }

    private static (string label, DateTimeOffset from) ResolveRange(string? range, DateTimeOffset now) => range switch
    {
        "24h" => ("24 часа", now.AddHours(-24)),
        "7d" => ("7 дней", now.AddDays(-7)),
        "90d" => ("90 дней", now.AddDays(-90)),
        "all" => ("Всё время", DateTimeOffset.UnixEpoch),
        _ => ("30 дней", now.AddDays(-30)),
    };

    private const string GoogleButton = """
        <a class="p" href="/auth/google">
          <svg width="18" height="18" viewBox="0 0 48 48"><path fill="#EA4335" d="M24 9.5c3.5 0 6.6 1.2 9 3.6l6.7-6.7C35.6 2.6 30.2 0 24 0 14.6 0 6.4 5.4 2.5 13.3l7.8 6.1C12.3 13.2 17.7 9.5 24 9.5z"/><path fill="#4285F4" d="M46.5 24.5c0-1.6-.1-3.1-.4-4.5H24v9h12.7c-.5 2.9-2.2 5.4-4.7 7l7.6 5.9c4.4-4.1 6.9-10.1 6.9-17.4z"/><path fill="#FBBC05" d="M10.3 28.6c-.5-1.5-.8-3-.8-4.6s.3-3.1.8-4.6l-7.8-6.1C.9 16.4 0 20.1 0 24s.9 7.6 2.5 10.7l7.8-6.1z"/><path fill="#34A853" d="M24 48c6.2 0 11.5-2 15.3-5.6l-7.6-5.9c-2.1 1.4-4.8 2.3-7.7 2.3-6.3 0-11.7-3.7-13.7-9.1l-7.8 6.1C6.4 42.6 14.6 48 24 48z"/></svg>
          Продолжить с Google
        </a>
        """;

    private const string GitHubButton = """
        <a class="p gh" href="/auth/github">
          <svg width="18" height="18" viewBox="0 0 496 512"><path fill="currentColor" d="M244.8 8C106.1 8 0 113.3 0 252c0 110.9 69.8 205.8 169.5 239.2 12.8 2.3 17.3-5.6 17.3-12.1 0-6.2-.3-40.4-.3-61.4 0 0-70 15-84.7-29.8 0 0-11.4-29.1-27.8-36.6 0 0-22.9-15.7 1.6-15.4 0 0 24.9 2 38.6 25.8 21.9 38.6 58.6 27.5 72.9 20.9 2.3-16 8.8-27.1 16-33.7-55.9-6.2-112.3-14.3-112.3-110.5 0-27.5 7.6-41.3 23.6-58.9-2.6-6.5-11.1-33.3 2.6-67.9 20.9-6.5 69 27 69 27 20-5.6 41.5-8.5 62.8-8.5s42.8 2.9 62.8 8.5c0 0 48.1-33.6 69-27 13.7 34.7 5.2 61.4 2.6 67.9 16 17.7 25.8 31.5 25.8 58.9 0 96.5-58.9 104.2-114.8 110.5 9.2 7.9 17 22.9 17 46.4 0 33.7-.3 75.4-.3 83.6 0 6.5 4.6 14.4 17.3 12.1C428.2 457.8 496 362.9 496 252 496 113.3 383.5 8 244.8 8z"/></svg>
          Продолжить с GitHub
        </a>
        """;

    private const string LoginProviders = """
        <!doctype html><html lang="ru"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Вход — OpenCode Spend</title>
        <style>
        :root{color-scheme:dark}
        body{margin:0;min-height:100vh;display:grid;place-items:center;background:radial-gradient(900px 500px at 20% -10%,#16203a,transparent 60%),#0b0e14;color:#e7ebf3;font:15px/1.5 "Segoe UI",system-ui,sans-serif}
        .box{width:min(92vw,380px);background:#131824;border:1px solid #212a3b;border-radius:16px;padding:28px;box-shadow:0 20px 50px -30px #000;text-align:center}
        h1{margin:0 0 6px;font-size:20px}
        p{color:#8b93a7;font-size:13px;margin:0 0 22px}
        a.p{display:flex;align-items:center;justify-content:center;gap:10px;padding:12px;border-radius:10px;text-decoration:none;font-weight:600;margin-bottom:10px}
        a.p:hover{filter:brightness(.94)}
        a.p{background:#fff;color:#111}
        a.p.gh{background:#24292f;color:#fff}
        .err{color:#f2555a;font-size:13px;margin-top:14px}
        </style></head><body>
        <div class="box">
          <h1>OpenCode Spend</h1>
          <p>Войдите, чтобы смотреть статистику с любого устройства</p>
          PROVIDERS
          <div style="margin-top:18px;border-top:1px solid #212a3b;padding-top:16px">
            <p style="margin:0 0 10px">Или войдите по коду доступа к аккаунту</p>
            <form method="post" action="/login-code" style="width:100%;background:none;border:0;padding:0;box-shadow:none">
              <input name="code" placeholder="XXXX-XXXX-XXXX-XXXX-XXXX" autocomplete="off"
                     style="width:100%;box-sizing:border-box;padding:11px 13px;border-radius:10px;border:1px solid #2a3548;background:#0f131c;color:#e7ebf3;font:inherit" />
              <button type="submit" style="width:100%;margin-top:12px;padding:11px;border:0;border-radius:10px;background:#4f8cff;color:#fff;font:inherit;font-weight:600;cursor:pointer">Войти по коду</button>
            </form>
          </div>
          ERRPLACEHOLDER
        </div></body></html>
        """;

}
