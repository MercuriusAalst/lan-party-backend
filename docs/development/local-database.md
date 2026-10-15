# Local database

Local development uses one committed Postgres compose file, `compose.yaml` at the repository root.

- The image is pinned to `postgres:17`, the same major version as production, CI and the Testcontainers tests.
- The compose project is named `mercurius`, so every clone and every extra working tree resolves to the same project, container (`mercurius-postgres`) and volume (`mercurius_postgres_data`), whatever the folder is called.
- Visual Studio no longer generates its own untagged `postgres` container from a Service Dependency, so opening a new clone or working tree does not start or recreate a database.

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

Earlier clones used a Visual Studio generated container `vs-postgresql-1` with volume `vs_postgresql_data`, running an untagged (now Postgres 18) image. Postgres 17 cannot read that data directory, and both containers want port 5432, so:

1. Optional, keep the data: dump it with Postgres 18 while the old container is still running.

   ```sh
   docker exec vs-postgresql-1 pg_dumpall -U postgres > old-dump.sql
   ```

   If the old container is stopped or crash looping, start a throwaway Postgres 18 against the old volume instead. The volume is mounted at the Postgres 18 data directory (`/var/lib/postgresql/18/docker`) because the old volume holds the data directory at its root; mounted anywhere else, Postgres would initialise a fresh empty cluster and the dump would be empty. Wait until the server accepts connections before dumping, since crash recovery on that volume can take minutes:

   ```sh
   docker run --rm -d --name old-pg -e POSTGRES_HOST_AUTH_METHOD=trust -v vs_postgresql_data:/var/lib/postgresql/18/docker postgres:18
   until docker exec old-pg pg_isready -U postgres; do sleep 2; done
   docker exec old-pg pg_dumpall -U postgres > old-dump.sql
   docker stop old-pg
   ```

2. Stop and remove the old container: `docker rm -f vs-postgresql-1` (and the old Adminer, `docker rm -f vs-adminer-1`, if it exists).
3. Start the new database: `docker compose up -d`.
4. Optional, restore the dump: `docker exec -i mercurius-postgres psql -U postgres < old-dump.sql`. Alternatively skip the dump and let the application migrations recreate the schema.
5. Once you are sure you no longer need the old data: `docker volume rm vs_postgresql_data`.

## Reset the database

This deletes all local data in the shared volume `mercurius_postgres_data`:

```sh
docker compose down -v
docker compose up -d
```

Use `docker compose down` (without `-v`) to stop and remove the container while keeping the data.
