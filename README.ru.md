[English](README.md) | **Русский**

# OpenCode Spend

[![Repository](https://img.shields.io/badge/GitHub-OpenCodeSpend-181717?logo=github&logoColor=white)](https://github.com/JevKray/OpenCodeSpend)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![WPF](https://img.shields.io/badge/WPF-Windows-0078D4?logo=windows&logoColor=white)](https://learn.microsoft.com/dotnet/desktop/wpf/)
[![Docker](https://img.shields.io/badge/Docker-ready-2496ED?logo=docker&logoColor=white)](https://www.docker.com/)
[![SQLite](https://img.shields.io/badge/SQLite-%D1%85%D1%80%D0%B0%D0%BD%D0%B8%D0%BB%D0%B8%D1%89%D0%B5-003B57?logo=sqlite&logoColor=white)](https://www.sqlite.org/)
[![status](https://img.shields.io/badge/status-active-35c88a)](#)
![License](https://img.shields.io/badge/license-MIT-35c88a)
[![Live Server](https://img.shields.io/badge/Live%20Server-agentstats.we4.online-35c88a?logo=googlechrome&logoColor=white)](https://agentstats.we4.online/)

Мониторинг расходов и активности opencode. Приложение на ПК собирает данные и
шлёт их на сервер, а на сервере работает веб-панель с аккаунтами: траты, лимиты
Go, платежи, активные чаты с фазами и кнопкой остановки агентов. Всё считается по
данным opencode.ai (консоль) и локальной базы opencode.

<p align="center">
  <img src="docs/desktop-ru.png" width="82%" alt="Панель OpenCode Spend — десктоп" />
</p>
<p align="center">
  <img src="docs/mobile-ru.png" width="24%" alt="Мобильный вид" />
  <img src="docs/tablet-ru.png" width="36%" alt="Планшетный вид" />
</p>

*Веб-панель: траты, лимиты Go, разбивка по моделям и активные сессии.*

## 🌐 Готовый сервер — разворачивать не обязательно

Панель уже работает: **https://agentstats.we4.online/**

Свой сервер поднимать не нужно — достаточно авторизоваться и поставить приложение:

1. Открой **https://agentstats.we4.online/** и войди (через Google или по коду доступа к аккаунту).
2. Скачай приложение из раздела [**Releases**](../../releases) — архив `OpenCodeSpend-win-x64.zip`.
3. Распакуй и запусти `OpenCodeSpend.exe`.
4. На сайте открой **«Управление соединением»** и скопируй код подключения.
5. В приложении нажми **«Подключить сервер»** и вставь код.
6. Готово — траты, лимиты Go и живые сессии появятся в панели, откуда их можно смотреть с любого устройства.

> Один аккаунт — один компьютер. Хочешь свой сервер — см. раздел «🐳 Развёртывание на сервере».

## ✨ Возможности

- **Лимиты Go** — окна 5 часов / неделя / месяц с прогрессом.
- **Траты по дням** — динамика расходов.
- **Разбивка по моделям** — где именно уходит бюджет.
- **История запросов** — журнал обращений к моделям.
- **Платежи и подписка** — статус оплат и текущего плана.
- **Точное отслеживание активных чатов** — состояние берётся из самого opencode, а не по таймерам; сессии со всех запущенных серверов opencode собираются в одно дерево.
- **Индикатор состояния на аватаре** — онлайн / оффлайн / подключён к серверу.
- **Остановка агентов** — команда «Стоп» уходит на ПК.
- **Мультиаккаунт** — несколько аккаунтов на одном сервере.
- **Привязка ПК по коду** — один аккаунт = один ПК.
- **Вход Google/GitHub + код доступа** — провайдеры опциональны.
- **Интерфейс RU/EN** — русский и английский, язык определяется автоматически.
- **Адаптивная вёрстка** — удобно с телефона и планшета.
- **Мгновенные обновления через SSE** — браузер обновляется без перезагрузки.

## 🏗 Архитектура

```
┌─────────────── ПК (Windows) ───────────────┐        ┌──────── СЕРВЕР (Linux/Docker) ────────┐
│ opencode  ──SSE──▶ сборщик ──POST /api/device/sync──▶│ аккаунты, SQLite, веб-панель          │
│     ▲                   ◀── команды (стоп) ──────────│ /api/events (SSE) ──▶ браузер         │
└────────────────────────────────────────────┘        └──────────────────────────────────────┘
```

Одна сборка играет две роли. Роль задаётся конфигом `Spend:Mode` (`server` или
`collector`) и наличием локальной базы opencode:

- **СЕРВЕР** (`Spend__Mode=server`) — аккаунты, вход, хранение данных, веб-панель.
  Сам в opencode не ходит.
- **СБОРЩИК** (приложение на ПК, WPF + WebView2) — поднимает панель на случайном
  локальном порту, собирает данные и отправляет их на сервер.

Связь: ПК привязывается к аккаунту кодом подключения (в нём зашифрован адрес
сервера), дальше раз в секунду шлёт один запрос `POST /api/device/sync` и получает
в ответе команды. Если сервер отвечает 401 — ПК отвязывается.

## 🚀 Быстрый старт

Сервер (локально, через Docker):

```bash
docker compose up -d --build
# панель: http://127.0.0.1:15000
```

Приложение для ПК:

```bash
dotnet publish src/OpenCodeSpend.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Первый вход: на сервере войти через Google/GitHub либо по коду доступа к аккаунту;
затем «Управление соединением» → взять код → вставить в приложении в «Подключить
сервер».

## 🐳 Развёртывание на сервере

Нужен Linux-сервер с Docker и плагином `docker compose`.

**Что скопировать.** Папку проекта с `docker-compose.yml`, `Dockerfile` и
исходниками `src/OpenCodeSpend.Web`. Образ собирается двухстадийно:
`mcr.microsoft.com/dotnet/sdk:10.0` выполняет `restore` и
`publish -c Release -o /app`, затем рантайм `mcr.microsoft.com/dotnet/aspnet:10.0`
копирует `/app` и запускает `dotnet OpenCodeSpend.Web.dll`.

**Конфигурация.** Создайте `.env` на основе `.env.example` и задайте значения.

**Запуск.**

```bash
docker compose up -d --build   # собрать и поднять
docker compose logs -f         # логи в реальном времени
docker compose down            # остановить (тома сохраняются)
```

**Порты.** Внутри контейнера приложение слушает `5199`, наружу порт публикуется
только на loopback хоста:

```yaml
ports:
  - "127.0.0.1:15000:5199"
```

Публиковать `5199` напрямую в интернет не нужно — снаружи доступ идёт через
reverse-proxy на `127.0.0.1:15000`.

**Тома.** Данные переживают пересборку благодаря двум томам:

```yaml
volumes:
  - appdata:/data
  - dpkeys:/root/.aspnet/DataProtection-Keys
```

`appdata` хранит SQLite-базу `/data/opencodespend.db`. `dpkeys` хранит ключи
ASP.NET Core Data Protection: при их потере инвалидируются cookie сессий и все
пользователи разлогинятся, а данные аккаунтов останутся.

**Обновление.** `docker compose up -d --build` пересобирает и поднимает заново.
Для автозапуска включите у службы Docker политику `restart: unless-stopped`
(уже задана в compose) — контейнер поднимется после перезагрузки хоста.

## 🔒 Домен и HTTPS

Панель сама HTTPS не терминирует — нужен Caddy или nginx на хосте, который
слушает домен, выдаёт TLS и проксирует на `127.0.0.1:15000`. Приложение читает
заголовки `X-Forwarded-Proto` и `X-Forwarded-Host` и строит ссылки, включая
OAuth-редиректы, из схемы и хоста запроса.

Пример Caddy:

```caddyfile
ocspend.example.com {
    reverse_proxy 127.0.0.1:15000
}
```

Пример nginx (TLS добавьте отдельно, например через certbot):

```nginx
server {
    listen 80;
    server_name ocspend.example.com;

    location / {
        proxy_pass http://127.0.0.1:15000;
        proxy_set_header Host              $host;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host  $host;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
    }
}
```

Для потока `/api/events` (SSE) в nginx **обязательно** отключить буферизацию,
иначе обновления не будут приходить мгновенно:

```nginx
location /api/events {
    proxy_pass http://127.0.0.1:15000;
    proxy_buffering off;
    proxy_set_header Host              $host;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Host  $host;
}
```

**Адреса возврата OAuth.** В консолях провайдеров прописать:

- Google Cloud Console → OAuth 2.0 Client ID → Authorized redirect URIs:
  `https://<домен>/signin-google`
- GitHub → OAuth App → Authorization callback URL: `https://<домен>/signin-github`

Адрес приложение вычисляет из самого запроса, поэтому он должен совпадать с тем,
по которому открыта панель. Если OAuth не нужен — вход по коду доступа к аккаунту
работает всегда.

## ⚙️ Переменные окружения

`docker-compose.yml` читает из `.env` эти переменные. Секреты живут только в
`.env` (в образ не попадают) и передаются контейнеру как `Spend__*`.

| Переменная | Значение | Зачем |
| --- | --- | --- |
| `TZ` | `Europe/Moscow` | часовой пояс сервера |
| `PAIRING_TTL` | `5` | срок жизни кода подключения ПК, минуты |
| `GOOGLE_CLIENT_ID` | `` | OAuth-клиент Google (опционально) |
| `GOOGLE_CLIENT_SECRET` | `` | секрет OAuth-клиента Google |
| `GITHUB_CLIENT_ID` | `` | OAuth-клиент GitHub (опционально) |
| `GITHUB_CLIENT_SECRET` | `` | секрет OAuth-клиента GitHub |

## 🔌 Порты

| Порт | Где | Назначение |
| --- | --- | --- |
| `5199` | внутри контейнера | `ASPNETCORE_URLS` / `EXPOSE`, менять не нужно |
| `15000` | проброс на хосте | `127.0.0.1:15000:5199`, вход для reverse-proxy |
| `80` / `443` | reverse-proxy | Caddy/nginx, публикация в интернет |
| случайный | локально на ПК | панель приложения, только `127.0.0.1` |

## 🧭 Пример работы

1. Развернули сервер и открыли панель по своему домену.
2. Вошли — через Google/GitHub или по коду доступа к аккаунту.
3. На панели открыли «Управление соединением» и взяли код подключения.
4. Вставили код в приложении в «Подключить сервер».
5. На панели появились траты и лимиты Go.
6. В разделе «Сессии» видно активные чаты с фазами (печатает, инструмент, команда).
7. Нажали «Стоп» — агент остановлен, состояние обновилось мгновенно.

## 🧱 Стек

- **.NET 10**
- **ASP.NET Core** — серверная логика, API и веб-панель
- **WPF + WebView2** — приложение для ПК
- **SQLite** — единственное хранилище, один файл
- **Docker** — сборка и развёртывание сервера

## 📁 Структура

```
OpencodeSpend/
├─ src/
│  └─ OpenCodeSpend.Web/       # логика + wwwroot (index.html, app.js, styles.css)
│  └─ OpenCodeSpend.Desktop/   # WPF + WebView2, сбор и отправка данных
├─ tests/
│  └─ OpenCodeSpend.Tests/     # самопроверки
├─ docs/                       # скриншоты
├─ docker-compose.yml
├─ Dockerfile
└─ .env.example
```

## 🧪 Тесты

```bash
dotnet run --project tests/OpenCodeSpend.Tests
```

42 самопроверки.

## ❓ Частые проблемы

- **`redirect_uri_mismatch`** — адрес возврата не прописан в консоли провайдера.
- **В панели «нет данных»** — ПК не привязан или отвязан (сервер ответил 401).
- **Профиль не подтягивается** — в приложении истекла сессия opencode, нужно войти
  заново.
- **После пересборки пропали сессии** — проверьте, что том `dpkeys` на месте.

---

## 📄 Лицензия

Проект распространяется по лицензии [MIT](LICENSE). Репозиторий открыт.

## 💬 Contacts & Support

<div align="center">

[![Telegram](https://img.shields.io/badge/Telegram-@eugenekray-2CA5E0?logo=telegram&logoColor=white)](https://t.me/eugenekray)
[![Email](https://img.shields.io/badge/Email-krasovskyworks@gmail.com-EA4335?logo=gmail&logoColor=white)](mailto:krasovskyworks@gmail.com)
[![Boosty](https://img.shields.io/badge/Support-Boosty-FF6A00?logo=boosty&logoColor=white)](https://boosty.to/jevkray)

</div>
