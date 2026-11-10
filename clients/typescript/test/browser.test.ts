import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
  EssentialsBridge, SessionFiles, browserEssentials, emailService, geocodingService, launcherService, mailtoUrl, mapUrl, secureStorageService,
  sensorService, smsUrl, vibrationService, webAuthenticatorService,
} from '../src/index.js';
import type { EssentialsService, SecureKeyStore, ServerMethod } from '../src/index.js';

class MemoryStorage {
  private readonly items = new Map<string, string>();
  get length(): number { return this.items.size; }
  key(index: number): string | null { return [...this.items.keys()][index] ?? null; }
  getItem(key: string): string | null { return this.items.get(key) ?? null; }
  setItem(key: string, value: string): void { this.items.set(key, value); }
  removeItem(key: string): void { this.items.delete(key); }
}

const memoryKeys = (): SecureKeyStore => {
  let key: Promise<CryptoKey> | null = null;
  return { getOrCreate: create => (key ??= create()) };
};

const call = (service: EssentialsService, method: string, ...args: unknown[]) =>
  (service.methods![method] as (...a: unknown[]) => unknown)(...args);

async function rejectsWith(action: () => unknown, code: string): Promise<void> {
  await assert.rejects(Promise.resolve().then(action), (error: { code?: string }) => error.code === code);
}

describe('the addresses a browser opens', () => {
  it('builds an SMS with its recipients and body', () => {
    assert.equal(smsUrl({ body: 'Hi & bye', recipients: ['+33 1 23', 'x+44'] }), 'sms:+33123,+44?body=Hi%20%26%20bye');
  });

  it('builds a mailto with the escaped fields', () => {
    assert.equal(mailtoUrl({ subject: 'Zoo?', body: 'a\nb', to: ['ann@example.org'], cc: ['bob@example.org'] }),
      'mailto:ann%40example.org?subject=Zoo%3F&body=a%0Ab&cc=bob%40example.org');
  });

  it('opens OpenStreetMap at a place, along a route, or searching an address', () => {
    assert.equal(mapUrl({ latitude: 47.2, longitude: -1.5 }), 'https://www.openstreetmap.org/?mlat=47.2&mlon=-1.5#map=15/47.2/-1.5');
    assert.match(mapUrl({ latitude: 47.2, longitude: -1.5, navigationMode: 'Walking' }), /engine=fossgis_osrm_foot&route=%3B47.2%2C-1.5/);
    assert.equal(mapUrl({ latitude: 0, longitude: 0, placemark: { thoroughfare: 'Rue X', locality: 'Nantes' } }),
      'https://www.openstreetmap.org/search?query=Rue%20X%2C%20Nantes');
  });
});

describe('what the browser refuses', () => {
  it('never opens a scheme that runs code', async () => {
    const launcher = launcherService(new SessionFiles());
    assert.equal(call(launcher, 'canOpen', 'javascript:alert(1)'), false);
    assert.equal(call(launcher, 'open', 'javascript:alert(1)'), false);
    assert.equal(call(launcher, 'open', 'data:text/html,hi'), false);
    assert.equal(call(launcher, 'canOpen', 'https://example.org/'), true);
    await rejectsWith(() => call(webAuthenticatorService(), 'authenticate', { url: 'javascript:x', callbackUrl: '/done' }), 'invalid-argument');
  });

  it('says what it cannot do', async () => {
    await rejectsWith(() => call(emailService(), 'compose', { to: ['a@b.c'], attachments: [{ url: '/f' }] }), 'not-supported');
    await rejectsWith(() => call(sensorService('barometer'), 'start', { speed: 'UI' }), 'not-supported');
    assert.deepEqual(vibrationService().state!(), { isSupported: false });   // no navigator.vibrate in Node
    await rejectsWith(() => call(vibrationService(), 'vibrate', 100), 'not-supported');
  });

  it('announces every service a browser has, even without the DOM', async () => {
    const services = browserEssentials();
    const bridge = new EssentialsBridge(services, async () => ({ success: true }));
    const { capabilities, state } = await bridge.announcement();
    for (const name of ['appInfo', 'deviceInfo', 'battery', 'geolocation', 'filePicker', 'webAuthenticator', 'accelerometer'])
      assert.ok(capabilities.includes(name), name);
    assert.equal(capabilities.includes('geocoding'), false);   // only with a Nominatim of yours
    assert.deepEqual(state['barometer'], { isSupported: false });
    assert.equal((state['deviceInfo'] as { platform: string }).platform, 'Browser');
  });
});

describe('the files of the session', () => {
  it('uploads to the slot the server gave and downloads with the name and type it said', async () => {
    const seen: { url: string; init?: RequestInit }[] = [];
    const fake = (async (url: string | URL | Request, init?: RequestInit) => {
      seen.push({ url: String(url), init });
      return init?.method === 'POST'
        ? new Response(JSON.stringify({ file: 'abc' }), { status: 200 })
        : new Response('<b>hi</b>', { status: 200, headers: { 'Content-Type': 'application/octet-stream' } });
    }) as typeof fetch;
    const files = new SessionFiles({ baseUrl: 'https://zoo.example/app/', fetch: fake });

    assert.equal(await files.upload('/routeables/essentials/files/T1', new Blob(['x'], { type: 'image/png' }), 'cat é.png'), 'abc');
    assert.equal(seen[0]!.url, 'https://zoo.example/routeables/essentials/files/T1');
    assert.equal((seen[0]!.init!.headers as Record<string, string>)['X-File-Name'], 'cat%20%C3%A9.png');

    const file = await files.download({ url: '/routeables/essentials/files/T2', name: 'page.html', contentType: 'text/html' });
    assert.equal(file.name, 'page.html');
    assert.equal(file.type, 'text/html');
    assert.equal(await file.text(), '<b>hi</b>');
  });

  it('says when the server refused an upload', async () => {
    const files = new SessionFiles({ baseUrl: 'https://zoo.example/', fetch: (async () => new Response('', { status: 413 })) as typeof fetch });
    await rejectsWith(() => files.upload('/x', new Blob(['x']), 'big.bin'), 'invalid-argument');
  });
});

describe('the secure storage of the browser', () => {
  it('stores ciphertext only, and reads its values back', async () => {
    const storage = new MemoryStorage();
    const secure = secureStorageService(storage, memoryKeys());

    await call(secure, 'set', 'token', 's3cr3t');
    const raw = storage.getItem('routeables:secure:token')!;
    assert.ok(!raw.includes('s3cr3t'));
    assert.deepEqual(secure.state!(), { keys: ['token'] });
    assert.equal(await call(secure, 'get', 'token'), 's3cr3t');
    assert.equal(await call(secure, 'get', 'missing'), null);
  });

  it('reads a value it cannot decrypt as missing, and forgets it', async () => {
    const storage = new MemoryStorage();
    await call(secureStorageService(storage, memoryKeys()), 'set', 'token', 'old key');
    const other = secureStorageService(storage, memoryKeys());   // the key is gone (site data partly cleared)

    assert.equal(await call(other, 'get', 'token'), null);
    assert.equal(storage.getItem('routeables:secure:token'), null);
  });

  it('removes one key or all of its keys, not the others', () => {
    const storage = new MemoryStorage();
    storage.setItem('routeables:secure:a', '{}');
    storage.setItem('routeables:secure:b', '{}');
    storage.setItem('other', 'kept');
    const secure = secureStorageService(storage, memoryKeys());

    call(secure, 'remove', 'a');
    assert.deepEqual(secure.state!(), { keys: ['b'] });
    call(secure, 'removeAll');
    assert.deepEqual(secure.state!(), { keys: [] });
    assert.equal(storage.getItem('other'), 'kept');
  });
});

describe('geocoding through a Nominatim of yours', () => {
  it('turns its answers into placemarks and locations', async () => {
    const asked: string[] = [];
    const fake = (async (url: string | URL | Request) => {
      asked.push(String(url));
      return String(url).includes('reverse')
        ? new Response(JSON.stringify({ lat: '47.2', lon: '-1.5', address: { city: 'Nantes', country_code: 'fr', road: 'Rue X' } }))
        : new Response(JSON.stringify([{ lat: '47.2', lon: '-1.5' }]));
    }) as typeof fetch;
    const geocoding = geocodingService('https://geo.example.org', fake);

    const [placemark] = (await call(geocoding, 'getPlacemarks', 47.2, -1.5)) as { locality: string; countryCode: string }[];
    assert.deepEqual([placemark!.locality, placemark!.countryCode], ['Nantes', 'FR']);
    assert.deepEqual(await call(geocoding, 'getLocations', 'Nantes & co'), [{ latitude: 47.2, longitude: -1.5 }]);
    assert.equal(asked[1], 'https://geo.example.org/search?format=json&q=Nantes%20%26%20co');
  });
});

describe('a bridge over the browser services', () => {
  it('answers a call of the server with what the service returned', async () => {
    const calls: { method: ServerMethod; args: unknown[] }[] = [];
    const storage = new MemoryStorage();
    const bridge = new EssentialsBridge({ secureStorage: secureStorageService(storage, memoryKeys()) }, async (method, ...args) => {
      calls.push({ method, args });
      return { success: true };
    });

    await bridge.invoke({ id: 'e1', service: 'secureStorage', method: 'set', args: ['k', 'v'] });
    await bridge.invoke({ id: 'e2', service: 'secureStorage', method: 'get', args: ['k'] });

    assert.deepEqual(calls.map(c => c.args[0]), [{ id: 'e1', success: true, result: null }, { id: 'e2', success: true, result: 'v' }]);
  });
});
