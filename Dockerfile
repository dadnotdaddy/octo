# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY octo.sln .
COPY octo/octo.csproj octo/
COPY octo.Tests/octo.Tests.csproj octo.Tests/

RUN dotnet restore

COPY octo/ octo/
COPY octo.Tests/ octo.Tests/

# ReadyToRun: precompiled IL, so the first requests after a restart skip JIT.
RUN dotnet publish octo/octo.csproj -c Release -o /app/publish -r linux-x64 --self-contained false -p:PublishReadyToRun=true

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

RUN mkdir -p /app/downloads

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "octo.dll"]
