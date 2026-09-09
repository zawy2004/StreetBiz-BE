FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["Directory.Build.props", "."]
COPY ["src/StreetBiz.Domain/StreetBiz.Domain.csproj", "src/StreetBiz.Domain/"]
COPY ["src/StreetBiz.Application/StreetBiz.Application.csproj", "src/StreetBiz.Application/"]
COPY ["src/StreetBiz.Infrastructure/StreetBiz.Infrastructure.csproj", "src/StreetBiz.Infrastructure/"]
COPY ["src/StreetBiz.API/StreetBiz.API.csproj", "src/StreetBiz.API/"]
RUN dotnet restore "src/StreetBiz.API/StreetBiz.API.csproj"

COPY . .
RUN dotnet publish "src/StreetBiz.API/StreetBiz.API.csproj" +    --configuration Release +    --output /app/publish +    --no-restore +    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "StreetBiz.API.dll"]
