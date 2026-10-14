# Project files only, so the restore layer below stays cached until a csproj changes.
FROM mcr.microsoft.com/dotnet/sdk:10.0-azurelinux3.0 AS projects
WORKDIR /src
COPY ["src", "src/"]
RUN find src -type f ! -name '*.csproj' -delete

FROM mcr.microsoft.com/dotnet/sdk:10.0-azurelinux3.0 AS build

ARG BUILD_CONFIGURATION=Release

WORKDIR /src

COPY ["global.json", "./"]
COPY --from=projects /src ./

RUN dotnet restore \
    "./src/MercuriusAPI/Mercurius.LAN.API.csproj"

COPY ["src", "src/"]

RUN dotnet publish \
    "./src/MercuriusAPI/Mercurius.LAN.API.csproj" \
    --no-restore \
    --configuration $BUILD_CONFIGURATION \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-azurelinux3.0 AS run
RUN tdnf upgrade -y pcre2 \
    && tdnf clean all
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Mercurius.LAN.API.dll"]
