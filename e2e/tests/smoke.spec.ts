/**
 * @file Basic E2E smoke coverage.
 *
 * This suite:
 * - Verifies the app starts and renders.
 * - Verifies the seeded admin can authenticate through the UI.
 */
import { expect, test } from '@playwright/test';

test('home page loads', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveTitle(/Hawk\.Web/i);
  await expect(page.getByRole('link', { name: /home/i })).toBeVisible();
});

test('seed admin can log in', async ({ page }) => {
  const email = process.env.HAWK_SEED_EMAIL ?? 'ad@dualconsult.com';
  const password = process.env.HAWK_SEED_PASSWORD ?? 'Hawk!2026-Admin#1';

  await page.goto('/');
  await page.getByRole('link', { name: /^login$/i }).click();

  // Branch: on Identity UI, labels are localized; we match by regex.
  await page.getByLabel(/email/i).fill(email);
  await page.getByLabel(/password/i).fill(password);
  await page.getByRole('button', { name: /log in/i }).click();

  await expect(page.getByRole('button', { name: /^logout$/i })).toBeVisible();
  await expect(page.getByRole('link', { name: /hello/i })).toBeVisible();
});
