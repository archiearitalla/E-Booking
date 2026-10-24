# VeskaFlow web app. Build: docker build -t barberbook .   Run: see docs/DEPLOY.md
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first so this layer is cached until a project file changes.
COPY src/BarberBook.Core/BarberBook.Core.csproj src/BarberBook.Core/
COPY src/BarberBook.Web/BarberBook.Web.csproj src/BarberBook.Web/
RUN dotnet restore src/BarberBook.Web/BarberBook.Web.csproj

COPY src/ src/
RUN dotnet publish src/BarberBook.Web/BarberBook.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID

# Hosts such as Render pass the port to listen on in PORT; default 8080 elsewhere.
# The connection string comes from ConnectionStrings__Default at run time. Nothing secret is baked into the image.
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080} exec dotnet BarberBook.Web.dll"]
