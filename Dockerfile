# Stage 1 — Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files first for layer caching
COPY src/KanAuth.Domain/KanAuth.Domain.csproj           src/KanAuth.Domain/
COPY src/KanAuth.Application/KanAuth.Application.csproj src/KanAuth.Application/
COPY src/KanAuth.Infrastructure/KanAuth.Infrastructure.csproj src/KanAuth.Infrastructure/
COPY src/KanAuth.API/KanAuth.API.csproj                 src/KanAuth.API/

RUN dotnet restore src/KanAuth.API/KanAuth.API.csproj

# Copy source and publish
COPY . .
RUN dotnet publish src/KanAuth.API/KanAuth.API.csproj \
    -c Release -o /app/publish --no-restore

# Stage 2 — Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Non-root user
RUN groupadd -r appgroup && useradd -r -g appgroup appuser
USER appuser

COPY --from=build /app/publish .

EXPOSE 80
ENV ASPNETCORE_HTTP_PORTS=80

ENTRYPOINT ["dotnet", "KanAuth.API.dll", "--migrate"]
