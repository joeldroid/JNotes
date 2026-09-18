FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first so `dotnet restore` (and its NuGet package downloads, the slow part)
# is cached independently of source changes.
COPY JNotes.slnx Directory.Build.props global.json .
COPY src/JNotes.Web/JNotes.Web.csproj src/JNotes.Web/
COPY src/JNotes.Core/JNotes.Core.csproj src/JNotes.Core/
COPY tests/JNotes.Tests/JNotes.Tests.csproj tests/JNotes.Tests/
RUN dotnet restore src/JNotes.Web/JNotes.Web.csproj

# NOT --no-restore: publish needs to re-resolve the static web assets manifest (Blazor's
# _framework/*.js, wwwroot files, scoped CSS) against the actual source now that it's present —
# the csproj-only restore above ran before wwwroot/Styles/etc. existed, so its cached manifest is
# incomplete and --no-restore here would publish from that stale manifest, silently 404ing on
# _framework/blazor.web.js and other static assets at runtime. The NuGet packages restored above
# are still cache-warm, so this re-restore is cheap — no packages are re-downloaded.
COPY src/ src/
RUN dotnet publish src/JNotes.Web/JNotes.Web.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV NOTES_PATH=/data/notes
ENV DB_PATH=/data/jnotes.db
# IANA time zone id (e.g. America/New_York) used to display note timestamps — storage stays UTC
# regardless. tzdata is already present in this base image, so any IANA id resolves correctly.
ENV TZ=UTC
# How often the background pass reconciles every user's note index against the files on disk, in
# minutes. 0 disables it (the startup pass and Settings -> "Rescan Now" still work).
ENV AUTO_INDEX_INTERVAL_MINUTES=5

# The base image's default non-root user needs to own the mounted volume's mount point;
# actual ownership of bind-mounted host content still depends on the host directory's
# permissions (see README for a chown workaround if the container can't write to ./data).
RUN mkdir -p /data/notes && chown -R $APP_UID /data
USER $APP_UID

VOLUME ["/data"]
EXPOSE 8080
ENTRYPOINT ["dotnet", "JNotes.Web.dll"]
