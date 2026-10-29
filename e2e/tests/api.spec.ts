/**
 * @file E2E coverage for the agent API: create a key on the API keys page, use /api/v1 with it (whoami, list, pause/resume,
 * PATCH alerting, run now + poll), revoke it in the UI and expect 401. A read-only key gets 403 on writes.
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
