FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /source
RUN apk add gcc zlib-dev musl-dev

COPY ./xtoken.csproj .
RUN dotnet restore

COPY ./Program.cs .
RUN dotnet publish -c Release -o /app -r linux-musl-x64

FROM alpine
EXPOSE 80
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
COPY --from=build /app/xtoken /app/
WORKDIR /app
CMD [ "./xtoken" ]
