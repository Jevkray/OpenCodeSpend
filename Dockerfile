FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/OpenCodeSpend.Web/OpenCodeSpend.Web.csproj src/OpenCodeSpend.Web/
RUN dotnet restore src/OpenCodeSpend.Web/OpenCodeSpend.Web.csproj
COPY src/OpenCodeSpend.Web/ src/OpenCodeSpend.Web/
RUN dotnet publish src/OpenCodeSpend.Web/OpenCodeSpend.Web.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:5199
EXPOSE 5199
ENTRYPOINT ["dotnet", "OpenCodeSpend.Web.dll"]
