# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG VERSION=1.0.0
WORKDIR /src

# Copy project files and restore dependencies
COPY ["src/Fransync.Bridging.WebService/Fransync.Bridging.WebService.csproj", "src/Fransync.Bridging.WebService/"]
RUN dotnet restore "src/Fransync.Bridging.WebService/Fransync.Bridging.WebService.csproj"

# Copy source code
COPY . .
WORKDIR "/src/src/Fransync.Bridging.WebService"

# Build and publish the application
RUN dotnet build "Fransync.Bridging.WebService.csproj" -c Release -o /app/build \
    /p:Version=${VERSION} \
    /p:AssemblyVersion=${VERSION} \
    /p:FileVersion=${VERSION} \
    /p:InformationalVersion=${VERSION}

RUN dotnet publish "Fransync.Bridging.WebService.csproj" -c Release -o /app/publish \
    /p:UseAppHost=false \
    /p:Version=${VERSION} \
    /p:AssemblyVersion=${VERSION} \
    /p:FileVersion=${VERSION} \
    /p:InformationalVersion=${VERSION}

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Create non-root user for security
RUN adduser --disabled-password --gecos "" --uid 1001 appuser

# Copy published application
COPY --from=build /app/publish .

# Set ownership and permissions
RUN chown -R appuser:appuser /app
USER appuser

# Configure ASP.NET Core
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

# Expose port
EXPOSE 8080

# Entry point
ENTRYPOINT ["dotnet", "Fransync.Bridging.WebService.dll"]