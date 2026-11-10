# Routeables Remote Protocol (RRP) — version 1

RRP replicates documents between a server and clients that work offline. A client writes locally at any time, survives
restarts, and converges with every other client once it reaches the server again, without losing a legitimate write and
without applying one twice. The server stays the authority: it decides what enters the history and what each user may read.

This document is normative. The implementations, and what checks them:

| Part | Where | Checked by |
| --- | --- | --- |
| Merge, client replica, server, in-memory store | `src/Routeables.Remote` (.NET, no dependency, Native AOT) | `tests/Routeables.Remote.Test` |
| SignalR hub (`MapRouteablesReplication`) | `src/Routeables.SignalR/Replication.cs` | `tests/e2e/replication.test.mjs` (real Kestrel and SignalR, the TypeScript replica) |
| TypeScript replica, SignalR channel | `clients/typescript/src/remote*.ts` (`@routeables/client`) | `clients/typescript/test/remote.test.ts`, the e2e tests |
| Dart replica, SignalR channel | `clients/dart/lib/src/remote*.dart` (`routeables_client`) | `clients/dart/test/remote_test.dart` |
| Durable store (EF Core, MongoDB, Dommel, PostgreSQL) | `Queryeables.Replication` (Queryeables repository) | `ReplicationStoreContractTests` there |
| One merge in three languages | `clients/fixtures/remote-v1.json`: a log written by .NET, and what each document must show | the three test suites |

## 1. Scope, and what RRP is not

| Layer | What it carries | Where it lives |
|---|---|---|
| **Session** (navigation, view state, commands) | `Hello/Welcome`, `Navigated`, `Changed`, `Resync`, `Invoke`… | Routeables.SignalR, protocol 2, hub `/routeables` |
| **Replication** (data written offline) | `Hello`, `Push`, `Pull`, `Snapshot`, `LogAdvanced` | **RRP** (this document), hub `/replication` |
| **Query** (reading data online) | LINQ over a source | Queryeables |

RRP does not resume a UI session: protocol 2 of Routeables.SignalR already does (`Hello.Resume`, generations, `Resync`).
RRP does not replace queries: an online application keeps querying; RRP is for the data that must stay writable without
the server. The two hubs are separate endpoints, with their own authorization.

Topology: a **star**. Clients talk to one logical server, never to each other. That choice keeps RRP small: the server
totally orders the history, so a client needs one cursor, not a version vector of the whole system.

## 2. Identities

| Name | Definition | Rule |
|---|---|---|
| `ReplicaId` | A GUID made by the client the first time it opens its storage; on the wire its text in lower case. | Bound to the first user that uses it on the server; never shared by two users or two devices. Replicas are **ordered by that text, ordinally**: the same order in .NET, TypeScript and Dart. |
| `Dot` | `(ReplicaId, Seq)`, `Seq` = 1, 2, 3… with no gap. | Identifies a change for ever. Never reused, even for a change the server refused. |
| `Hlc` | Hybrid logical clock `(Wall ms, Counter, ReplicaId)` (Kulkarni et al., 2014). | Total order: wall, then counter, then replica. Every replica observes the clock of the server at `Hello`, and the server observes every change it admits. |
| `OpStamp` | `(Hlc of the change, index of the operation in the change)`. | Orders two writes of one field, even inside one change. |
| `DocumentId` | `(Collection, Key)`. | The key is chosen by the client (a GUID or a natural key); a key chosen offline by two clients is one document, merged. |
| Position | `1, 2, 3…` in the log of the server. | The cursor of a client is the last position it considered. |

## 3. Data model: one document, four kinds of fields

A document is a set of named fields. Values are opaque strings (JSON in practice): RRP merges fields, not values.

| Kind | Operations | Merge | Reason |
|---|---|---|---|
| Register (LWW) | `set {field, value}` | The greatest `OpStamp` wins. | A name, a status, a date: one value, the latest. |
| Set (add-wins OR-Set) | `add {field, element}` / `remove {field, element, observed}` | An element is present while it keeps a tag. `add` gives the tag `(dot, index)`; `remove` deletes only the tags it **observed**. | A concurrent add the remover did not see survives. |
| Counter (PN) | `increment {field, delta}` | Sum of the deltas. | Stock, visits, votes. Exactly-once (§5) means a delta is never counted twice. |
| Text (RGA) | `insert {field, after, text}` / `erase {field, spans}` | Each character has an identity `(dot, index, offset)`. An insertion goes right after its anchor `after` (the start when null), past the characters inserted there by later insertions; an erasure marks characters, by identity, as erased (they stay as tombstones). | Two people typing in one field both keep what they typed; text typed inside a passage erased concurrently survives; concurrent insertions at one place never interleave, the latest first. |
| Existence | `restore` / `delete` | A register: the greatest `OpStamp` decides. | Deleting is a tombstone; fields are kept, so a later restore wins back the document. |

Text positions count **UTF-16 code units**, as .NET, JavaScript and Dart do. `ReplaceText(field, value)` (the call a text
box makes) sends the smallest edit (common start and end kept, never splitting a surrogate pair), so that concurrent edits
elsewhere in the text survive. An insertion whose anchor is missing (its insertion was refused, the same on every replica)
goes to the start.

Every merge is commutative for concurrent operations and idempotent per dot; changes are applied in the order of the log,
which respects each replica's order and causality (an operation is always built from what its replica had seen).
Replicas that applied the same log hold the same state (tests: `ConvergenceTests`, `TextTests`, the fixture).

A **change** is `{dot, stamp, ops[]}`: one or more operations on one or more documents. It is atomic: the server admits
or refuses it whole. Each operation of a change is built from what the replica sees **with the operations before it in
the same change**: a change may create a text and edit it, add an element and remove it.

## 4. Messages

JSON, camelCase (`RemoteJsonContext` in .NET, source generated). Operations are tagged by `"op"`: `set`, `add`, `remove`,
`increment`, `delete`, `restore`, `insert`, `erase`.

```
RemoteHello   { protocol: 1, replica, clock }
RemoteWelcome { success, protocol, clock, lastSeq, maxChangesPerPush, error? }

PushRequest   { changes: Change[] }                         // in seq order, from lastSeq + 1
PushResult    { acks: { dot, status: 1 accepted | 2 rejected | 3 duplicate, position, code?, message? }[] }

PullRequest   { collections[], since, max }
PullResult    { entries: { position, change }[], position, more, snapshotRequired }

SnapshotRequest { collections[] }
SnapshotResult  { documents: DocumentSnapshot[], position }

LogAdvanced   { collections[], position }                  // server → client, a hint only

Change        { dot: {replica, seq}, stamp: {wall, counter, replica}, ops: { document: {collection, key}, op }[] }
DocumentSnapshot { id, exists, existenceStamp, registers[], elements[], counters[], texts: { field, runs: { start, stamp, text, erased }[] }[] }
```

Over SignalR the hub methods are `Hello`, `Push`, `Pull`, `Snapshot`, each with one argument, and the client method
`LogAdvanced`. Over HTTP they map to four POSTs. RRP needs no session: every call carries what it needs.

## 5. Synchronization

```
client                                           server
  ── Hello {replica, clock} ────────────────────▶  checks version and owner of the replica
  ◀──────────── Welcome {clock, lastSeq} ───────   observes the clock if it is not too far ahead
  ── Push [changes with seq > lastSeq] ─────────▶  for each, in order: dedupe, limits, policy,
  ◀───────────────────────── PushResult acks ────   one transaction: entry, documents, replica
  ── Pull {since: cursor} ──────────────────────▶  entries after `since`, filtered for this user
  ◀─────────────── PullResult {entries, position, more}
  (more → pull again; snapshotRequired → Snapshot)
```

### 5.1 Client state

A replica stores, after every step: the **confirmed** documents (only from pulls or a snapshot) and the last seq applied
per replica; the **pending** changes, each with the position the server gave it (0 = not decided yet); the **rejected**
changes until the application dismisses them; the cursor, the next seq, the clock, the collections.

What the application reads is **confirmed + pending replayed on top**: a local write is visible at once, offline; a
refused change disappears exactly, every operation of it; a restart shows what was shown before.

### 5.2 Push: exactly once

1. The client sends its pending changes not yet decided, in seq order, at most `maxChangesPerPush`.
2. The server decides one change at a time. `seq <= lastSeq`: **duplicate**, the remembered answer is sent again.
   `seq > lastSeq + 1`: rejected `out-of-order`. Otherwise the limits (§7), then `IReplicationPolicy.AdmitAsync`, which
   sees the documents as stored. Accepted: the entry (next position), the merged documents, the replica (its `lastSeq`
   and answer) are written **in one transaction** (`IReplicationStore.CommitAsync`). Refused: only the replica.
3. An accepted change stays pending, with its position, until a pull brings it back; it is then confirmed.

### 5.3 Pull: one cursor

The server answers the entries after `since`, up to its current position, that touch the requested collections, each
**reduced to the operations the user may read** (`CanRead`), and the new `position`: every position up to it was
considered, sent or not. The client applies each entry (once per dot), removes its own change of that dot from pending,
then removes the pending changes whose position is `<= position`.

### 5.4 Snapshot: below the horizon

A server may compact its log up to a position (the horizon). A pull from before it answers `snapshotRequired`. The
snapshot holds the documents the user may read, merged up to `position`, taken between two decisions. The client replaces
confirmed with it, drops the pending changes with `0 < position <= snapshot.position` (already in it), and pulls from
`position`. Text tombstones are part of a snapshot: a later insertion may be anchored on an erased character.

A client that changes the collections it replicates starts from a snapshot.

### 5.5 Live updates

The hub adds a connection to the group of each collection it pulls, and sends `LogAdvanced` to the group when a change on
that collection is admitted. It is a hint to synchronize, never the data: a lost hint costs only delay.

### 5.6 Storage of the server

`IReplicationStore` keeps the head (position, horizon), the replicas, the documents and the log. `MemoryReplicationStore`
is the default. `EntityReplicationStore` (Queryeables.Replication) writes them through any Queryeables store, and can
write the merged documents to the application's own entities in the same transaction (`ReplicationProjection<T>`). A
server process started again over the same store goes on where the previous one stopped (test:
`A_server_restarted_over_its_store_goes_on_where_it_stopped`, and `Devices_converge_across_a_restart_of_the_server…` in
Queryeables).

## 6. Failures and their outcome

| Failure | Outcome | Test |
|---|---|---|
| Network down | Writes go on locally; the sync throws and changes nothing. | `Two_devices_offline_converge…` |
| Push answer lost | Pushed again → `duplicate`, applied once. | `A_push_whose_answer_was_lost…` |
| Pull answer lost / stop after push | Pending kept with its position; the next pull confirms it. | `A_stop_between_the_push_and_the_pull…` |
| Client restart | Same replica, cursor, pending, rejected, clock. | `A_restart_keeps_the_offline_changes…`, `A_text_survives_a_restart` |
| Server restart | Same log, documents, replicas (store). | `A_server_restarted_over_its_store…` |
| Log compacted | Snapshot, own changes counted once. | `A_replica_far_behind…`, `A_snapshot_keeps_the_erased_characters…` |
| Device clock in the future (> 5 min) | Refused `clock-skew`; the server does not follow that clock; the client falls back to the server's clock and sends the same intent again under a new dot (once per sync). Later operations that refer to the old dot (an observed tag, a text anchor, an erased span) are renamed. | `A_clock_in_the_future…`, `A_clock_still_wrong…`, `…stamped_again…` |
| Change refused by the rules | Removed from the view whole, kept in `Rejected`; never reaches another client. | `A_refused_change_disappears_exactly…`, `A_change_is_admitted_whole_or_not_at_all` |
| Answer forgotten by the server and change not in the log | Client side: `lost`, moved to `Rejected`. | bounded by `RememberedAcksPerReplica` |
| Random mix of the above | Same state everywhere, every write in the log once, no rejection. | `Random_writes_cuts_restarts…` (.NET, TypeScript), `Random_typing_on_three_devices_converges` |

## 7. Security

* **Authentication** is the transport's (`MapRouteablesReplication().RequireAuthorization()`). RRP receives a `ClaimsPrincipal`.
* **A replica belongs to one user**: the first that uses it. Another user gets `replica-taken`.
* **Admission** (`IReplicationPolicy.AdmitAsync`) sees the user, the whole change, and the documents **as stored on the
  server**. A client offline cannot write a document of another user by pretending it is new. Validation belongs here.
* **Reading** (`CanRead`) filters the log and the snapshot per operation.
* **Limits**: changes per push, operations per change, value, text and key length, collections per pull, clock skew;
  refused as `too-large`, `invalid`, `clock-skew`.

## 8. Versioning

`protocol` is an integer, separate from protocol 2 of the sessions. A server refuses another version with a message
(`update the client`). Adding a field is compatible; adding an operation or changing a merge is a new version. Each client
store has its own format (`ReplicaCodec` 2 in .NET, which still reads 1; `format: 1` JSON in TypeScript and Dart).

## 9. Mapping

### 9.1 Queryeables

* A Queryeables entity is a collection, its key the document key; scalar properties are registers, a collection of values
  a set, a counter a counter, a long text a text field.
* `EntityReplicationStore` keeps the protocol in the database; `ReplicationProjection<T>` writes each merged document to its
  entity in the same transaction: the server's read model, queried as usual.

### 9.2 Routeables.SignalR

* `services.AddRouteablesReplication(_ => new ReplicationServer(policy, store: store))` and `app.MapRouteablesReplication()`.
* A `[RouteableState]` bound to replicated data is refreshed on the replica's change event: the session protocol carries
  the view, RRP the data.

## 10. Known limits of version 1

* One server process writes a log at a time: two processes over one database refuse the second write of a position
  instead of corrupting the log, but a lock in the database is needed to run several.
* The client's own change that it may not read (`CanRead`) disappears from its view after the sync.
* Visibility is decided with the state of the document now; a document that becomes visible later is received complete
  only through a snapshot.
* Text tombstones are never collected; a field edited for years grows with every character ever typed.
* The client stores rewrite their whole state at each step; a large replica would want an append-only store.
