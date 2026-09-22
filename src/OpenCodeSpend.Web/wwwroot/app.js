(() => {
  const $ = (s) => document.querySelector(s);
  const state = { range: "30d", mode: "paid", charts: {}, timer: null, local: null, profile: null, expanded: new Set(), linked: false, serverUrl: "", pairTimer: null, account: "", devices: [], selfDeviceId: "" };

  const PALETTE = ["#4f8cff", "#35c88a", "#f5a524", "#a78bfa", "#f2555a", "#22d3ee",
                   "#f472b6", "#84cc16", "#fb923c", "#60a5fa", "#e879f9", "#14b8a6"];
  const colorFor = (() => {
    const map = new Map();
    let i = 0;
    return (key) => {
      if (!map.has(key)) map.set(key, PALETTE[i++ % PALETTE.length]);
      return map.get(key);
    };
  })();

  const withAlpha = (hex, a) => {
    const h = String(hex).replace("#", "");
    const full = h.length === 3 ? h.split("").map((c) => c + c).join("") : h;
    const n = parseInt(full, 16);
    return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${a})`;
  };
  const barFill = (c) => withAlpha(c, 0.32);
  const barLine = (c) => c;
  const barHover = (c) => withAlpha(c, 0.5);

  const usd = (n) => "$" + Number(n || 0).toLocaleString("ru-RU", { minimumFractionDigits: 4, maximumFractionDigits: 4 });
  const usd0 = (n) => "$" + Number(n || 0).toLocaleString("ru-RU", { minimumFractionDigits: 4, maximumFractionDigits: 4 });
  const pct = (n) => Number(n || 0).toFixed(1) + "%";
  const num = (n) => Number(n || 0).toLocaleString("ru-RU");
  const compact = (n) => {
    n = Number(n || 0);
    if (n >= 1e9) return (n / 1e9).toFixed(2) + "B";
    if (n >= 1e6) return (n / 1e6).toFixed(2) + "M";
    if (n >= 1e3) return (n / 1e3).toFixed(1) + "K";
    return String(n);
  };
  const modelKey = (m) => (m.provider || m.providerId || "?") + "/" + (m.model || m.modelId || "?");
  const ago = (ts) => {
    if (!ts) return "—";
    const s = Math.max(0, Math.round((Date.now() - new Date(ts).getTime()) / 1000));
    if (s < 60) return s + " с назад";
    if (s < 3600) return Math.round(s / 60) + " мин назад";
    return Math.round(s / 3600) + " ч назад";
  };

  async function fetchJson(url, opts) {
    const r = await fetch(url, opts);
    if (!r.ok) throw new Error("HTTP " + r.status);
    return r.json();
  }

  // слева в шапке: зелёная точка + «онлайн» + текущее время
  function setStatus(ok) {
    const el = $("#status");
    el.className = "status" + (ok ? "" : " err");
    el.innerHTML = '<span class="dot"></span>' + (ok ? "онлайн" : "офлайн") +
      '<span class="time">' + new Date().toLocaleTimeString("ru-RU") + "</span>";
  }

  function showAlert(msg) {
    const a = $("#alert");
    if (!msg) { a.hidden = true; return; }
    a.hidden = false;
    a.textContent = msg;
  }

  // -------- KPI (приоритет — данные профиля opencode) --------
  function renderKpis() {
    const L = state.profile && state.profile.limits;
    if (L) {
      const p = state.profile.periods || {};
      const items = [
        { k: "5 часов · Go", v: usd0(L.rollingUsage), s: `лимит ${usd0(L.rollingLimit)} · ${pct(L.rollingPct)} · сброс ${fmtReset(L.rollingResetSec)}`, cls: "accent" },
        { k: "Неделя · Go", v: usd0(L.weeklyUsage), s: `лимит ${usd0(L.weeklyLimit)} · ${pct(L.weeklyPct)}` },
        { k: "Месяц · Go", v: usd0(L.monthlyUsage), s: `лимит ${usd0(L.monthlyLimit)} · ${pct(L.monthlyPct)}` },
        { k: "Сегодня · профиль", v: usd0(p.today), s: `${num(p.todayCount)} запросов · в базе ${num(p.totalCount)}` },
      ];
      $("#kpis").innerHTML = items.map((i) => `
        <div class="kpi ${i.cls || ""}">
          <div class="k">${i.k}</div>
          <div class="v">${i.v}</div>
          <div class="s">${i.s}</div>
        </div>`).join("");
      return;
    }
    const d = state.local;
    if (!d) return;
    const q = d.periods;
    const items = [
      { k: "Сегодня (локально)", t: q.today, cls: "accent" },
      { k: "Неделя", t: q.week },
      { k: "Месяц", t: q.month },
      { k: "Всё время", t: q.all },
    ];
    $("#kpis").innerHTML = items.map((i) => `
      <div class="kpi ${i.cls || ""}">
        <div class="k">${i.k}</div>
        <div class="v">${usd0(i.t.cost)}</div>
        <div class="s">${compact(i.t.tokens)} токенов · ${num(i.t.messages)} шагов · ${num(i.t.sessions)} сессий</div>
      </div>`).join("");
  }

  // -------- budgets --------
  function renderBudgets(rows) {
    const el = $("#budgets");
    if (!rows || !rows.length) { el.innerHTML = '<div class="empty">Нет настроенных лимитов</div>'; return; }
    el.innerHTML = rows.map((b) => {
      const p = Math.min(100, Math.round((b.ratio || 0) * 100));
      const cls = b.ratio >= 1 ? "bad" : b.ratio >= (b.warnAt || 0.8) ? "warn" : "";
      return `<div class="budget-row">
        <span class="budget-name">${esc(b.name)}</span>
        <span class="budget-val">${usd0(b.spent)} / ${usd0(b.limit)} · ${p}%</span>
        <span class="bar ${cls}"><i style="width:${p}%"></i></span>
      </div>`;
    }).join("");
  }

  function esc(s) {
    return String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  }

  // -------- live (реальные активные сессии, те же, что в «Стоп») --------
  function renderLiveTree(tree, available, reason) {
    const el = $("#live");
    const flat = [];
    const walk = (n, depth) => { flat.push({ ...n, depth }); (n.children || []).forEach((c) => walk(c, depth + 1)); };
    (tree || []).forEach((n) => walk(n, 0));

    if (!available) {
      el.innerHTML = `<div class="empty">${esc(reason || "управление недоступно")}</div>`;
      $("#liveHint").textContent = "";
      return;
    }
    if (!flat.length) {
      el.innerHTML = '<div class="empty">Сейчас ничего не выполняется</div>';
      $("#liveHint").textContent = "";
      return;
    }
    el.innerHTML = flat.map((n) => `
      <div class="live-row" style="margin-left:${n.depth * 14}px">
        <span class="dot ${n.active ? "" : "idle"}"></span>
        <div class="meta">
          <div class="live-name" title="${esc(n.title || n.id)}">${esc(n.title || n.id)}</div>
          <div class="live-sub">${esc(n.agent || "—")} · ${esc(n.phase || n.status || "")}${n.detail ? " · " + esc(n.detail) : ""}${n.updated ? " · " + esc(ago(new Date(n.updated))) : ""}</div>
        </div>
      </div>`).join("");
    $("#liveHint").textContent = flat.length + " сессий · активных " + flat.filter(x => x.active).length;
  }

  async function loadLive() {
    try {
      const r = await fetchJson("/api/control/sessions");
      let tree = [];
      if (r.relayed) { try { tree = JSON.parse(r.raw).items || []; } catch { tree = []; } }
      else tree = r.tree || [];
      renderLiveTree(tree, r.available || r.relayed, r.reason);
    } catch { /* тихо */ }
  }

  // -------- sessions --------
  function renderSessions(list) {
    const tb = $("#sessionsTable tbody");
    let rows = list || [];
    if (state.mode === "paid") rows = rows.filter((r) => Number(r.cost) > 0);
    if (!rows.length) { tb.innerHTML = '<tr><td colspan="5" class="empty">Нет данных за период</td></tr>'; return; }
    tb.innerHTML = rows.map((s) => `<tr>
      <td class="cell-title" title="${esc(s.title)}">${esc(s.title || s.id)}</td>
      <td><span class="chip">${esc(s.agent || "?")}</span></td>
      <td class="num">${compact(s.tokens)}</td>
      <td class="num">${num(s.messages)}</td>
      <td class="num">${usd(s.cost)}</td>
    </tr>`).join("");
    $("#sessionsHint").textContent = rows.length + " шт.";
  }

  // -------- charts --------
  function chart(id, config) {
    const c = state.charts[id];
    if (c) { c.data = config.data; c.options = config.options; c.update("none"); return c; }
    state.charts[id] = new Chart($("#" + id), config);
    return state.charts[id];
  }

  const baseOpts = {
    responsive: true, maintainAspectRatio: false, animation: { duration: 350 },
    plugins: { legend: { labels: { color: "#8b93a7", boxWidth: 10, boxHeight: 10, usePointStyle: true } } },
  };

  function renderDaily(series) {
    const days = [...new Set(series.map((r) => r.day))].sort();
    const models = [...new Set(series.map((r) => modelKey(r)))];
    const idx = new Map(series.map((r) => [r.day + "|" + modelKey(r), Number(r.cost)]));
    const datasets = models.map((m) => ({
      label: m,
      data: days.map((d) => idx.get(d + "|" + m) || 0),
      backgroundColor: barFill(colorFor(m)),
      borderColor: barLine(colorFor(m)),
      borderWidth: 1.5,
      hoverBackgroundColor: barHover(colorFor(m)),
      hoverBorderColor: colorFor(m),
      borderRadius: 4,
      stack: "s",
    }));
    $("#seriesHint").textContent = days.length + " дней";
    chart("chartDaily", {
      type: "bar",
      data: { labels: days, datasets },
      options: {
        ...baseOpts,
        scales: {
          x: { stacked: true, ticks: { color: "#5f6880", maxRotation: 0, autoSkipPadding: 16 }, grid: { display: false } },
          y: { stacked: true, ticks: { color: "#5f6880", callback: (v) => "$" + v }, grid: { color: "rgba(255,255,255,.04)" } },
        },
      },
    });
  }

  function renderModels(byModel) {
    const top = byModel.slice(0, 12);
    chart("chartModels", {
      type: "doughnut",
      data: {
        labels: top.map(modelKey),
        datasets: [{
          data: top.map((m) => Number(m.cost)),
          backgroundColor: top.map((m) => withAlpha(colorFor(modelKey(m)), 0.45)),
          borderColor: top.map((m) => colorFor(modelKey(m))),
          borderWidth: 1.5,
          hoverBackgroundColor: top.map((m) => withAlpha(colorFor(modelKey(m)), 0.65)),
        }],
      },
      options: { ...baseOpts, cutout: "62%", plugins: { legend: { position: "bottom", labels: baseOpts.plugins.legend.labels } } },
    });
  }

  function renderTop(byModel) {
    const top = byModel.slice(0, 10).reverse();
    chart("chartTop", {
      type: "bar",
      data: {
        labels: top.map(modelKey),
        datasets: [{
          label: "Стоимость",
          data: top.map((m) => Number(m.cost)),
          backgroundColor: top.map((m) => barFill(colorFor(modelKey(m)))),
          borderColor: top.map((m) => barLine(colorFor(modelKey(m)))),
          borderWidth: 1.5,
          hoverBackgroundColor: top.map((m) => barHover(colorFor(modelKey(m)))),
          borderRadius: 5,
        }],
      },
      options: {
        ...baseOpts,
        indexAxis: "y",
        plugins: { legend: { display: false } },
        scales: {
          x: { ticks: { color: "#5f6880", callback: (v) => "$" + v }, grid: { color: "rgba(255,255,255,.04)" } },
          y: { ticks: { color: "#8b93a7" }, grid: { display: false } },
        },
      },
    });
  }

  // -------- load --------
  async function loadMe() {
    try {
      const m = await fetchJson("/api/me");
      state.mode = m.mode;
      $("#devicesBtn").hidden = m.mode !== "server";
      // на компьютере с opencode — привязка к серверу; на сервере — отслеживание ПК
      $("#serverLinkBtn").hidden = m.mode !== "collector";
      state.linked = !!m.linked;
      state.serverUrl = m.serverUrl || "";
      $("#serverLinkBtn").textContent = m.linked ? "Подключен к серверу" : "Подключить сервер";

      // справа: аватар аккаунта (Google или GitHub) и выход
      const el = $("#userLabel");
      // «Выйти»: на сервере — отключаем компьютер, в приложении — выходим из аккаунта opencode
      const logout = m.mode === "server" ? "/auth/logout" : "/auth/opencode-logout";
      const acc = m.account;
      state.account = acc && (acc.name || acc.email) ? (acc.name || acc.email) : "";
      state.devices = m.devices || [];
      state.selfDeviceId = m.selfDeviceId || "";
      if (acc && (acc.email || acc.name)) {
        const label = acc.name || acc.email;
        const initial = (label.trim()[0] || "?").toUpperCase();
        const pic = acc.picture
          ? `<img class="avatar" src="${esc(acc.picture)}" alt="" referrerpolicy="no-referrer">`
          : `<span class="avatar">${esc(initial)}</span>`;
        const codeLink = m.mode === "server" ? `<button type="button" class="linklike" id="accountOpen">Код доступа</button>` : "";
        el.innerHTML = pic + `<span class="name">${esc(label)}</span>` + codeLink + `<a href="${logout}">Выйти</a>`;
        const openBtn = $("#accountOpen");
        if (openBtn) openBtn.addEventListener("click", () => { $("#accountCodeBox").hidden = true; $("#accountDialog").showModal(); });
      } else {
        el.innerHTML = "";
      }
    } catch { /* тихо */ }
  }

  async function load() {
    try {
      const d = await fetchJson("/api/summary?range=all");
      showAlert(d.syncError ? "Синхронизация: " + d.syncError : "");
      setStatus(d.dbOk);
      $("#tzLabel").textContent = "данные профиля opencode · часовой пояс " + d.tz;
      $("#footLeft").textContent = "Итого за " + d.range.label + ": " + usd(d.totals.cost) + " · " + compact(d.totals.tokens) + " токенов";
      $("#footRight").textContent = "Моделей: " + d.byModel.length + " · сессий в периоде: " + num(d.totals.sessions);
      state.local = d;
      renderKpis();
      loadProfile();
      loadLive();
    } catch (e) {
      setStatus(false);
      showAlert("Не удалось получить данные: " + e.message);
    }
  }

  // -------- профиль opencode (их данные) --------
  function fmtReset(s) {
    if (!s) return "";
    const h = Math.floor(s / 3600), m = Math.round((s % 3600) / 60);
    return h ? h + "ч " + m + "м" : m + "м";
  }

  function renderSiteLimits(L) {
    const el = $("#siteLimits");
    if (!L) {
      el.innerHTML = '<div class="empty">Нет данных профиля. Нажми «Обновить профиль».</div>';
      $("#siteLimitsHint").textContent = "";
      $("#accountLabel").textContent = "";
      return;
    }
    const rows = [
      ["Go: 5 часов", L.rollingUsage, L.rollingLimit, L.rollingPct, L.rollingResetSec],
      ["Go: неделя", L.weeklyUsage, L.weeklyLimit, L.weeklyPct, L.weeklyResetSec],
      ["Go: месяц", L.monthlyUsage, L.monthlyLimit, L.monthlyPct, L.monthlyResetSec],
    ];
    el.innerHTML = rows.map(([name, used, lim, p, rs]) => {
      const cls = p >= 100 ? "bad" : p >= 80 ? "warn" : "";
      return `<div class="budget-row">
        <span class="budget-name">${name}</span>
        <span class="budget-val">${usd0(used)} / ${usd0(lim)} · ${pct(p)}${rs ? " · сброс " + fmtReset(rs) : ""}</span>
        <span class="bar ${cls}"><i style="width:${Math.min(100, p)}%"></i></span>
      </div>`;
    }).join("");
    $("#siteLimitsHint").textContent = "обновлено " + new Date(L.fetchedAt).toLocaleTimeString("ru-RU");
    $("#accountLabel").textContent = (L.email || "") + (L.plan ? " · plan " + L.plan : "") +
      " · balance " + usd0(L.balance) + (L.useBalance ? " (use balance)" : "");
  }

  const monthLabel = (m) => {
    const [y, mm] = m.split("-").map(Number);
    return new Date(y, mm - 1, 1).toLocaleDateString("ru-RU", { month: "long", year: "numeric" });
  };

  function renderSiteDaily(byDay) {
    const days = byDay || [];
    const months = [...new Set(days.map((x) => String(x.day).slice(0, 7)))].sort();
    if (!months.length) {
      $("#dayMonth").innerHTML = '<span class="month-static">нет данных</span>';
      $("#dayPrev").disabled = $("#dayNext").disabled = true;
      $("#siteDayHint").textContent = "";
      return;
    }
    if (!state.dayMonth || !months.includes(state.dayMonth)) state.dayMonth = months[months.length - 1];
    state.dayMonths = months;
    renderMonthPicker(months);

    const m = state.dayMonth;
    const [y, mm] = m.split("-").map(Number);
    const dim = new Date(y, mm, 0).getDate();
    const map = new Map(days.filter((x) => String(x.day).startsWith(m)).map((x) => [x.day, Number(x.cost)]));
    const labels = [], values = [];
    for (let d = 1; d <= dim; d++) {
      const key = `${m}-${String(d).padStart(2, "0")}`;
      labels.push(String(d));
      values.push(map.get(key) || 0);
    }
    const idx = months.indexOf(m);
    $("#dayPrev").disabled = idx <= 0;
    $("#dayNext").disabled = idx >= months.length - 1;
    $("#siteDayHint").textContent = usd0(values.reduce((a, b) => a + b, 0)) + " за месяц";

    chart("chartSiteDaily", {
      type: "bar",
      data: {
        labels,
        datasets: [{
          label: "Стоимость",
          data: values,
          backgroundColor: barFill("#35c88a"),
          borderColor: "#35c88a",
          borderWidth: 1.5,
          hoverBackgroundColor: barHover("#35c88a"),
          borderRadius: 4,
        }],
      },
      options: {
        ...baseOpts,
        plugins: { legend: { display: false } },
        scales: {
          x: { ticks: { color: "#5f6880", maxRotation: 0, autoSkipPadding: 8 }, grid: { display: false } },
          y: { ticks: { color: "#5f6880", callback: (v) => "$" + v }, grid: { color: "rgba(255,255,255,.04)" } },
        },
      },
    });
  }

  // выпадающий список месяцев: при одном месяце — просто текст
  function renderMonthPicker(months) {
    const host = $("#dayMonth");
    if (months.length <= 1) {
      host.innerHTML = `<span class="month-static">${esc(monthLabel(months[0]))}</span>`;
      return;
    }
    host.innerHTML = `<button type="button" class="month-btn" id="monthBtn">${esc(monthLabel(state.dayMonth))}<span class="chev">▾</span></button>
      <div class="month-list" id="monthList" hidden>${months.map((m) =>
        `<button type="button" class="month-item${m === state.dayMonth ? " active" : ""}" data-month="${m}">${esc(monthLabel(m))}</button>`).join("")}</div>`;

    const btn = $("#monthBtn"), list = $("#monthList");
    btn.addEventListener("click", (e) => {
      e.stopPropagation();
      list.hidden = !list.hidden;
      if (!list.hidden) document.addEventListener("click", () => { list.hidden = true; }, { once: true });
    });
    list.addEventListener("click", (e) => {
      const it = e.target.closest("[data-month]");
      if (!it) return;
      state.dayMonth = it.dataset.month;
      list.hidden = true;
      if (state.profile) renderSiteDaily(state.profile.summary.byDay);
    });
  }

  function shiftDayMonth(delta) {
    const months = state.dayMonths || [];
    const i = months.indexOf(state.dayMonth);
    const next = months[i + delta];
    if (!next) return;
    state.dayMonth = next;
    if (state.profile) renderSiteDaily(state.profile.summary.byDay);
  }

  function renderSiteSessions(list) {
    const tb = $("#siteSessionsTable tbody");
    let rows = list || [];
    if (state.mode === "paid") rows = rows.filter((x) => Number(x.cost) > 0);
    if (!rows.length) { tb.innerHTML = '<tr><td colspan="5" class="empty">Нет данных</td></tr>'; $("#siteSessionsHint").textContent = ""; return; }
    tb.innerHTML = rows.slice(0, 60).map((s) => `<tr>
      <td class="cell-title" title="${esc(s.sessionId)}">${esc(s.orphan ? "Subagent без родителя ····" + String(s.sessionId || "").slice(-4) : (s.title || "сессия ····" + String(s.sessionId || "").slice(-4)))}</td>
      <td><span class="chip">${esc(s.agent || "—")}</span></td>
      <td class="num">${num(s.steps)}</td>
      <td class="num">${compact(s.tokens)}</td>
      <td class="num">${usd(s.cost)}</td>
    </tr>`).join("");
    $("#siteSessionsHint").textContent = rows.length + " чатов · " + (state.mode === "paid" ? "только платные" : "все");
  }

  function renderSiteModels(byModel) {
    let rows = byModel || [];
    if (state.mode === "paid") rows = rows.filter((m) => Number(m.cost) > 0);
    const top = rows.slice(0, 12).reverse();
    chart("chartSiteModels", {
      type: "bar",
      data: {
        labels: top.map((m) => m.model),
        datasets: [{
          label: "Стоимость",
          data: top.map((m) => Number(m.cost)),
          backgroundColor: top.map((m) => barFill(colorFor(m.model))),
          borderColor: top.map((m) => colorFor(m.model)),
          borderWidth: 1.5,
          hoverBackgroundColor: top.map((m) => barHover(colorFor(m.model))),
          borderRadius: 5,
        }],
      },
      options: {
        ...baseOpts,
        indexAxis: "y",
        plugins: { legend: { display: false } },
        scales: {
          x: { ticks: { color: "#5f6880", callback: (v) => "$" + v }, grid: { color: "rgba(255,255,255,.04)" } },
          y: { ticks: { color: "#8b93a7" }, grid: { display: false } },
        },
      },
    });
    $("#siteModelsHint").textContent = rows.length + " моделей · " + (state.mode === "paid" ? "платные" : "все");
  }

  function renderSiteRecent(recent) {
    const tb = $("#siteRecentTable tbody");
    let rows = recent || [];
    if (state.mode === "paid") rows = rows.filter((r) => Number(r.cost) > 0);
    if (!rows.length) { tb.innerHTML = '<tr><td colspan="6" class="empty">Нет записей</td></tr>'; $("#siteRecentHint").textContent = ""; return; }
    tb.innerHTML = rows.slice(0, 60).map((r) => `<tr>
      <td>${new Date(r.time).toLocaleTimeString("ru-RU")}</td>
      <td>${esc(r.model || "?")}</td>
      <td class="num">${compact(r.input)}</td>
      <td class="num">${compact(r.output)}</td>
      <td class="num">${compact(r.cacheRead)}</td>
      <td class="num">${usd(r.cost)}</td>
    </tr>`).join("");
    $("#siteRecentHint").textContent = rows.length + " записей · " + (state.mode === "paid" ? "платные" : "все");
  }

  function renderSubscription(payments, liteSub) {
    const el = $("#subscription");
    const list = payments || [];
    const last = list[0];
    let next = null;
    if (last && last.paidAt) {
      const d = new Date(last.paidAt);
      next = new Date(d.getFullYear(), d.getMonth() + 1, d.getDate());
    }
    el.innerHTML = `
      <div class="sub-row"><span>План</span><b>${esc((state.profile && state.profile.limits && state.profile.limits.plan) || "lite")}</b></div>
      <div class="sub-row"><span>ID подписки</span><b>${liteSub ? esc(liteSub) : "—"}</b></div>
      <div class="sub-row"><span>Последний платёж</span><b>${last ? usd0(last.amount) + " · " + new Date(last.paidAt).toLocaleDateString("ru-RU") : "—"}</b></div>
      <div class="sub-row"><span>Следующий ~</span><b>${next ? next.toLocaleDateString("ru-RU") : "—"}</b></div>`;
    const tb = $("#paymentsTable tbody");
    tb.innerHTML = list.length
      ? list.map((p) => `<tr><td>${new Date(p.paidAt).toLocaleDateString("ru-RU")}</td><td class="cell-title">${esc(p.id)}</td><td class="num">${usd0(p.amount)}</td></tr>`).join("")
      : '<tr><td colspan="3" class="empty">Нет платежей</td></tr>';
    $("#subHint").textContent = list.length + " платежей";
  }

  async function loadProfile() {
    try {
      const p = await fetchJson("/api/profile");
      state.profile = p;
      renderKpis();
      renderSiteLimits(p.limits);
      renderSiteDaily(p.summary.byDay);
      renderSiteModels(p.summary.byModel);
      renderSiteSessions(p.bySession);
      renderSiteRecent(p.summary.recent);
      renderSubscription(p.payments, p.liteSubscriptionId);

      // данных ещё нет — на сервере их присылает приложение с opencode
      if (!p.hasData && !state.noDataAsked) {
        state.noDataAsked = true;
        toast("Данных пока нет: подключите компьютер с opencode", "err");
      }
    } catch { /* тихо */ }
  }

  $("#modes").addEventListener("click", (e) => {
    const btn = e.target.closest("button[data-mode]");
    if (!btn) return;
    state.mode = btn.dataset.mode;
    document.querySelectorAll("#modes button").forEach((x) => x.classList.toggle("active", x === btn));
    if (state.profile) {
      renderSiteModels(state.profile.summary.byModel);
      renderSiteSessions(state.profile.bySession);
      renderSiteRecent(state.profile.summary.recent);
    }
  });

  // привязка этого компьютера к серверу — страница с полем для кода
  $("#serverLinkBtn").addEventListener("click", () => {
    if (!state.linked) { location.href = "/login"; return; }
    const st = $("#srvState");
    st.className = "srv-state ok";
    st.querySelector(".srv-state-text").textContent = "подключено · данные отправляются";
    $("#srvAccount").textContent = state.account || "—";
    $("#srvUrl").textContent = state.serverUrl || "—";
    renderSrvDevices();
    $("#serverDialog").showModal();
  });

  function renderSrvDevices() {
    const box = $("#srvDevices");
    const list = state.devices || [];
    $("#srvDevicesHint").textContent = list.length ? list.length + " шт." : "";
    if (!list.length) { box.innerHTML = '<div class="empty">нет данных</div>'; return; }
    box.innerHTML = list.map((d) => `
      <div class="srv-device${d.id === state.selfDeviceId ? " self" : ""}">
        <div>
          <div class="nm">${esc(d.name || d.id)}${d.id === state.selfDeviceId ? " · этот компьютер" : ""}</div>
          <div class="sub">${d.lastSeen ? "был(а) " + esc(ago(d.lastSeen)) : ""}</div>
        </div>
        ${d.id === state.selfDeviceId ? "" : `<button class="btn danger" data-revoke-srv="${esc(d.id)}">Отключить</button>`}
      </div>`).join("");
  }
  $("#serverClose").addEventListener("click", () => $("#serverDialog").close());
  $("#serverDialog").addEventListener("click", (e) => { if (e.target === $("#serverDialog")) $("#serverDialog").close(); });
  $("#serverDialog").addEventListener("cancel", (e) => { e.preventDefault(); $("#serverDialog").close(); });
  $("#serverCopy").addEventListener("click", async () => {
    try { await navigator.clipboard.writeText(state.serverUrl || ""); toast("Адрес скопирован", "ok"); }
    catch { toast("Скопируйте адрес вручную", "err"); }
  });
  $("#serverUnlink").addEventListener("click", () => { location.href = "/auth/unlink"; });

  // отключение чужого компьютера от аккаунта (через локальный прокси)
  $("#srvDevices").addEventListener("click", async (e) => {
    const b = e.target.closest("[data-revoke-srv]");
    if (!b) return;
    b.disabled = true; b.textContent = "…";
    try {
      const r = await fetch("/api/server/revoke", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ id: b.dataset.revokeSrv })
      });
      const j = await r.json().catch(() => ({}));
      toast(j.ok ? "Компьютер отключён" : "Не удалось отключить", j.ok ? "ok" : "err");
    } catch (e2) { toast("Ошибка: " + e2.message, "err"); }
    setTimeout(() => loadMe(), 800);
  });

  // код доступа к аккаунту сервера (вход с других устройств)
  async function makeAccountCode() {
    const b = $("#accountCodeBtn");
    b.disabled = true;
    try {
      const r = await fetchJson("/api/account/code", { method: "POST" });
      const shown = String(r.code || "").replace(/(.{4})(?=.)/g, "$1-");
      const box = $("#accountCodeBox");
      box.hidden = false;
      box.innerHTML = `<div class="code" id="accountCodeValue">${esc(shown)}</div>
        <div class="pair-actions">
          <button type="button" class="btn" id="accountCodeCopy">Скопировать код</button>
          <span class="hint">один код на все ваши устройства</span>
        </div>`;
      $("#accountCodeCopy").addEventListener("click", async () => {
        try { await navigator.clipboard.writeText(shown); toast("Код скопирован", "ok"); }
        catch { toast("Скопируйте код вручную", "err"); }
      });
    } catch (e) { toast("Не удалось получить код: " + e.message, "err"); }
    finally { b.disabled = false; }
  }

  $("#accountClose").addEventListener("click", () => $("#accountDialog").close());
  $("#accountDialog").addEventListener("click", (e) => { if (e.target === $("#accountDialog")) $("#accountDialog").close(); });
  $("#accountDialog").addEventListener("cancel", (e) => { e.preventDefault(); $("#accountDialog").close(); });
  $("#accountCodeBtn").addEventListener("click", makeAccountCode);

  // -------- отслеживаемые компьютеры --------
  async function loadDevices() {
    const box = $("#connDevices");
    try {
      const d = await fetchJson("/api/devices");
      const list = d.devices || [];
      $("#connAccount").textContent = state.account || "—";
      const st = $("#connState");
      st.className = "srv-state" + (list.length ? " ok" : " err");
      st.querySelector(".srv-state-text").textContent = list.length ? "подключено" : "нет подключённого компьютера";
      $("#connHint").textContent = list.length ? "1 из 1" : "";
      $("#disconnectAll").disabled = !list.length;
      box.innerHTML = list.length
        ? list.map((x) => `
            <div class="srv-device">
              <div>
                <div class="nm">${esc(x.name || x.id)}</div>
                <div class="sub">${esc(ago(x.lastSeen))}</div>
              </div>
              <button class="btn danger" data-revoke="${esc(x.id)}">Отключить</button>
            </div>`).join("")
        : '<div class="empty">Пока ни одного компьютера</div>';
    } catch (e) {
      box.innerHTML = '<div class="empty">Не удалось загрузить: ' + esc(e.message) + '</div>';
    }
  }

  function confirmAction(title, text, yesLabel, onYes) {
    $("#confirmTitle").textContent = title;
    $("#confirmText").textContent = text;
    $("#confirmYes").textContent = yesLabel;
    $("#confirmYes").onclick = async () => { $("#confirmDialog").close(); await onYes(); };
    $("#confirmDialog").showModal();
  }
  $("#confirmClose").addEventListener("click", () => $("#confirmDialog").close());
  $("#confirmNo").addEventListener("click", () => $("#confirmDialog").close());
  $("#confirmDialog").addEventListener("click", (e) => { if (e.target === $("#confirmDialog")) $("#confirmDialog").close(); });
  $("#confirmDialog").addEventListener("cancel", (e) => { e.preventDefault(); $("#confirmDialog").close(); });

  async function disconnectAllDevices() {
    try {
      const d = await fetchJson("/api/devices");
      for (const x of (d.devices || [])) await fetchJson("/api/devices/" + encodeURIComponent(x.id), { method: "DELETE" });
      toast("Соединение разорвано", "ok");
    } catch (e) { toast("Ошибка: " + e.message, "err"); }
    setTimeout(() => location.reload(), 700);
  }
  $("#disconnectAll").addEventListener("click", () => confirmAction(
    "Разорвать соединение?",
    "Компьютер будет отключён от аккаунта на всех устройствах. Чтобы подключить снова, понадобится новый код.",
    "Разорвать", disconnectAllDevices));

  function revokeDevice(id, btn) { confirmAction("Отключить компьютер?",
    "Компьютер будет отключён от аккаунта на всех устройствах.", "Отключить", async () => {
    if (btn) { btn.disabled = true; btn.textContent = "…"; }
    try { await fetchJson("/api/devices/" + encodeURIComponent(id), { method: "DELETE" }); toast("Компьютер отключён", "ok"); }
    catch (e) { toast("Ошибка: " + e.message, "err"); }
    loadDevices();
  }); }

  $("#devicesBtn").addEventListener("click", () => {
    if (state.pairTimer) { clearInterval(state.pairTimer); state.pairTimer = null; }
    $("#devicesDialog").showModal();
    loadDevices();
  });
  $("#devicesClose").addEventListener("click", () => { if (state.pairTimer) { clearInterval(state.pairTimer); state.pairTimer = null; } $("#devicesDialog").close(); });
  // закрытие по клику вне окна и по Esc
  $("#devicesDialog").addEventListener("click", (e) => { if (e.target === $("#devicesDialog")) { if (state.pairTimer) { clearInterval(state.pairTimer); state.pairTimer = null; } $("#devicesDialog").close(); } });
  $("#devicesDialog").addEventListener("cancel", (e) => { e.preventDefault(); if (state.pairTimer) { clearInterval(state.pairTimer); state.pairTimer = null; } $("#devicesDialog").close(); });
  $("#connDevices").addEventListener("click", (e) => {
    const b = e.target.closest("[data-revoke]");
    if (b) revokeDevice(b.dataset.revoke, b);
  });

  // -------- управление: остановка всех чатов --------
  async function loadControl() {
    try {
      const s = await fetchJson("/api/control/status");
      const b = $("#stopBtn"), n = $("#stopCount");
      b.disabled = !s.available;
      b.classList.toggle("on", !!s.active);
      n.textContent = s.active ? " (" + s.active + ")" : "";
      b.title = s.available
        ? (s.active ? "Активных сессий: " + s.active : "Сейчас активных сессий нет")
        : (s.reason || "управление недоступно");
    } catch { /* тихо */ }
  }

  // -------- модальное окно активных сессий --------
  const STOP_PER_PAGE = 5;

  function toast(msg, kind) {
    const el = document.createElement("div");
    el.className = "toast" + (kind ? " " + kind : "");
    el.textContent = msg;
    $("#toasts").appendChild(el);
    setTimeout(() => el.remove(), 4000);
  }

  function stopRow(n) {
    const kids = n.children || [];
    const expanded = state.expanded.has(n.id);
    const arrow = kids.length
      ? `<button class="stop-toggle" data-toggle="${esc(n.id)}" title="${kids.length} субагентов">${expanded ? "▾" : "▸"} ${kids.length}</button>`
      : '<span class="stop-toggle empty"></span>';
    const row = `<div class="stop-row">
      ${arrow}
      <div class="meta">
        <div class="ttl" title="${esc(n.title || n.id)}">${esc(n.title || n.id)}</div>
        <div class="sub"><span class="dot ${n.active ? "" : "idle"}"></span>${esc(n.agent || "—")} · ${esc(n.phase || n.status || "")}${n.detail ? " · " + esc(n.detail) : ""}${n.updated ? " · " + esc(ago(new Date(n.updated))) : ""}</div>
      </div>
      <button class="btn danger" data-stop="${esc(n.id)}" title="Остановить">Стоп</button>
    </div>`;
    if (!kids.length || !expanded) return row;
    return row + `<div class="stop-children">${kids.map(stopRow).join("")}</div>`;
  }

  function renderStopPage(page) {
    const roots = state.stopList || [];
    const body = $("#stopBody");
    const pages = Math.max(1, Math.ceil(roots.length / STOP_PER_PAGE));
    const p = Math.min(Math.max(1, page), pages);
    state.stopPage = p;

    if (!roots.length) {
      body.innerHTML = '<div class="empty">Сейчас ничего не выполняется</div>';
      $("#stopSummary").textContent = "";
      return;
    }
    $("#stopSummary").textContent = roots.length + " активных · стр. " + p + " из " + pages;
    const rows = roots.slice((p - 1) * STOP_PER_PAGE, p * STOP_PER_PAGE).map((n) => stopRow(n, 0)).join("");
    const pager = pages > 1 ? `
      <div class="stop-pager">
        <button class="btn" data-page="${p - 1}" ${p <= 1 ? "disabled" : ""}>‹</button>
        <span class="hint">${p} / ${pages}</span>
        <button class="btn" data-page="${p + 1}" ${p >= pages ? "disabled" : ""}>›</button>
      </div>` : "";
    body.innerHTML = rows + pager;
  }

  async function refreshStop(page) {
    $("#stopBody").innerHTML = '<div class="empty">ищем активные сессии…</div>';
    try {
      const r = await fetchJson("/api/control/sessions");
      let list = [];
      if (r.relayed) {
        try { list = (JSON.parse(r.raw).items) || []; } catch { list = []; }
      } else if (r.available) {
        list = r.tree || [];
      } else {
        $("#stopBody").innerHTML = '<div class="empty">' + esc(r.reason || "управление недоступно") + '</div>';
        return;
      }
      state.stopList = list;
      renderStopPage(page || 1);
    } catch (e) {
      $("#stopBody").innerHTML = '<div class="empty">ошибка: ' + esc(e.message) + '</div>';
    }
  }

  async function stopOne(id, btn) {
    if (btn) { btn.disabled = true; btn.textContent = "останавливаю…"; }
    try {
      const r = await fetchJson("/api/control/sessions/" + encodeURIComponent(id) + "/stop", { method: "POST" });
      if (r.ok) toast(r.queued ? "Команда отправлена на устройство"
        : (r.stopped > 1 ? `Прервано сессий: ${r.stopped}` : "Сессия прервана"), "ok");
      else toast("Не удалось: " + (r.error || "неизвестная ошибка"), "err");
    } catch (e) { toast("Ошибка: " + e.message, "err"); }
    // проверочный запрос — обновляем список
    setTimeout(() => refreshStop(state.stopPage), 900);
  }

  $("#stopBtn").addEventListener("click", () => {
    $("#stopDialog").showModal();
    refreshStop(1);
  });
  $("#stopClose").addEventListener("click", () => $("#stopDialog").close());
  $("#stopRefresh").addEventListener("click", () => refreshStop(state.stopPage));
  $("#stopBody").addEventListener("click", (e) => {
    const pg = e.target.closest("[data-page]");
    if (pg) { renderStopPage(Number(pg.dataset.page)); return; }
    const tg = e.target.closest("[data-toggle]");
    if (tg) {
      const id = tg.dataset.toggle;
      if (state.expanded.has(id)) state.expanded.delete(id); else state.expanded.add(id);
      renderStopPage(state.stopPage);
      return;
    }
    const st = e.target.closest("[data-stop]");
    if (st) stopOne(st.dataset.stop, st);
  });

  $("#dayPrev").addEventListener("click", () => shiftDayMonth(-1));
  $("#dayNext").addEventListener("click", () => shiftDayMonth(1));
  

  load();
  loadMe();
  loadControl();
  setInterval(loadControl, 1000);
  state.timer = setInterval(load, 1000);
  // сам профиль обновляет фоновый сервис на сервере; здесь только перечитываем из базы
  setInterval(loadProfile, 1000);

  // мгновенные обновления: сервер сам сообщает, когда меняется состояние сессий
  try {
    const es = new EventSource("/api/events");
    es.onmessage = () => {
      loadLive();
      loadControl();
      const dlg = document.getElementById("stopDialog");
      if (dlg && dlg.open) refreshStop(state.stopPage);
    };
  } catch (e) { /* тихо */ }
})();
