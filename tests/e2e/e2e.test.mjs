// End-to-end tests: a real Kestrel server with real SignalR (tests/Routeables.SignalR.E2E.Server), driven by @routeables/client from Node.
// What the in-memory tests cannot show is here: SignalR runs the calls of one client one after the other, closes a connection, reconnects it.
//
//   dotnet build tests/Routeables.SignalR.E2E.Server -c Release
//   npm --prefix clients/typescript run build
//   node --test tests/e2e/
//
// E2E_SERVER may name the server assembly (the Release build by default).
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { dirname, join } from 'node:path';
import { after, before, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';
import { RouteablesClient, signalRTransport } from '../../clients/typescript/dist/index.js';

const here = dirname(fileURLToPath(import.meta.url));
const assembly = process.env.E2E_SERVER
  ?? join(here, '../Routeables.SignalR.E2E.Server/bin/Release/net10.0/Routeables.SignalR.E2E.Server.dll');

let server;
let address;

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

after(() => server?.kill());

/** A client of the server, signed in as `user` (with `roles`), or anonymous. */
async function open(user, roles = '') {
  const query = user ? `?user=${encodeURIComponent(user)}&roles=${encodeURIComponent(roles)}` : '';
  const transport = signalRTransport(`${address}/routeables${query}`, { logLevel: 6 /* LogLevel.None */ });
  const client = new RouteablesClient(transport);
  await client.connect();
  return client;
}

/** Waits until `condition` holds (the state arrives by messages). */
async function until(condition, what) {
  for (let i = 0; i < 300; i++) {
    if (condition()) return;
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  assert.fail(`Timed out waiting for ${what}.`);
}

describe('over a real SignalR connection', () => {
  it('refuses a connection that is not signed in (the endpoint is protected)', async () => {
    await assert.rejects(open(null));
  });

  it('shows a page, applies what changes, and answers after the state', async () => {
    const client = await open('ann');
    try {
      assert.equal((await client.goTo('/stream')).success, true);
      assert.equal(client.page.viewModel, 'StreamViewModel');

      assert.equal((await client.invoke('count', 1000)).success, true);
      // The answer comes after the change it caused: no wait needed.
      assert.equal(client.page.state.value, 1000);
      assert.equal(client.page.version, 1);   // a thousand changes in one turn: one message

      for (const word of ['a', 'b', 'c']) await client.invoke('add', word);
      assert.deepEqual(client.page.state.words, ['a', 'b', 'c']);

      assert.equal((await client.set('search', 'tea')).success, true);
      assert.equal(client.page.state.search, 'tea');
      assert.equal((await client.set('value', 5)).code, 'not-found');   // not settable
    } finally {
      await client.disconnect();
    }
  });

  it('does not block a route change that asks the visitor, although SignalR runs one call at a time', async () => {
    const client = await open('ann');
    try {
      await client.goTo('/guarded');

      const leaving = client.goTo('/stream');
      await until(() => client.dialogs.length === 1, 'the dialog');
      assert.equal(client.dialogs[0].title, 'Leave');

      // Answering is another call of the same connection: it gets through while the route change waits.
      assert.equal((await client.answer(client.dialogs[0], 0)).success, true);
      assert.equal((await leaving).success, true);
      assert.equal(client.page.viewModel, 'StreamViewModel');
    } finally {
      await client.disconnect();
    }
  });

  it('waits for the end of a command that asks something, and of a long one', async () => {
    const client = await open('ann');
    try {
      await client.goTo('/stream');

      const asking = client.invoke('askAsync');
      await until(() => client.dialogs.length === 1, 'the dialog');
      await client.answer(client.dialogs[0], 1);
      assert.equal((await asking).success, true);
      assert.equal(client.page.state.answer, 'no');

      const slow = await client.invoke('slowAsync', 600);
      assert.equal(slow.success, true);
      assert.equal(client.page.state.value, 1);
    } finally {
      await client.disconnect();
    }
  });

  it('authorizes every call for the user of the connection', async () => {
    const clerk = await open('clerk');
    const manager = await open('boss', 'manager');
    try {
      await clerk.goTo('/stream');
      await manager.goTo('/stream');

      assert.equal((await clerk.invoke('approve')).code, 'unauthorized');
      assert.equal((await manager.invoke('approve')).success, true);
      assert.equal(manager.page.state.answer, 'approved');
      assert.ok(!clerk.page.commands.includes('approve'));

      assert.equal((await clerk.goTo('/admin')).success, false);
      assert.notEqual(clerk.page.viewModel, 'AdminViewModel');
      assert.equal((await manager.goTo('/admin')).success, true);
      assert.equal(manager.page.state.secret, 'for managers');
    } finally {
      await clerk.disconnect();
      await manager.disconnect();
    }
  });

  it('refuses an argument too large without closing the connection', async () => {
    const client = await open('ann');
    try {
      await client.goTo('/stream');

      const ack = await client.invoke('add', 'x'.repeat(5000));

      assert.equal(ack.code, 'invalid-argument');
      assert.equal((await client.invoke('add', 'small')).success, true);
    } finally {
      await client.disconnect();
    }
  });

  it('resumes its session on a new connection: the page, its state and the dialog that waits', async () => {
    const client = await open('ann');
    try {
      await client.goTo('/stream');
      await client.invoke('count', 7);
      const instance = client.page.sequence;
      const asking = client.invoke('askAsync');
      await until(() => client.dialogs.length === 1, 'the dialog');

      // The connection closes, and the same client connects again: it says hello with the token of its session.
      await client.disconnect();
      await client.connect();

      await until(() => client.page?.generation > 1, 'the page sent again');
      assert.equal(client.page.sequence, instance);   // the same page: the component is kept
      assert.equal(client.page.state.value, 7);
      assert.equal(client.dialogs.length, 1);

      await client.answer(client.dialogs[0], 0);
      assert.equal((await asking).success, true);   // the call made on the first connection ends on the second
      assert.equal(client.page.state.answer, 'yes');
    } finally {
      await client.disconnect();
    }
  });
});
