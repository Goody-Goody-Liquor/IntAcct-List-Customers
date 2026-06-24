# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY TokenRefresh.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish TokenRefresh.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "TokenRefresh.dll"]
