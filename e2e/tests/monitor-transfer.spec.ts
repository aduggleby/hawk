/**
 * @file E2E coverage for admin bulk export and import of all monitors.
 */

import { expect, test, type Page } from '@playwright/test';
import fs from 'node:fs';
import { loginAsSeedAdmin, snap } from './helpers';

const MOCK_BASE = process.env.MOCK_BASE_URL ?? 'http://mock:8081';

async function openSection(page: Page, title: string) {
  const details = page.locator(`details:has(summary:has-text("${title}"))`).first();
  const isOpen = await details.evaluate(el => el.hasAttribute('open'));
  if (!isOpen) {
    await details.locator('summary').click();
  }
}

async function createMonitor(
  page: Page,
  name: string,
  opts: { header?: [string, string]; rule?: [string, string] },
): Promise<string> {
  await page.goto('/Monitors/Create');
  await page.getByLabel(/^name$/i).fill(name);
  await page.getByLabel(/^url$/i).fill(`${MOCK_BASE}/ok`);
  await page.getByLabel(/^interval$/i).selectOption('60');

  if (opts.header) {
    await openSection(page, 'Headers');
    await page.locator('input[name="Form.HeaderNames[0]"]').fill(opts.header[0]);
    await page.locator('input[name="Form.HeaderValues[0]"]').fill(opts.header[1]);
  }

  if (opts.rule) {
    await openSection(page, 'Match rules');
    await page.locator('select[name="Form.MatchModes[0]"]').selectOption(opts.rule[0]);
    await page.locator('input[name="Form.MatchPatterns[0]"]').fill(opts.rule[1]);
  }

  await page.getByRole('button', { name: /^create$/i }).click();
  await expect(page.getByRole('heading', { name: new RegExp(name, 'i') })).toBeVisible();
  const id = page.url().split('/').filter(Boolean).at(-1);
  expect(id).toBeTruthy();
  return id!;
}

async function deleteMonitor(page: Page, id: string) {
  await page.goto(`/Monitors/Delete/${id}`);
  await page.getByRole('button', { name: /^delete$/i }).click();
  await page.waitForURL(url => !url.pathname.toLowerCase().includes('/delete/'));
}

test('export all monitors, delete two, preview and import them back paused, then skip as duplicates', async ({ page }) => {
  await loginAsSeedAdmin(page);

  const stamp = Date.now();
  const nameA = `Transfer A ${stamp}`;
  const nameB = `Transfer B ${stamp}`;
  const idA = await createMonitor(page, nameA, { header: ['X-Transfer', 'alpha'], rule: ['Contains', 'Example Domain'] });
  const idB = await createMonitor(page, nameB, { rule: ['Regex', 'Exam+ple'] });

  // Rules match the mock /ok body so these monitors never produce failing runs.
  // Export all.
  await page.goto('/Admin/Transfer');
  await expect(page.getByRole('heading', { name: 'Export / Import monitors' })).toBeVisible();
  await snap(page, '40-transfer-page');

  const [download] = await Promise.all([
    page.waitForEvent('download'),
    page.getByRole('link', { name: 'Export all monitors' }).click(),
  ]);
  expect(download.suggestedFilename()).toMatch(/^hawk-monitors-\d{8}-\d{6}\.json$/);
  const exportPath = await download.path();
  const exportBuffer = fs.readFileSync(exportPath);
  const exported = JSON.parse(exportBuffer.toString('utf-8')) as { version: number; monitors: { name: string }[] };
  expect(exported.version).toBe(2);
  const names = exported.monitors.map(m => m.name);
  expect(names).toContain(nameA);
  expect(names).toContain(nameB);
  const total = exported.monitors.length;

  // Delete the two monitors.
  await deleteMonitor(page, idA);
  await deleteMonitor(page, idB);
  await page.goto('/Monitors');
  await expect(page.getByText(nameA)).toHaveCount(0);
  await expect(page.getByText(nameB)).toHaveCount(0);

  // Preview import.
  const upload = { name: 'hawk-monitors.json', mimeType: 'application/json', buffer: exportBuffer };
  await page.goto('/Admin/Transfer');
  await page.getByLabel('JSON file').setInputFiles(upload);
  await page.getByRole('button', { name: 'Preview import' }).click();

  await expect(page.getByTestId('preview-total')).toHaveText(String(total));
  await expect(page.getByTestId('preview-new')).toHaveText('2');
  await expect(page.getByTestId('preview-duplicates')).toHaveText(String(total - 2));
  await expect(page.getByTestId('preview-invalid')).toHaveText('0');
  await expect(page.getByTestId('preview-owners')).toContainText('2 matched an existing user by email');
  await expect(page.getByLabel(/create all imported monitors paused/i)).toBeChecked();
  await snap(page, '41-transfer-preview');

  // Nothing written yet.
  await page.goto('/Monitors');
  await expect(page.getByText(nameA)).toHaveCount(0);

  // Redo the preview and confirm.
  await page.goto('/Admin/Transfer');
  await page.getByLabel('JSON file').setInputFiles(upload);
  await page.getByRole('button', { name: 'Preview import' }).click();
  await page.getByRole('button', { name: 'Confirm import' }).click();
  await expect(page.getByText(`Imported 2 monitor(s), skipped ${total - 2} duplicate(s). Created paused: yes.`)).toBeVisible();
  await snap(page, '42-transfer-imported');

  // Both exist again, paused, with header and rules.
  await page.goto('/Monitors');
  const linkA = page.getByRole('link', { name: nameA }).first();
  await expect(linkA).toBeVisible();
  await linkA.click();
  await expect(page.getByRole('heading', { name: new RegExp(nameA, 'i') })).toBeVisible();
  await expect(page.getByText('Paused', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('X-Transfer: alpha')).toBeVisible();
  await expect(page.getByText('Contains: Example Domain')).toBeVisible();

  await page.goto('/Monitors');
  await page.getByRole('link', { name: nameB }).first().click();
  await expect(page.getByRole('heading', { name: new RegExp(nameB, 'i') })).toBeVisible();
  await expect(page.getByText('Paused', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Regex: Exam+ple')).toBeVisible();

  // Importing again skips everything.
  await page.goto('/Admin/Transfer');
  await page.getByLabel('JSON file').setInputFiles(upload);
  await page.getByRole('button', { name: 'Preview import' }).click();
  await expect(page.getByTestId('preview-new')).toHaveText('0');
  await expect(page.getByTestId('preview-duplicates')).toHaveText(String(total));
  await snap(page, '43-transfer-duplicates');
  await page.getByRole('button', { name: 'Confirm import' }).click();
  await expect(page.getByText(`Imported 0 monitor(s), skipped ${total} duplicate(s). Created paused: yes.`)).toBeVisible();
});
