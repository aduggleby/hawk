/**
 * @file Basic E2E smoke coverage.
 *
 * This suite:
 * - Verifies the app starts and renders.
 * - Verifies the seeded admin can authenticate through the UI.
 */
import { expect, test } from '@playwright/test';
import { loginAsSeedAdmin, snap } from './helpers';

test('home page loads', async ({ page }) => {
  await page.goto('/');
  await snap(page, '01-home');
  await expect(page).toHaveTitle(/Hawk/i);
  // Be specific: home has both "Monitors" (nav) and "Open monitors" (CTA).
  await expect(page.getByRole('link', { name: /^monitors$/i })).toBeVisible();
});

test('seed admin can log in', async ({ page }) => {
  await loginAsSeedAdmin(page);

  await expect(page.getByRole('button', { name: /^logout$/i })).toBeVisible();
  await snap(page, '03-after-login');
});
