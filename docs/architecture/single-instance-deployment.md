# Single-Instance Deployment

The back-end runs as exactly one replica. This is a deliberate decision, not an oversight: several parts of the host keep state in the process or on a node-local disk. Do not raise the replica count until the items under "Scaling out" are in place.

## Why one replica

- **Media lives on a ReadWriteOnce volume.** `FileSystemMediaModule` writes uploads to `FileStorage:Location` and Imageflow serves `/images` from the same directory. A second pod would either fail to mount the volume or, on another node, see a different disk.
- **Realtime state is in memory.** `SignalRRealtimeConnectionManager` tracks connections, user-to-connection maps and access gates in process memory, and SignalR runs without a backplane. A team event published on one pod never reaches clients connected to another.
- **Rate limits are per process.** The global fixed-window limiter and `TeamManagementHubInvocationRateLimitFilter` keep their partitions in memory, so each replica would grant its own full budget.
- **Migrations run on startup by default.** `Database:ApplyMigrationsOnStartup` (default `true`) makes every starting pod run `Database.Migrate()`; concurrent pods would race on the migration history.

The outbox dispatcher and the Discovery projection rebuild are already safe with more than one instance: outbox messages are claimed with a lease token before dispatch, and rebuild jobs are coordinated through the database.

## Scaling out

Before running more than one replica:

1. Move media to shared storage: a ReadWriteMany volume, or object storage behind `IMediaModule` with a matching Imageflow blob provider.
2. Add a SignalR backplane (for example Redis or Azure SignalR Service) and replace the in-memory connection manager with one whose connection and access-gate state is shared or tolerant of multiple nodes. Use sticky sessions or WebSockets-only transport for negotiation.
3. Accept per-replica rate-limit budgets (divide the configured limits by the replica count) or move limiting to the ingress or a distributed store.
4. Run migrations once per release (for example a pre-sync Job) and set `Database:ApplyMigrationsOnStartup=false` on the application pods.
