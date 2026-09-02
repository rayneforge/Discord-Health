# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /source

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/DiscordHealth.Runtime/DiscordHealth.Runtime.csproj src/DiscordHealth.Runtime/
RUN dotnet restore src/DiscordHealth.Runtime/DiscordHealth.Runtime.csproj

COPY src/DiscordHealth.Runtime/ src/DiscordHealth.Runtime/
RUN dotnet publish src/DiscordHealth.Runtime/DiscordHealth.Runtime.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM build AS test
COPY tests/DiscordHealth.Runtime.Tests/DiscordHealth.Runtime.Tests.csproj tests/DiscordHealth.Runtime.Tests/
RUN dotnet restore tests/DiscordHealth.Runtime.Tests/DiscordHealth.Runtime.Tests.csproj
COPY tests/DiscordHealth.Runtime.Tests/ tests/DiscordHealth.Runtime.Tests/
RUN dotnet test tests/DiscordHealth.Runtime.Tests/DiscordHealth.Runtime.Tests.csproj \
    --configuration Release \
    --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble AS final
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish ./
USER root
RUN mkdir -p /data && chown $APP_UID:$APP_UID /data
USER $APP_UID
ENTRYPOINT ["dotnet", "DiscordHealth.Runtime.dll"]
