FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY apps/ReceiptReader.Api/ReceiptReader.Api.csproj apps/ReceiptReader.Api/
RUN dotnet restore apps/ReceiptReader.Api/ReceiptReader.Api.csproj

COPY apps/ReceiptReader.Api/ apps/ReceiptReader.Api/
WORKDIR /src/apps/ReceiptReader.Api
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish ./
RUN mkdir -p /app/AppData/uploads
EXPOSE 8080

ENTRYPOINT ["dotnet", "ReceiptReader.Api.dll"]
