FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
ARG VERSION=1.0.0
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG VERSION=1.0.0
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy project file and restore dependencies
COPY ["src/Fransync.Bridging.WebService/Fransync.Bridging.WebService.csproj", "src/Fransync.Bridging.WebService/"]
RUN dotnet restore "src/Fransync.Bridging.WebService/Fransync.Bridging.WebService.csproj"

# Copy source code
COPY . .
WORKDIR "/src/src/Fransync.Bridging.WebService"
RUN dotnet build "Fransync.Bridging.WebService.csproj" -c $BUILD_CONFIGURATION -o /app/build \
	/p:Version=${VERSION} \
	/p:AssemblyVersion=${VERSION} \
	/p:FileVersion=${VERSION} \
	/p:InformationalVersion=${VERSION} \
	/p:AssemblyTitle="Fransync Bridging Web Service" \
	/p:AssemblyDescription="File synchronization bridging service" \
	/p:AssemblyProduct="Fransync"

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
ARG VERSION=1.0.0
RUN dotnet publish "Fransync.Bridging.WebService.csproj" -c $BUILD_CONFIGURATION -o /app/publish \
	/p:Version=${VERSION} \
	/p:AssemblyVersion=${VERSION} \
	/p:FileVersion=${VERSION} \
	/p:InformationalVersion=${VERSION} \
	/p:AssemblyTitle="Fransync Bridging Web Service" \
	/p:AssemblyDescription="File synchronization bridging service" \
	/p:AssemblyProduct="Fransync"

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Fransync.Bridging.WebService.dll"]