/**
 * @file E2E coverage for email confirmation.
 *
 * This suite:
 * - Registers a new (unconfirmed) user.
 * - Sends a confirmation email from the profile page.
 * - Opens the emailed link and verifies the account is confirmed (the token must keep its case).
 */
import { expect, test, type APIRequestContext } from '@playwright/test';

const MOCK_BASE = process.env.MOCK_BASE_URL ?? 'http://localhost:17801';

async function latestConfirmationLink(request: APIRequestContext, to: string): Promise<string> {
  const res = await request.get(`${MOCK_BASE}/emails`);
  expect(res.ok()).toBeTruthy();
  const emails: { body: string }[] = await res.json();
  const links = emails
    .map(e => JSON.parse(e.body) as { to: string | string[]; html?: string })
    .filter(m => [m.to].flat().includes(to))
    .map(m => /href='([^']+)'/.exec(m.html ?? '')?.[1])
    .filter((l): l is string => !!l);
  expect(links.length).toBeGreaterThan(0);
  return links[links.length - 1].replaceAll('&amp;', '&');
}

test('profile page sends a working confirmation email', async ({ page, request }) => {
  const email = `confirm-${Date.now()}@example.com`;
  const password = 'Hawk!2026-Confirm#1';

  await page.goto('/identity/account/register');
  await page.getByLabel(/^email$/i).fill(email);
  await page.locator('input[name="Input.Password"]').fill(password);
  await page.locator('input[name="Input.ConfirmPassword"]').fill(password);
  await page.locator('#registerSubmit').click();
  await page.waitForURL(url => !url.pathname.toLowerCase().includes('/register'));

  await request.post(`${MOCK_BASE}/emails/reset`);
  await page.goto('/identity/account/manage');
  await expect(page.getByText('Unconfirmed')).toBeVisible();
  await page.getByRole('button', { name: /send confirmation email/i }).click();
  await expect(page.getByText(`Confirmation email sent to ${email}.`)).toBeVisible();

  const link = await latestConfirmationLink(request, email);
  // Base64Url tokens are case-sensitive; a lowercased link can never confirm.
  expect(new URL(link).searchParams.get('code')).toMatch(/[A-Z]/);

  await page.goto(link);
  await expect(page.getByText('Thank you for confirming your email.')).toBeVisible();

  await page.goto('/identity/account/manage');
  await expect(page.getByText('Confirmed', { exact: true })).toBeVisible();
});
