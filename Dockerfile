FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["Directory.Build.props", "."]
COPY ["src/StreetBiz.Domain/StreetBiz.Domain.csproj", "src/StreetBiz.Domain/"]
COPY ["src/StreetBiz.Application/StreetBiz.Application.csproj", "src/StreetBiz.Application/"]
COPY ["src/StreetBiz.Infrastructure/StreetBiz.Infrastructure.csproj", "src/StreetBiz.Infrastructure/"]
COPY ["src/StreetBiz.API/StreetBiz.API.csproj", "src/StreetBiz.API/"]
RUN dotnet restore "src/StreetBiz.API/StreetBiz.API.csproj"

COPY . .
RUN dotnet publish "src/StreetBiz.API/StreetBiz.API.csproj" \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
COPY --from=build /app/publish .
# Run as the image's unprivileged "app" user (APP_UID 1654), not root. Only the uploads folder is
# writable; a fresh Docker volume mounted there copies this ownership. An existing volume created
# while the API ran as root must be chowned once (docs/deploy-vps.md, "Chạy API không bằng root").
RUN mkdir -p /app/App_Data/uploads && chown -R $APP_UID:$APP_UID /app/App_Data
USER $APP_UID
ENTRYPOINT ["dotnet", "StreetBiz.API.dll"]
