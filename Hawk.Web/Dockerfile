## <file>
## <summary>
## Dockerfile for Hawk.Web (Razor Pages app).
## Builds and publishes the app; database migrations are applied on startup by IdentitySeeder.
## </summary>
## </file>

FROM node:20-alpine AS assets
WORKDIR /src/Hawk.Web

COPY Hawk.Web/package.json Hawk.Web/package-lock.json ./
RUN npm ci

COPY Hawk.Web/Assets ./Assets
COPY Hawk.Web/tailwind.config.js ./tailwind.config.js
COPY Hawk.Web/Pages ./Pages
COPY Hawk.Web/Areas ./Areas
COPY Hawk.Web/Services ./Services
COPY Hawk.Web/Data ./Data
RUN mkdir -p ./wwwroot/css
RUN npm run build:css

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Hawk.Web/Hawk.Web.csproj Hawk.Web/
RUN dotnet restore Hawk.Web/Hawk.Web.csproj

COPY . .
COPY --from=assets /src/Hawk.Web/wwwroot/css/app.css Hawk.Web/wwwroot/css/app.css
RUN dotnet publish Hawk.Web/Hawk.Web.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# Bind explicitly to IPv4 in containers to keep inter-container connectivity predictable.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

COPY --from=build /app/publish .

# Write logs to /app/logs by default (volume mounted in docker-compose).
RUN mkdir -p /app/logs

ENTRYPOINT ["dotnet", "Hawk.Web.dll"]
