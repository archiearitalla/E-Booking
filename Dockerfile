# VeskaFlow web app. Build: docker build -t veskaflow .   Run: see docs/DEPLOY.md
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first so this layer is cached until a project file changes.
COPY src/VeskaFlow.Core/VeskaFlow.Core.csproj src/VeskaFlow.Core/
COPY src/VeskaFlow.Web/VeskaFlow.Web.csproj src/VeskaFlow.Web/
RUN dotnet restore src/VeskaFlow.Web/VeskaFlow.Web.csproj

COPY src/ src/
RUN dotnet publish src/VeskaFlow.Web/VeskaFlow.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID

# Hosts such as Render pass the port to listen on in PORT; default 8080 elsewhere.
# The connection string comes from ConnectionStrings__Default at run time. Nothing secret is baked into the image.
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080} exec dotnet VeskaFlow.Web.dll"]
