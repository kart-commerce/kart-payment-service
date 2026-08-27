# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY KartPaymentService.sln Directory.Build.props nuget.config ./
COPY packages/ packages/
COPY src/Api/KartPaymentService.Api.csproj src/Api/
COPY src/Application/KartPaymentService.Application.csproj src/Application/
COPY src/Domain/KartPaymentService.Domain.csproj src/Domain/
COPY src/Infrastructure/KartPaymentService.Infrastructure.csproj src/Infrastructure/
# The cache mount persists extracted NuGet packages under a stable id shared by every other
# kart-*-service Dockerfile, so restore stays fast (no re-download) even on a cache-miss here
# (e.g. after a .csproj change) as long as some other service's build already warmed it.
RUN --mount=type=cache,target=/root/.nuget/packages,id=nuget-packages \
    dotnet restore src/Api/KartPaymentService.Api.csproj

COPY src/ src/
COPY contracts/ contracts/
# Deliberately not --no-restore: some transitive packages (e.g. MongoDB.Driver's AWS-auth
# dependency) resolve differently between a bare `restore` and `publish`'s own RID-aware graph,
# so relying on --no-restore here has been observed to fail with NETSDK1064 even though the
# earlier restore step reported success. The earlier restore still warms the Docker layer cache;
# this just doesn't treat it as sufficient on its own. It performs its own restore, so it gets
# the same cache mount as the restore step above.
RUN --mount=type=cache,target=/root/.nuget/packages,id=nuget-packages \
    dotnet publish src/Api/KartPaymentService.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "KartPaymentService.Api.dll"]
