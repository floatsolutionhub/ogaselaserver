FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props .
COPY Ogasela.slnx .
COPY src/Ogasela.Domain/Ogasela.Domain.csproj src/Ogasela.Domain/
COPY src/Ogasela.Application/Ogasela.Application.csproj src/Ogasela.Application/
COPY src/Ogasela.Infrastructure/Ogasela.Infrastructure.csproj src/Ogasela.Infrastructure/
COPY src/Ogasela.Shared/Ogasela.Shared.csproj src/Ogasela.Shared/
COPY src/Ogasela.Api/Ogasela.Api.csproj src/Ogasela.Api/
RUN dotnet restore src/Ogasela.Api/Ogasela.Api.csproj
COPY src/ src/
RUN dotnet publish src/Ogasela.Api/Ogasela.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Ogasela.Api.dll"]