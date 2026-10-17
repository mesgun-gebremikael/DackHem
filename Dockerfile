# Build from the repository root.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY DackHem.Api/DackHem.Api.csproj DackHem.Api/
RUN dotnet restore DackHem.Api/DackHem.Api.csproj
COPY DackHem.Api/ DackHem.Api/
RUN dotnet publish DackHem.Api/DackHem.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_NOLOGO=true
# Railway supplies PORT. Never put SQLite or secret keys in the image.
CMD ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080} exec dotnet DackHem.Api.dll"]
