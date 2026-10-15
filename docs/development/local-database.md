# Local database

Local development uses one committed Postgres compose file, `compose.yaml` at the repository root.

- The image is pinned to `postgres:18`, the major version production runs (the CloudNativePG default image, 18.6). CI and the Testcontainers tests still use Postgres 17 for now.
- The compose project is named `mercurius`, so every clone and every extra working tree resolves to the same project, container (`mercurius-postgres`) and volume (`mercurius_postgres_data`), whatever the folder is called.
- The volume is mounted at `/var/lib/postgresql`, the Postgres 18 recommended layout, so the data lives in `18/docker` inside the volume. Mounting a volume at the pre-18 path `/var/lib/postgresql/data` is exactly what made Postgres 18 refuse to start.
- Visual Studio no longer generates its own untagged `postgres` container from a Service Dependency, so opening a new clone or working tree does not start or recreate a database.
- Published ports bind to `127.0.0.1` only, so the passwordless (`trust`) database and Adminer are not reachable from the network.

## Start the database

From any clone or working tree:

```sh
docker compose up -d
```

The container uses `restart: unless-stopped`, so it keeps running across reboots until you stop it. Start it once; Visual Studio no longer starts it on F5.

Adminer (a web UI on <http://localhost:8080>) is opt-in:

```sh
docker compose --profile tools up -d
```

## Connection string

Put this in your user secrets (the database uses trust authentication, so there is no password):

```sh
dotnet user-secrets set "ConnectionStrings:MercuriusDB" "Host=localhost;Port=5432;Database=postgres;Username=postgres" --project src/MercuriusAPI/Mercurius.LAN.API.csproj
```

## Migrating from the old Visual Studio container

Earlier clones used a Visual Studio generated container `vs-postgresql-1` with volume `vs_postgresql_data`. That volume is already Postgres 18 with the data directory at its root, so no dump or restore is needed: copy it into the new volume at file level.

1. Stop both database containers so nothing writes during the copy: `docker compose stop postgres` and `docker stop vs-postgresql-1`. If the old container is crash looping you can skip its stop; Postgres runs crash recovery on first start.
2. Remove the old containers: `docker rm -f vs-postgresql-1 vs-adminer-1`.
3. Copy the data into the `18/docker` directory of the new volume. The command checks that the old volume exists and is Postgres 18 and that `mercurius_postgres_data` is completely empty, hidden files included, before writing anything; it refuses and changes nothing otherwise:

   ```sh
   docker volume inspect vs_postgresql_data >/dev/null && MSYS_NO_PATHCONV=1 docker run --rm \
     --mount type=volume,src=vs_postgresql_data,dst=/from,readonly \
     --mount type=volume,src=mercurius_postgres_data,dst=/to \
     alpine sh -euc 'test "$(cat /from/PG_VERSION)" = 18 && test -z "$(ls -A /to)" && mkdir -p /to/18/docker && cp -a /from/. /to/18/docker/'
   ```

4. Start the new database: `docker compose up -d`.
5. Once it works, optionally remove the old volume: `docker volume rm vs_postgresql_data`.

If the copy refuses, either `mercurius_postgres_data` already holds a cluster from an earlier `docker compose up`, or the old volume is missing or not Postgres 18. Back up any destination data you need before deliberately removing the destination volume; the reset below removes it, and nothing here deletes a volume for you.

If the old volume is from Postgres 17 or older (its `PG_VERSION` file holds 17 or lower), the file copy does not work. Dump with that major version's image and restore into the new container instead (`pg_dumpall`, then `docker exec -i mercurius-postgres psql -U postgres < dump.sql`), or skip the data and let the application migrations recreate the schema.

## Reset the database

This deletes all local data in the shared volume `mercurius_postgres_data`:

```sh
docker compose down -v
docker compose up -d
```

Use `docker compose down` (without `-v`) to stop and remove the container while keeping the data. To empty the volume for the migration copy above, run `docker compose down -v` and skip the `docker compose up -d` until after the copy.
