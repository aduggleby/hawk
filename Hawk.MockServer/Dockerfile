## <file>
## <summary>
## Dockerfile for Hawk.MockServer (deterministic dependency mock).
## </summary>
## </file>

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Hawk.MockServer/Hawk.MockServer.csproj Hawk.MockServer/
RUN dotnet restore Hawk.MockServer/Hawk.MockServer.csproj

COPY . .
RUN dotnet publish Hawk.MockServer/Hawk.MockServer.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8081
EXPOSE 8081
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Hawk.MockServer.dll"]
