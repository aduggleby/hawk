/**
 * @file E2E coverage for heartbeat (push) monitors: create, ping, missed ping alert, recovery, and the absence of "Run now".
 * Uses the Testing-only 5s period and 0s grace; alert emails are captured by the mock server.
 */

import { expect, test } from '@playwright/test';
import type { APIRequestContext, Page } from '@playwright/test';
import { loginAsSeedAdmin, snap } from './helpers';

const MOCK_BASE = process.env.MOCK_BASE_URL ?? 'http://mock:8081';

type CapturedEmail = { at: string; body: string };

async function capturedEmails(request: APIRequestContext): Promise<CapturedEmail[]> {
  const res = await request.get(`${MOCK_BASE}/emails`);
  expect(res.ok()).toBeTruthy();
  return (await res.json()) as CapturedEmail[];
}

async function waitForEmail(request: APIRequestContext, subjectPart: string, name: string, timeoutMs = 60_000) {
  await expect
    .poll(
      async () => (await capturedEmails(request)).filter(e => e.body.includes(subjectPart) && e.body.includes(name)).length,
      { timeout: timeoutMs }
    )
    .toBeGreaterThanOrEqual(1);
}

/** Reloads the details page until any run row shows an OK badge (a 5s heartbeat may already have missed again). */
async function waitForAnyOkRun(page: Page, timeoutMs = 30_000) {
  await expect
    .poll(
      async () => {
        await page.reload();
        return await page.locator('tbody.hawk-runs-tbody .hawk-badge-success').count();
      },
      { timeout: timeoutMs }
    )
    .toBeGreaterThanOrEqual(1);
}

/** Reloads the details page until the newest run row shows the given badge. */
async function waitForLatestRun(page: Page, badge: 'FAIL', timeoutMs = 60_000) {
  await expect
    .poll(
      async () => {
        await page.reload();
        const first = page.locator('tbody.hawk-runs-tbody tr').first();
        if ((await first.count()) === 0) return '';
        return ((await first.locator('.hawk-badge').first().textContent()) ?? '').trim();
      },
      { timeout: timeoutMs }
    )
    .toBe(badge);
}

test('heartbeat: ping records OK, a missed ping alerts, the next ping recovers; Run now is absent', async ({ page, request }) => {
  await request.post(`${MOCK_BASE}/emails/reset`);
  await loginAsSeedAdmin(page);

  const name = `Heartbeat ${Date.now()}`;

  await page.goto('/Monitors/Create');
  await expect(page.getByRole('heading', { name: /new monitor/i })).toBeVisible();
  await page.getByLabel(/^monitor type$/i).selectOption('Heartbeat');
  // HTTP-only fields disappear, the heartbeat fields show up.
  await expect(page.getByLabel(/^url$/i)).toBeHidden();
  await page.getByLabel(/^name$/i).fill(name);
  await page.getByLabel(/^expected every$/i).selectOption('5');
  await page.getByLabel(/^grace$/i).selectOption('0');
  await snap(page, '50-heartbeat-create-form');
  await page.getByRole('button', { name: /^create$/i }).click();

  // 1. Details shows the ping URL and a curl example; there is no Run now.
  await expect(page.getByRole('heading', { name })).toBeVisible();
  const pingUrl = await page.locator('#ping-url').inputValue();
  expect(pingUrl).toMatch(/\/ping\/[0-9a-f]{32}$/);
  await expect(page.locator('#ping-curl')).toContainText(`curl -fsS -m 10 --retry 3 ${pingUrl}`);
  await expect(page.getByRole('button', { name: /run now/i })).toHaveCount(0);
  await snap(page, '51-heartbeat-details');
  const pingPath = new URL(pingUrl).pathname;

  // Unknown and malformed tokens are 404.
  expect((await request.get(`/ping/${'0'.repeat(32)}`)).status()).toBe(404);
  expect((await request.get('/ping/not-a-token')).status()).toBe(404);

  // 2. A ping is an OK run.
  const ping = await request.get(pingPath);
  expect(ping.status()).toBe(200);
  expect((await ping.text()).trim()).toBe('OK');
  await waitForAnyOkRun(page);
  await expect(page.locator('#last-ping')).toBeVisible();

  // 3. No further ping: a FAIL run and the failure email.
  await waitForLatestRun(page, 'FAIL');
  await expect(page.locator('tbody.hawk-runs-tbody')).toContainText(/no ping received/i);
  await waitForEmail(request, '[ALERT FAIL]', name);
  await snap(page, '52-heartbeat-missed');

  // 4. The next ping recovers.
  expect((await request.post(pingPath)).status()).toBe(200);
  await waitForEmail(request, '[ALERT RECOVERED]', name);

  // Clean up so the 5s heartbeat does not keep alerting during later tests.
  await page.goto('/Monitors');
  await page.getByRole('link', { name }).first().click();
  await page.getByRole('link', { name: /^edit$/i }).click();
  await page.getByRole('link', { name: /^delete$/i }).click();
  await page.getByRole('button', { name: /^delete$/i }).click();
  await expect(page.getByRole('heading', { name: /^monitors$/i })).toBeVisible();
});
