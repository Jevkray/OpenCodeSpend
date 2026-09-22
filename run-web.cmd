@echo off
echo OpenCode Spend (web) -> http://127.0.0.1:5199
dotnet run --project "%~dp0src\OpenCodeSpend.Web\OpenCodeSpend.Web.csproj" --urls http://127.0.0.1:5199
