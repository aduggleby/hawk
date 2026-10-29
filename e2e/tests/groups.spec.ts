/**
 * @file E2E coverage for monitor groups: group field on Create, group sections on the index, group Pause, and the
 * batch Resume across two groups (which must still post to the batch handler).
 */

import { expect, test } from '@playwright/test';
import type { Locator, Page } from '@playwright/test';
import { loginAsSeedAdmin, snap } from './helpers';

const MOCK_BASE = process.env.MOCK_BASE_URL ?? 'http://mock:8081';

async function createMonitor(page: Page, name: string, group: string) {
  await page.goto('/Monitors/Create');
  await expect(page.getByRole('heading', { name: /new monitor/i })).toBeVisible();
  await page.getByLabel(/^name$/i).fill(name);
  await page.getByLabel(/^group$/i).fill(group);
  await page.getByLabel(/^url$/i).fill(`${MOCK_BASE}/ok`);
  await page.getByRole('button', { name: /^create$/i }).click();
  await expect(page.getByRole('heading', { name })).toBeVisible();
  // The group shows as a breadcrumb on Details.
  await expect(page.getByRole('navigation', { name: /breadcrumb/i }).getByRole('link', { name: group })).toBeVisible();
}

/** The group section card on the monitors index: the card whose header names the group. */
function section(page: Page, group: string): Locator {
  return page.locator('section.hawk-bgroup', { has: page.getByRole('heading', { name: group, exact: true }) });
}

test('groups: sections, group pause, and batch resume across two groups', async ({ page }) => {
  await loginAsSeedAdmin(page);
  const stamp = Date.now();
  const groupA = `E2E Group A ${stamp}`;
  const groupB = `E2E Group B ${stamp}`;
  const a1 = `Group A one ${stamp}`;
  const a2 = `Group A two ${stamp}`;
  const b1 = `Group B one ${stamp}`;

  await createMonitor(page, a1, groupA);
  await createMonitor(page, a2, groupA);
  await createMonitor(page, b1, groupB);

  // The Edit form shows the saved group.
  await page.getByRole('link', { name: /^edit$/i }).click();
  await expect(page.getByLabel(/^group$/i)).toHaveValue(groupB);

  await page.goto('/Monitors');
  const cardA = section(page, groupA);
  const cardB = section(page, groupB);
  await expect(cardA).toContainText('2 monitor(s)');
  await expect(cardA.getByRole('link', { name: a1 })).toBeVisible();
  await expect(cardA.getByRole('link', { name: a2 })).toBeVisible();
  await expect(cardB).toContainText('1 monitor(s)');
  await snap(page, '60-groups-index');

  // Group Pause pauses both members of group A and leaves group B alone.
  await cardA.getByRole('button', { name: /^pause$/i }).click();
  await expect(page.getByText(new RegExp(`Paused 2 monitor\\(s\\) in ${groupA}`))).toBeVisible();
  await expect(section(page, groupA).locator('[data-status="paused"]')).toHaveCount(2);
  await expect(section(page, groupB).locator('[data-status="paused"]')).toHaveCount(0);

  // Resume only counts paused monitors, so pause group B as well before the cross-group batch resume.
  await cardB.getByRole('button', { name: /^pause$/i }).click();
  await expect(page.getByText(new RegExp(`Paused 1 monitor\\(s\\) in ${groupB}`))).toBeVisible();
  await expect(section(page, groupB).locator('[data-status="paused"]')).toHaveCount(1);

  // Select one monitor in each group: the batch bar appears and Resume posts to the batch handler.
  await section(page, groupA).getByRole('row', { name: new RegExp(a1) }).locator('input[type=checkbox]').check();
  await section(page, groupB).getByRole('row', { name: new RegExp(b1) }).locator('input[type=checkbox]').check();
  await expect(page.locator('#monitors-selected-count')).toHaveText('2');
  await page.locator('#monitors-actions-selected').getByRole('button', { name: /^resume$/i }).click();
  await expect(page.getByText(/Resumed 2 monitor\(s\)\./)).toBeVisible();
  await expect(section(page, groupA).locator('[data-status="paused"]')).toHaveCount(1);

  // Group select-all checks every member and drives the batch bar.
  await section(page, groupA).locator('input[data-group-select]').check();
  await expect(page.locator('#monitors-selected-count')).toHaveText('2');
  await page.locator('#monitors-uncheck-all').click();

  // Rename moves the members.
  const renamed = `${groupA} renamed`;
  await section(page, groupA).getByRole('button', { name: /^rename$/i }).click();
  await page.locator('#group-rename-new').fill(renamed);
  await page.locator('#group-rename-form').getByRole('button', { name: /^rename$/i }).click();
  await expect(page.getByText(/Renamed group/)).toBeVisible();
  await expect(section(page, renamed)).toContainText('2 monitor(s)');
  await snap(page, '61-groups-renamed');
});
