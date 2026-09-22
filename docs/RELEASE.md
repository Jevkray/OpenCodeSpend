# OpenCode Spend v1.0.0

Мониторинг расходов и активности opencode: приложение на ПК собирает данные, а на
сервере работает веб-панель с аккаунтами. Траты, лимиты Go, платежи и живые
сессии с фазами — всё в одном месте.

### 📦 Что в архивах

- `OpenCodeSpend-win-x64.zip` — приложение для ПК (Windows 10/11 x64). Внутри
  `OpenCodeSpend.exe` и `INSTALL.md`.
- `OpenCodeSpend-server-linux-x64.tar.gz` — сервер для Linux x64. Внутри готовая
  сборка и `INSTALL.md`.

### 🚀 Быстрый старт

**ПК.** Распакуйте архив, запустите `OpenCodeSpend.exe`, войдите в opencode и
привяжите ПК по коду из панели сервера.

**Сервер.** Распакуйте архив, задайте переменные окружения и запустите
`./OpenCodeSpend.Web`, затем поставьте за reverse-proxy.

```bash
Spend__Mode=server
Spend__DatabasePath=/opt/opencodespend/data/spend.db
Spend__TimeZone=UTC
Spend__PairingTtlMinutes=5
# при необходимости:
# Spend__GoogleClientId / Spend__GoogleClientSecret
# Spend__GitHubClientId / Spend__GitHubClientSecret
```

```bash
./OpenCodeSpend.Web
```

### ✨ Основное

- Лимиты Go — окна 5 часов / неделя / месяц с прогрессом.
- Траты по дням и разбивка по моделям.
- Платежи и статус подписки.
- Живые сессии с фазами и остановкой агентов.
- Мультиаккаунт — несколько аккаунтов на одном сервере.
- Мгновенные обновления через SSE — без перезагрузки страницы.

### 📄 Подробности

- [README.md](../README.md)
- [docs/INSTALL.md](INSTALL.md)

**Требования.** Приложение — Windows 10/11 x64. Сервер — Linux x64 + .NET 10 (для
self-contained не нужен) либо Docker.
