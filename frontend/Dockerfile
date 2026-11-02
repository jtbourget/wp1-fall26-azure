FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY ["frontend.csproj", "./"]
RUN dotnet restore "frontend.csproj"

# Copy source and publish release build
COPY . .
RUN dotnet publish "frontend.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime container
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "frontend.dll"]
