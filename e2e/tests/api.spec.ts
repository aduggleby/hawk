/**
 * @file E2E coverage for the agent API: create a key on the API keys page, use /api/v1 with it (whoami, list, pause/resume,
 * PATCH alerting, run now + poll), revoke it in the UI and expect 401. A read-only key gets 403 on writes.
 * A second test creates, edits and deletes an HTTP and a heartbeat monitor through the API (If-Match, ETag, pingUrl).
 * The CRUD test also checks a redirect with followRedirects false/true against the mock /redirect endpoint.
 * Uses the paused seed monitor "[Seed] Mock GET Contains" (mock server) and leaves it paused with its alert settings restored.
 */

import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';
import { loginAsSeedAdmin, snap } from './helpers';

type Scope = 'Read' | 'Write';

async function createKey(page: Page, name: string, scope: Scope): Promise<string> {
  await page.goto('/Identity/Account/Manage/ApiKeys');
  await expect(page.getByRole('heading', { name: 'API keys' })).toBeVisible();
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.locator(`input[name="Scope"][value="${scope}"]`).check();
  await page.getByRole('button', { name: /^create key$/i }).click();
  const input = page.locator('#new-api-key');
  await expect(input).toBeVisible();
  const key = await input.inputValue();
  expect(key).toMatch(/^hawk_[A-Za-z0-9_-]{43}$/);
  return key;
}

async function revokeKey(page: Page, name: string) {
  await page.goto('/Identity/Account/Manage/ApiKeys');
  // The key name is also a substring of other rows' names only if reused; names here are unique.
  const row = page.locator('div.p-4', { has: page.getByText(name, { exact: false }) }).filter({ has: page.getByRole('button', { name: 'Revoke', exact: true }) }).last();
  page.once('dialog', dialog => dialog.accept());
  await row.getByRole('button', { name: 'Revoke', exact: true }).click();
  await expect(page.getByText('API key revoked.')).toBeVisible();
}

test('api: write key reads, pauses/resumes, patches alerting, runs a monitor; revoked key gets 401', async ({ page, request }) => {
  await loginAsSeedAdmin(page);

  const keyName = `e2e write ${Date.now()}`;
  const key = await createKey(page, keyName, 'Write');
  await snap(page, '60-api-keys-new-key');
  const auth = { Authorization: `Bearer ${key}` };
  const json = { ...auth, 'Content-Type': 'application/json' };

  // The list shows a prefix, never the secret.
  await page.goto('/Identity/Account/Manage/ApiKeys');
  await expect(page.getByText(keyName)).toBeVisible();
  expect(await page.content()).not.toContain(key);
  await snap(page, '61-api-keys-list');

  // Whoami.
  const who = await request.get('/api/v1', { headers: auth });
  expect(who.status()).toBe(200);
  const whoBody = await who.json();
  expect(whoBody.user.email).toBe(process.env.HAWK_SEED_EMAIL ?? 'ad@dualconsult.com');
  expect(whoBody.key).toMatchObject({ name: keyName, scope: 'write' });

  // List monitors and find the seed monitor.
  const list = await request.get('/api/v1/monitors', { headers: auth });
  expect(list.status()).toBe(200);
  const monitors = (await list.json()).monitors as Array<{ id: string; name: string; state: string; type: string }>;
  const seed = monitors.find(m => m.name === '[Seed] Mock GET Contains');
  expect(seed).toBeTruthy();
  const id = seed!.id;
  const base = `/api/v1/monitors/${id}`;

  const detail = await (await request.get(base, { headers: auth })).json();
  const originalThreshold: number = detail.alertAfterConsecutiveFailures;
  const originalEmail: string | null = detail.alertEmailOverride;

  try {
    // Pause / resume: seed monitors start paused; resume then pause again (and pause twice for changed:false).
    const resume = await request.post(`${base}/resume`, { headers: auth });
    expect(resume.status()).toBe(200);
    expect(await resume.json()).toMatchObject({ state: 'active' });
    const resumedDetail = await (await request.get(base, { headers: auth })).json();
    expect(resumedDetail.state).toBe('active');
    const pause = await request.post(`${base}/pause`, { headers: auth });
    expect(await pause.json()).toMatchObject({ changed: true, state: 'paused' });
    const pauseAgain = await request.post(`${base}/pause`, { headers: auth });
    expect(await pauseAgain.json()).toMatchObject({ changed: false, state: 'paused' });

    // PATCH merge semantics.
    const patch1 = await request.patch(`${base}/alerting`, {
      headers: json,
      data: { alertAfterConsecutiveFailures: 3, alertEmailOverride: 'ops@example.com' },
    });
    expect(patch1.status()).toBe(200);
    expect(await patch1.json()).toMatchObject({ alertAfterConsecutiveFailures: 3, alertEmailOverride: 'ops@example.com' });
    const patch2 = await request.patch(`${base}/alerting`, { headers: json, data: { alertAfterConsecutiveFailures: 2 } });
    expect(await patch2.json()).toMatchObject({ alertAfterConsecutiveFailures: 2, alertEmailOverride: 'ops@example.com' });
    const typo = await request.patch(`${base}/alerting`, { headers: json, data: { alertEmailOverrid: 'a@b.c' } });
    expect(typo.status()).toBe(400);
    expect(typo.headers()['content-type']).toContain('application/problem+json');
    expect((await typo.json()).code).toBe('validation_failed');

    // The change is visible in the UI.
    await page.goto(`/Monitors/Details/${id}`);
    await expect(page.getByText('ops@example.com').first()).toBeVisible();

    // Run now (works on a paused monitor) and poll to completion.
    const run = await request.post(`${base}/run`, { headers: auth });
    expect(run.status()).toBe(202);
    const queued = await run.json();
    expect(queued.runId).toBeTruthy();
    expect(queued.runUrl).toBe(`${base}/runs/${queued.runId}`);
    await expect
      .poll(async () => (await (await request.get(queued.runUrl, { headers: auth })).json()).state, { timeout: 60_000 })
      .toBe('completed');
    const finished = await (await request.get(queued.runUrl, { headers: auth })).json();
    expect(finished).toMatchObject({ success: true, statusCode: 200, reason: 'manual' });
    const runs = await (await request.get(`${base}/runs?limit=5`, { headers: auth })).json();
    expect(runs.runs[0].id).toBe(queued.runId);
  } finally {
    // Restore the seed monitor for other tests.
    await request.patch(`${base}/alerting`, {
      headers: json,
      data: { alertAfterConsecutiveFailures: originalThreshold, alertEmailOverride: originalEmail },
    });
    await request.post(`${base}/pause`, { headers: auth });
  }

  // Revoke in the UI; the key stops working immediately.
  await revokeKey(page, keyName);
  await snap(page, '62-api-keys-revoked');
  const after = await request.get('/api/v1', { headers: auth });
  expect(after.status()).toBe(401);
  expect((await after.json()).code).toBe('unauthorized');
});

test('api: read-only key can read but gets 403 insufficient_scope on writes; cookie and query-string keys get 401', async ({ page, request }) => {
  await loginAsSeedAdmin(page);
  const keyName = `e2e read ${Date.now()}`;
  const key = await createKey(page, keyName, 'Read');
  const auth = { Authorization: `Bearer ${key}` };

  const list = await request.get('/api/v1/monitors', { headers: auth });
  expect(list.status()).toBe(200);
  const monitors = (await list.json()).monitors as Array<{ id: string; name: string }>;
  const seed = monitors.find(m => m.name === '[Seed] Mock GET Contains')!;

  const write = await request.post(`/api/v1/monitors/${seed.id}/pause`, { headers: auth });
  expect(write.status()).toBe(403);
  expect(write.headers()['content-type']).toContain('application/problem+json');
  expect((await write.json()).code).toBe('insufficient_scope');

  // A key in the query string is ignored; a login cookie alone does not authorize /api.
  expect((await request.get(`/api/v1?access_token=${key}`)).status()).toBe(401);
  const cookieOnly = await page.request.get('/api/v1');
  expect(cookieOnly.status()).toBe(401);

  await revokeKey(page, keyName);
});

test('api: create, edit and delete an HTTP monitor and a heartbeat monitor with a write key', async ({ page, request }) => {
  await loginAsSeedAdmin(page);
  const keyName = `e2e crud ${Date.now()}`;
  const key = await createKey(page, keyName, 'Write');
  const auth = { Authorization: `Bearer ${key}` };
  const json = { ...auth, 'Content-Type': 'application/json' };
  const created: string[] = [];

  try {
    // Reuse the seed monitor's mock URL and match rule so the new monitor passes against the mock server.
    const list = (await (await request.get('/api/v1/monitors', { headers: auth })).json()).monitors as Array<{ id: string; name: string }>;
    const seedId = list.find(m => m.name === '[Seed] Mock GET Contains')!.id;
    const seed = await (await request.get(`/api/v1/monitors/${seedId}`, { headers: auth })).json();

    // Create an HTTP monitor (AE1).
    const name = `e2e api http ${Date.now()}`;
    const create = await request.post('/api/v1/monitors', {
      headers: json,
      data: { name, url: seed.url, intervalSeconds: 300, matchRules: seed.matchRules },
    });
    expect(create.status()).toBe(201);
    const detail = await create.json();
    created.push(detail.id);
    const base = `/api/v1/monitors/${detail.id}`;
    expect(create.headers()['location']).toBe(base);
    expect(create.headers()['etag']).toBe(`"${detail.concurrencyStamp}"`);
    expect(detail).toMatchObject({ name, type: 'http', method: 'GET', state: 'active', ownedByCaller: true, pingUrl: null });
    await page.goto(`/Monitors/Details/${detail.id}`);
    await expect(page.getByText(name).first()).toBeVisible();

    // The new monitor really runs against the mock server.
    const run = await request.post(`${base}/run`, { headers: auth });
    expect(run.status()).toBe(202);
    const queued = await run.json();
    await expect
      .poll(async () => (await (await request.get(queued.runUrl, { headers: auth })).json()).state, { timeout: 60_000 })
      .toBe('completed');
    expect(await (await request.get(queued.runUrl, { headers: auth })).json()).toMatchObject({ success: true, statusCode: 200 });

    // Checking a redirect (AE6): without following, a 301 listed in allowedStatusCodes passes; following lands on /ok (200).
    const redirectUrl = (seed.url as string).replace(/\/[^/]*$/, '/redirect');
    const redirName = `e2e api redirect ${Date.now()}`;
    const redir = await request.post('/api/v1/monitors', {
      headers: json,
      data: { name: redirName, url: redirectUrl, intervalSeconds: 300, followRedirects: false, allowedStatusCodes: '301' },
    });
    expect(redir.status()).toBe(201);
    const redirDetail = await redir.json();
    created.push(redirDetail.id);
    expect(redirDetail).toMatchObject({ followRedirects: false, allowedStatusCodes: '301' });
    const redirBase = `/api/v1/monitors/${redirDetail.id}`;
    const runRedirect = async () => {
      const queuedRun = await (await request.post(`${redirBase}/run`, { headers: auth })).json();
      await expect
        .poll(async () => (await (await request.get(queuedRun.runUrl, { headers: auth })).json()).state, { timeout: 60_000 })
        .toBe('completed');
      return await (await request.get(queuedRun.runUrl, { headers: auth })).json();
    };
    expect(await runRedirect()).toMatchObject({ success: true, statusCode: 301 });
    const followed = await request.patch(redirBase, { headers: json, data: { followRedirects: true } });
    expect(followed.status()).toBe(200);
    expect(await followed.json()).toMatchObject({ followRedirects: true });
    expect(await runRedirect()).toMatchObject({ success: true, statusCode: 200 });
    await page.goto(`/Monitors/Details/${redirDetail.id}`);
    await expect(page.getByText('Follow redirects').first()).toBeVisible();
    await snap(page, '63-monitor-follow-redirects');

    // Validation errors use JSON field names.
    const bad = await request.post('/api/v1/monitors', { headers: json, data: { name: 'x', url: 'ftp://nope', intervalSeconds: 7 } });
    expect(bad.status()).toBe(400);
    const badErrors = (await bad.json()).errors;
    expect(Object.keys(badErrors)).toEqual(expect.arrayContaining(['url', 'intervalSeconds']));

    // Patch with If-Match (AE4): the stale value is rejected, the fresh one accepted.
    const patched = await request.patch(base, {
      headers: { ...json, 'If-Match': create.headers()['etag'] },
      data: { name: `${name} renamed`, headers: [{ name: 'X-E2E', value: '1' }] },
    });
    expect(patched.status()).toBe(200);
    const patchedBody = await patched.json();
    expect(patchedBody).toMatchObject({ name: `${name} renamed`, headers: [{ name: 'X-E2E', value: '1' }] });
    expect(patchedBody.matchRules).toEqual(seed.matchRules);
    expect(patchedBody.concurrencyStamp).not.toBe(detail.concurrencyStamp);
    const stale = await request.patch(base, { headers: { ...json, 'If-Match': create.headers()['etag'] }, data: { name: 'stale' } });
    expect(stale.status()).toBe(412);
    expect((await stale.json()).code).toBe('precondition_failed');
    expect((await (await request.get(base, { headers: auth })).json()).name).toBe(`${name} renamed`);
    const typeChange = await request.patch(base, { headers: json, data: { type: 'heartbeat' } });
    expect(typeChange.status()).toBe(400);

    // Heartbeat: create, ping via pingUrl, patch the period, delete (AE2, AE3).
    const hbName = `e2e api heartbeat ${Date.now()}`;
    const hbCreate = await request.post('/api/v1/monitors', {
      headers: json,
      data: { name: hbName, type: 'heartbeat', intervalSeconds: 3600, heartbeatGraceSeconds: 300 },
    });
    expect(hbCreate.status()).toBe(201);
    const hb = await hbCreate.json();
    created.push(hb.id);
    expect(hb.pingUrl).toMatch(/\/ping\/[0-9a-f]{32}$/);
    const hbBase = `/api/v1/monitors/${hb.id}`;
    const hbGet = await request.get(hbBase, { headers: auth });
    const hbDetail = await hbGet.json();
    expect(hbDetail.pingUrl).toBeNull();
    expect(JSON.stringify(hbDetail)).not.toContain(hb.pingUrl.split('/ping/')[1]);
    expect(hbDetail.lastPingAt).toBeNull();

    expect((await request.get(hb.pingUrl)).status()).toBe(200);
    const pinged = await (await request.get(hbBase, { headers: auth })).json();
    expect(pinged.lastPingAt).not.toBeNull();
    const deadline = pinged.nextRunAt;
    const rename = await (await request.patch(hbBase, { headers: json, data: { name: `${hbName} x` } })).json();
    expect(rename.nextRunAt).toBe(deadline);
    const longer = await (await request.patch(hbBase, { headers: json, data: { intervalSeconds: 86400 } })).json();
    expect(new Date(longer.nextRunAt).getTime()).toBeGreaterThan(new Date(deadline).getTime() + 20 * 3600 * 1000);

    // Delete (AE5): 204, then 404 for the monitor and for its ping URL.
    const del = await request.delete(hbBase, { headers: { ...auth, 'If-Match': `"${longer.concurrencyStamp}"` } });
    expect(del.status()).toBe(204);
    expect((await request.get(hbBase, { headers: auth })).status()).toBe(404);
    expect((await request.get(hb.pingUrl)).status()).toBe(404);
    expect((await request.delete(hbBase, { headers: auth })).status()).toBe(404);
    created.splice(created.indexOf(hb.id), 1);
  } finally {
    // Keep reruns clean.
    for (const id of created)
      await request.delete(`/api/v1/monitors/${id}`, { headers: auth });
  }

  const after = (await (await request.get('/api/v1/monitors', { headers: auth })).json()).monitors as Array<{ id: string }>;
  expect(after.some(m => created.includes(m.id))).toBe(false);
  await revokeKey(page, keyName);
});

test('api: monitor groups - create with a group, filter the list, list groups, pause, rename and resume a group', async ({ page, request }) => {
  await loginAsSeedAdmin(page);
  const keyName = `e2e groups ${Date.now()}`;
  const key = await createKey(page, keyName, 'Write');
  const auth = { Authorization: `Bearer ${key}` };
  const json = { ...auth, 'Content-Type': 'application/json' };
  const created: string[] = [];
  const group = `E2E Group ${Date.now()}`;
  const renamed = `${group} renamed`;

  try {
    for (const suffix of ['a', 'b']) {
      const create = await request.post('/api/v1/monitors', {
        headers: json,
        // The second request spells the group differently; it must join the first group.
        data: { name: `e2e group ${suffix} ${Date.now()}`, url: 'http://mock:8081/ok', intervalSeconds: 300, group: suffix === 'a' ? group : group.toUpperCase() },
      });
      expect(create.status()).toBe(201);
      const body = await create.json();
      created.push(body.id);
      expect(body.group).toBe(group);
    }

    const filtered = (await (await request.get(`/api/v1/monitors?group=${encodeURIComponent(group.toLowerCase())}`, { headers: auth })).json()).monitors;
    expect(filtered.map((m: { id: string }) => m.id).sort()).toEqual([...created].sort());
    expect((await (await request.get('/api/v1/monitors?group=no-such-group', { headers: auth })).json()).monitors).toEqual([]);

    const groups = (await (await request.get('/api/v1/groups', { headers: auth })).json()).groups as Array<{ name: string; monitorCount: number; pausedCount: number }>;
    expect(groups.find(g => g.name === group)).toMatchObject({ monitorCount: 2, pausedCount: 0 });

    const paused = await request.post('/api/v1/groups/pause', { headers: json, data: { group } });
    expect(paused.status()).toBe(200);
    expect(await paused.json()).toEqual({ changed: 2 });

    const rename = await request.post('/api/v1/groups/rename', { headers: json, data: { group, newName: renamed } });
    expect(rename.status()).toBe(200);
    expect(await rename.json()).toEqual({ renamed: 2, group: renamed });
    expect((await request.post('/api/v1/groups/pause', { headers: json, data: { group } })).status()).toBe(404);

    const resumed = await request.post('/api/v1/groups/resume', { headers: json, data: { group: renamed } });
    expect(await resumed.json()).toEqual({ changed: 2 });
    const detail = await (await request.get(`/api/v1/monitors/${created[0]}`, { headers: auth })).json();
    expect(detail).toMatchObject({ group: renamed, state: 'active' });

    // The group is cleared with null.
    const cleared = await request.patch(`/api/v1/monitors/${created[0]}`, { headers: json, data: { group: null } });
    expect((await cleared.json()).group).toBeNull();
  } finally {
    for (const id of created)
      await request.delete(`/api/v1/monitors/${id}`, { headers: auth });
  }

  await revokeKey(page, keyName);
});
