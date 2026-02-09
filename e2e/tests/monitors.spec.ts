/**
 * @file E2E coverage for monitor CRUD and execution, using the mock server for deterministic behavior.
 */

import { expect, test } from '@playwright/test';
import { loginAsSeedAdmin, snap } from './helpers';

const MOCK_BASE = process.env.MOCK_BASE_URL ?? 'http://mock:8081';

async function pollForAtLeastRuns(page, minRuns: number, timeoutMs: number) {
  await expect.poll(
    async () => {
      await page.reload();
      return await page.locator('tbody tr').count();
    },
    { timeout: timeoutMs }
  ).toBeGreaterThanOrEqual(minRuns);
}

async function gotoMonitors(page) {
  // Navigate directly to avoid relying on responsive nav rendering.
  await page.goto('/Monitors');
  await expect(page.getByRole('heading', { name: /monitors/i })).toBeVisible();
  await snap(page, '10-monitors-index');
}

test('create GET monitor (contains) and observe scheduled runs (5s interval in Testing)', async ({ page }) => {
  await loginAsSeedAdmin(page);
  await gotoMonitors(page);

  await page.getByRole('link', { name: /new monitor/i }).click();
  await expect(page.getByRole('heading', { name: /new monitor/i })).toBeVisible();
  await snap(page, '11-monitor-create-form');

  await page.getByLabel(/^name$/i).fill('GET ok contains');
  await page.getByLabel(/^url$/i).fill(`${MOCK_BASE}/ok`);
  await page.getByLabel(/^method$/i).selectOption('GET');

  // Testing-only interval should include 5s.
  await page.getByLabel(/^interval$/i).selectOption('5');

  // First match rule: contains "Example Domain" (present in /ok).
  await page.locator('input[name="Form.MatchPatterns[0]"]').fill('Example Domain');
  await page.locator('select[name="Form.MatchModes[0]"]').selectOption('Contains');

  await page.getByRole('button', { name: /^create$/i }).click();
  await expect(page.getByRole('heading', { name: /GET ok contains/i })).toBeVisible();
  await snap(page, '12-monitor-details-created');

  // Wait until at least 2 runs are present (page must be reloaded, since this isn't live-updating yet).
  await pollForAtLeastRuns(page, 2, 40_000);
  await snap(page, '13-monitor-details-with-runs');
});

test('create POST monitor with headers/body and run now succeeds', async ({ page }) => {
  await loginAsSeedAdmin(page);
  await gotoMonitors(page);

  await page.getByRole('link', { name: /new monitor/i }).click();
  await page.getByLabel(/^name$/i).fill('POST echo');
  await page.getByLabel(/^url$/i).fill(`${MOCK_BASE}/echo`);
  await page.getByLabel(/^method$/i).selectOption('POST');
  await page.getByLabel(/^interval$/i).selectOption('60');
  await page.getByLabel(/content-type/i).fill('application/json');
  await page.getByLabel(/^body$/i).fill('{"hello":"world"}');
  await page.locator('input[name="Form.HeaderNames[0]"]').fill('X-Test');
  await page.locator('input[name="Form.HeaderValues[0]"]').fill('hawk');
  // /echo returns JSON with the request body as an escaped string; simplest assertion is that "hello" appears somewhere.
  await page.locator('select[name="Form.MatchModes[0]"]').selectOption('Contains');
  await page.locator('input[name="Form.MatchPatterns[0]"]').fill('hello');

  await page.getByRole('button', { name: /^create$/i }).click();
  await snap(page, '20-post-monitor-details');

  await page.getByRole('button', { name: /^run now$/i }).click();
  await pollForAtLeastRuns(page, 1, 30_000);
  await expect(page.locator('tbody tr').first()).toContainText('OK');
  await snap(page, '21-post-monitor-after-run');
});

test('error states: non-2xx and match failure trigger a FAIL result (and alert is sent)', async ({ page, request }) => {
  await loginAsSeedAdmin(page);
  await gotoMonitors(page);

  // Non-2xx error
  await page.getByRole('link', { name: /new monitor/i }).click();
  await page.getByLabel(/^name$/i).fill('GET error 500');
  await page.getByLabel(/^url$/i).fill(`${MOCK_BASE}/error`);
  await page.getByLabel(/^method$/i).selectOption('GET');
  await page.getByLabel(/^interval$/i).selectOption('60');
  await page.getByRole('button', { name: /^create$/i }).click();
  await page.getByRole('button', { name: /^run now$/i }).click();
  await pollForAtLeastRuns(page, 1, 30_000);
  await expect(page.locator('tbody tr').first()).toContainText('FAIL');
  await snap(page, '30-error-500-fail');

  // Email alert should be sent to the Resend-compatible mock endpoint.
  const emailsRes = await request.get(`${MOCK_BASE}/emails`);
  expect(emailsRes.ok()).toBeTruthy();
  const emails = await emailsRes.json();
  expect(Array.isArray(emails)).toBeTruthy();
  expect(emails.length).toBeGreaterThan(0);

  // Match failure (new monitor)
  await gotoMonitors(page);
  await page.getByRole('link', { name: /new monitor/i }).click();
  await page.getByLabel(/^name$/i).fill('GET nomatch contains');
  await page.getByLabel(/^url$/i).fill(`${MOCK_BASE}/nomatch`);
  await page.getByLabel(/^method$/i).selectOption('GET');
  await page.getByLabel(/^interval$/i).selectOption('60');
  await page.locator('select[name="Form.MatchModes[0]"]').selectOption('Contains');
  await page.locator('input[name="Form.MatchPatterns[0]"]').fill('Example Domain');
  await page.getByRole('button', { name: /^create$/i }).click();
  await page.getByRole('button', { name: /^run now$/i }).click();
  await pollForAtLeastRuns(page, 1, 30_000);
  await expect(page.locator('tbody tr').first()).toContainText('FAIL');
  await snap(page, '31-nomatch-fail');
});

test('alert threshold: only alert after N consecutive failures', async ({ page, request }) => {
  await loginAsSeedAdmin(page);
  await gotoMonitors(page);

  // Reset captured emails so this test is deterministic.
  await request.post(`${MOCK_BASE}/emails/reset`);

  await page.getByRole('link', { name: /new monitor/i }).click();
  await page.getByLabel(/^name$/i).fill('Threshold 2');
  await page.getByLabel(/^url$/i).fill(`${MOCK_BASE}/error`);
  await page.getByLabel(/^method$/i).selectOption('GET');
  await page.getByLabel(/^interval$/i).selectOption('60');
  await page.getByLabel(/alert after consecutive failures/i).fill('2');

  await page.getByRole('button', { name: /^create$/i }).click();
  await snap(page, '40-threshold-details-created');

  // Run 1: fail, but no alert yet.
  await page.getByRole('button', { name: /^run now$/i }).click();
  await pollForAtLeastRuns(page, 1, 30_000);
  let emailsRes = await request.get(`${MOCK_BASE}/emails`);
  let emails = await emailsRes.json();
  expect(emails.length).toBe(0);

  // Run 2: second consecutive failure should alert.
  await page.getByRole('button', { name: /^run now$/i }).click();
  await pollForAtLeastRuns(page, 2, 30_000);
  emailsRes = await request.get(`${MOCK_BASE}/emails`);
  emails = await emailsRes.json();
  expect(emails.length).toBe(1);
});
