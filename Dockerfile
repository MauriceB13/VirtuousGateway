# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["VirtuousGateway.sln", "./"]
COPY ["src/VirtuousGateway.Api/VirtuousGateway.Api.csproj", "src/VirtuousGateway.Api/"]
COPY ["src/VirtuousGateway.Core/VirtuousGateway.Core.csproj", "src/VirtuousGateway.Core/"]
COPY ["src/VirtuousGateway.Infrastructure/VirtuousGateway.Infrastructure.csproj", "src/VirtuousGateway.Infrastructure/"]
COPY ["tests/VirtuousGateway.Tests/VirtuousGateway.Tests.csproj", "tests/VirtuousGateway.Tests/"]

RUN dotnet restore "VirtuousGateway.sln"

COPY . .

RUN dotnet publish "src/VirtuousGateway.Api/VirtuousGateway.Api.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_HTTP_PORTS=8080

COPY --from=build /app/publish .

EXPOSE 8080

USER app

ENTRYPOINT ["dotnet", "VirtuousGateway.Api.dll"]
