FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY KartPaymentService.sln Directory.Build.props nuget.config ./
COPY packages/ packages/
COPY src/Api/KartPaymentService.Api.csproj src/Api/
COPY src/Application/KartPaymentService.Application.csproj src/Application/
COPY src/Domain/KartPaymentService.Domain.csproj src/Domain/
COPY src/Infrastructure/KartPaymentService.Infrastructure.csproj src/Infrastructure/
RUN dotnet restore src/Api/KartPaymentService.Api.csproj

COPY src/ src/
COPY contracts/ contracts/
# Deliberately not --no-restore: some transitive packages (e.g. MongoDB.Driver's AWS-auth
# dependency) resolve differently between a bare `restore` and `publish`'s own RID-aware graph,
# so relying on --no-restore here has been observed to fail with NETSDK1064 even though the
# earlier restore step reported success. The earlier restore still warms the Docker layer cache;
# this just doesn't treat it as sufficient on its own.
RUN dotnet publish src/Api/KartPaymentService.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "KartPaymentService.Api.dll"]
