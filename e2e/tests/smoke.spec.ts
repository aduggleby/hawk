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

  await expect(page.getByText(/^account$/i).first()).toBeVisible();
  await expect(page.getByRole('heading', { name: /^monitors$/i })).toBeVisible();
  await snap(page, '03-after-login');
});

test('login button is disabled after a valid submit and an empty submit keeps it usable', async ({ page }) => {
  const email = process.env.HAWK_SEED_EMAIL ?? 'ad@dualconsult.com';
  const password = process.env.HAWK_SEED_PASSWORD ?? 'Hawk!2026-Admin#1';
  await page.goto('/Identity/Account/Login');
  const button = page.locator('#login-submit');

  // Client validation blocks the submit, so the button must stay enabled.
  await button.click();
  await expect(button).toBeEnabled();

  await page.getByLabel(/email/i).fill(email);
  await page.getByLabel(/password/i).fill(password);

  // Hold the POST so the page stays on screen after the submit. While a navigation is pending, Playwright's
  // locators and page.evaluate do not see the live document, so the page reports the button state via console.
  let release: () => void = () => {};
  const held = new Promise<void>(resolve => { release = resolve; });
  await page.route(/\/identity\/account\/login/i, async route => {
    if (route.request().method() === 'POST') await held;
    await route.continue();
  });
  await page.evaluate(() => document.getElementById('account')!.addEventListener('submit', () =>
    setTimeout(() => console.log('hawk-submit-disabled=' + (document.getElementById('login-submit') as HTMLButtonElement).disabled), 50)));
  const reported = page.waitForEvent('console', m => m.text().startsWith('hawk-submit-disabled='));
  // click() waits for the navigation it starts, which is held, so await it only after releasing.
  const clicked = button.click();
  expect((await reported).text()).toBe('hawk-submit-disabled=true');
  release();
  await clicked;
  await page.waitForURL(url => !url.pathname.toLowerCase().includes('/identity/account/login'));
  await expect(page.getByRole('heading', { name: /^monitors$/i })).toBeVisible();
});
