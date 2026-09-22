# Установка OpenCode Spend

Инструкция по установке собранных файлов из релиза. Подробности о проекте — в
[README.md](../README.md).

## Приложение для ПК (Windows)

1. Скачайте `OpenCodeSpend-win-x64.exe` из релиза.
2. Положите файл в любую папку и запустите его.
3. Приложение само поднимет локальную панель и откроет её в окне.
4. При первом запуске войдите в opencode (кнопка входа) и привяжите ПК к серверу
   по коду из панели сервера.

Отдельного установщика нет — это переносимый один файл.

## Сервер (Linux)

1. Скачайте архив `OpenCodeSpend-server-linux-x64.tar.gz` из релиза.
2. Распакуйте его в выбранную папку.
3. Задайте переменные окружения:

   | Переменная | Назначение |
   | --- | --- |
   | `Spend__Mode=server` | режим работы: сервер |
   | `Spend__DatabasePath` | путь к файлу базы данных |
   | `Spend__TimeZone` | часовой пояс |
   | `Spend__GoogleClientId` / `Spend__GoogleClientSecret` | вход через Google |
   | `Spend__GitHubClientId` / `Spend__GitHubClientSecret` | вход через GitHub |
   | `Spend__PairingTtlMinutes` | время жизни кода привязки ПК, минуты |

4. Запустите сервер:

   ```bash
   ./OpenCodeSpend.Web
   ```

   Порт по умолчанию — `5199`, меняется через `ASPNETCORE_URLS`.

5. Снаружи сервер ставьте за reverse-proxy (Caddy или nginx). Для корректной
   работы живых событий на `/api/events` обязательно отключите буферизацию:

   ```nginx
   location /api/events {
       proxy_pass http://127.0.0.1:5199;
       proxy_buffering off;
   }
   ```

### systemd-юнит

Краткий пример `/etc/systemd/system/opencodespend.service`:

```ini
[Unit]
Description=OpenCode Spend server
After=network.target

[Service]
WorkingDirectory=/opt/opencodespend
ExecStart=/opt/opencodespend/OpenCodeSpend.Web
Restart=always
Environment=Spend__Mode=server
Environment=Spend__DatabasePath=/opt/opencodespend/data/spend.db
Environment=Spend__TimeZone=UTC

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now opencodespend
```

### Адреса возврата OAuth

В консолях Google и GitHub пропишите адреса возврата (замените `<домен>` на свой):

- `https://<домен>/signin-google`
- `https://<домен>/signin-github`

За подробностями обращайтесь к [README.md](../README.md).
