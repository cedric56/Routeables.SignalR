# Routeables.Remote

The Routeables Remote Protocol (RRP, [PROTOCOL-REMOTE.md](https://github.com/cedric56/Routeables.SignalR/blob/master/PROTOCOL-REMOTE.md)):
documents replicated between a server and clients that write offline, restart, reconnect and converge, without a write lost
or applied twice. No dependency; Native AOT compatible.

```csharp
// Client
var replica = await Replica.OpenAsync(storage, ["Person", "Note"]);
var ana = new DocumentId("Person", "ana");
await replica.Edit().Create(ana).Set(ana, "name", "\"Ana\"").Increment(ana, "visits", 1).CommitAsync();
await replica.Edit().ReplaceText(note, "body", textBox.Text).CommitAsync();   // concurrent typing merges
replica.Find(ana);                       // visible at once, offline
await replica.SyncAsync(channel);        // when the network is back; call again after a failure
replica.Rejected;                        // what the server refused, for the UI

// Server
var server = new ReplicationServer(policy, store: store);   // IReplicationPolicy, IReplicationStore (memory by default)
IReplicationChannel channel = server.Connect(user);         // in process; Routeables.SignalR serves it over a hub
```

Fields: last-writer-wins registers, add-wins sets, counters, collaborative text (RGA), and the existence of the document.
The SignalR hub is in `Routeables.SignalR` (`MapRouteablesReplication`); the TypeScript and Dart replicas are in
`@routeables/client` and `routeables_client`; a durable store over EF Core or MongoDB is `Queryeables.Replication`.
