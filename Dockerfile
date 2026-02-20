# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY WebApplication2.csproj ./
RUN dotnet restore WebApplication2.csproj

COPY . .
RUN dotnet publish WebApplication2.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV PORT=8080

COPY --from=build /app/publish ./

EXPOSE 8080

# Railway injects PORT at runtime. Fallback stays at 8080.
CMD ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080} dotnet WebApplication2.dll"]
