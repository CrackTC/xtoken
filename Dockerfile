FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /source
RUN apk add gcc zlib-dev musl-dev

COPY ./xtoken.csproj .
RUN dotnet restore

COPY ./Program.cs .
RUN dotnet publish -c Release -o /app -r linux-musl-x64

FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine
EXPOSE 80
COPY --from=build /app /app
WORKDIR /app
ENTRYPOINT ["./xtoken"]
