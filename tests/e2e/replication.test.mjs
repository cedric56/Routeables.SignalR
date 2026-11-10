// End-to-end tests of the Routeables Remote Protocol: the .NET server (ReplicationHub of tests/Routeables.SignalR.E2E.Server) and the
// TypeScript replica (@routeables/client), over a real SignalR connection. The same build steps as e2e.test.mjs.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { dirname, join } from 'node:path';
import { after, before, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';
import { MemoryReplicaStorage, Replica, signalRReplicationChannel } from '../../clients/typescript/dist/index.js';

const here = dirname(fileURLToPath(import.meta.url));
const assembly = process.env.E2E_SERVER
  ?? join(here, '../Routeables.SignalR.E2E.Server/bin/Release/net10.0/Routeables.SignalR.E2E.Server.dll');

let server;
let address;
const channels = [];

before(async () => {
  server = spawn('dotnet', [assembly], { stdio: ['ignore', 'pipe', 'inherit'] });
  let output = '';
  server.stdout.setEncoding('utf8');
  while (!address) {
    const [chunk] = await Promise.race([
      once(server.stdout, 'data'),
      once(server, 'exit').then(([code]) => { throw new Error(`The server stopped (${code}).`); }),
    ]);
    output += chunk;
    address = /listening (\S+)/.exec(output)?.[1];
  }
});

after(async () => {
  await Promise.all(channels.map(c => c.stop().catch(() => {})));
  server?.kill();
});

/** A device: a replica in its storage, and a channel to the hub as `user`. */
async function device(user, collections = ['Person', 'Note']) {
  const storage = new MemoryReplicaStorage();
  const channel = signalRReplicationChannel(`${address}/replication?user=${encodeURIComponent(user)}`, { logLevel: 6 });
  channels.push(channel);
  return { storage, channel, replica: await Replica.open(storage, collections), collections };
}

async function until(condition, what) {
  for (let i = 0; i < 300; i++) {
    if (condition()) return;
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  assert.fail(`Timed out waiting for ${what}.`);
}

const unique = () => Math.random().toString(36).slice(2);

describe('replication over a real SignalR connection', () => {
  it('refuses a replica that is not signed in (the endpoint is protected)', async () => {
    const channel = signalRReplicationChannel(`${address}/replication`, { logLevel: 6 });
    await assert.rejects(channel.start());
  });

  it('converges two devices that wrote offline, text included', async () => {
    const person = { collection: 'Person', key: unique() };
    const note = { collection: 'Note', key: unique() };
    const a = await device('ann');
    const b = await device('bob');
    await a.replica.edit().create(person).set(person, 'name', '"Ann"').addElement(person, 'tags', '"x"')
      .create(note).insertText(note, 'body', 0, 'The quick fox').commit();
    await a.replica.sync(a.channel);
    await b.replica.sync(b.channel);
    assert.equal(b.replica.find(note).text('body'), 'The quick fox');

    // Offline: nothing is sent; both write.
    await a.replica.edit().replaceText(note, 'body', 'The quick brown fox').removeElement(person, 'tags', '"x"').increment(person, 'visits', 2).commit();
    await b.replica.edit().replaceText(note, 'body', 'The quick fox jumps').addElement(person, 'tags', '"x"').increment(person, 'visits', 3).commit();

    for (const d of [a, b, a]) await d.replica.sync(d.channel);
    for (const d of [a, b]) {
      assert.equal(d.replica.find(note).text('body'), 'The quick brown fox jumps');
      assert.deepEqual(d.replica.find(person).elements('tags'), ['"x"']);
      assert.equal(d.replica.find(person).counter('visits'), 5);
      assert.equal(d.replica.pendingCount, 0);
    }
  });

  it('tells the connected devices that the log grew (a hint to pull)', async () => {
    const person = { collection: 'Person', key: unique() };
    const a = await device('ann');
    const b = await device('bob');
    await b.replica.sync(b.channel);                 // a pull subscribes the connection to its collections
    const off = b.channel.onLogAdvanced(() => { b.replica.sync(b.channel).catch(() => {}); });
    await a.replica.edit().create(person).set(person, 'name', '"live"').commit();
    await a.replica.sync(a.channel);
    await until(() => b.replica.find(person)?.get('name') === '"live"', 'the change on the other device');
    off();
  });

  it('applies the rules of the server: a refused change leaves whole, a private document is never sent', async () => {
    const own = { collection: 'Person', key: unique() };
    const ann = await device('ann');
    const bob = await device('bob');
    await ann.replica.edit().create(own).set(own, 'owner', '"ann"').set(own, 'name', '"Ann"').commit();
    await ann.replica.sync(ann.channel);
    await ann.replica.edit().set(own, 'name', '"forbidden"').increment(own, 'visits', 1).commit();
    const report = await ann.replica.sync(ann.channel);
    assert.equal(report.rejected, 1);
    assert.equal(ann.replica.find(own).get('name'), '"Ann"');
    assert.equal(ann.replica.find(own).counter('visits'), 0);
    assert.equal(ann.replica.rejected[0].code, 'invalid');

    await bob.replica.sync(bob.channel);
    assert.equal(bob.replica.find(own), undefined);
    // Bob guesses the key and writes it offline: the server refuses it.
    await bob.replica.edit().create(own).set(own, 'name', '"pwned"').commit();
    assert.equal((await bob.replica.sync(bob.channel)).rejected, 1);
    await ann.replica.sync(ann.channel);
    assert.equal(ann.replica.find(own).get('name'), '"Ann"');
  });

  it('keeps its offline changes across a restart and sends them once', async () => {
    const person = { collection: 'Person', key: unique() };
    const a = await device('ann');
    await a.replica.edit().create(person).increment(person, 'visits', 1).commit();
    const reopened = await Replica.open(a.storage, a.collections);   // the process stopped before it could synchronize
    await reopened.sync(a.channel);
    await reopened.sync(a.channel);
    const b = await device('bob');
    await b.replica.sync(b.channel);
    assert.equal(b.replica.find(person).counter('visits'), 1);
    assert.equal(reopened.pendingCount, 0);
  });

  it('refuses a replica taken by another user', async () => {
    const a = await device('ann');
    await a.replica.edit().create({ collection: 'Person', key: unique() }).commit();
    await a.replica.sync(a.channel);
    const thief = signalRReplicationChannel(`${address}/replication?user=mallory`, { logLevel: 6 });
    channels.push(thief);
    const welcome = await thief.hello({ protocol: 1, replica: a.replica.id, clock: a.replica.clock.last });
    assert.equal(welcome.success, false);
  });
});
