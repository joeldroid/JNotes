FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first so `dotnet restore` is cached independently of source changes.
COPY JNotes.slnx .
COPY src/JNotes.Web/JNotes.Web.csproj src/JNotes.Web/
COPY src/JNotes.Core/JNotes.Core.csproj src/JNotes.Core/
COPY tests/JNotes.Tests/JNotes.Tests.csproj tests/JNotes.Tests/
RUN dotnet restore src/JNotes.Web/JNotes.Web.csproj

COPY src/ src/
RUN dotnet publish src/JNotes.Web/JNotes.Web.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV JNotes__NotesFolderPath=/data/notes
ENV JNotes__DbPath=/data/jnotes.db

# The base image's default non-root user needs to own the mounted volume's mount point;
# actual ownership of bind-mounted host content still depends on the host directory's
# permissions (see README for a chown workaround if the container can't write to ./data).
RUN mkdir -p /data/notes && chown -R $APP_UID /data
USER $APP_UID

VOLUME ["/data"]
EXPOSE 8080
ENTRYPOINT ["dotnet", "JNotes.Web.dll"]
