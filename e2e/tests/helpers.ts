import type { Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

/**
 * Writes a screenshot to SCREENSHOTS_DIR (default: ../screenshots) with a stable filename.
 */
export async function snap(page: Page, name: string) {
  const dir = process.env.SCREENSHOTS_DIR ?? path.resolve(process.cwd(), '..', 'screenshots');
  fs.mkdirSync(dir, { recursive: true });
  const safe = name.replace(/[^a-z0-9._-]+/gi, '_').toLowerCase();
  const file = path.join(dir, `${safe}.png`);
  for (let attempt = 1; attempt <= 3; attempt++) {
    try {
      await page.screenshot({ path: file, fullPage: true });
      return;
    } catch (err) {
      if (attempt === 3) {
        // Screenshot failures can be intermittent in dockerized headed runs; don't fail the whole suite.
        // Tests still validate behavior; screenshots are best-effort.
        // eslint-disable-next-line no-console
        console.warn(`snap('${name}') failed:`, err);
        return;
      }

      await page.waitForTimeout(250 * attempt);
    }
  }
}

export async function loginAsSeedAdmin(page: Page) {
  const email = process.env.HAWK_SEED_EMAIL ?? 'ad@dualconsult.com';
  const password = process.env.HAWK_SEED_PASSWORD ?? 'Hawk!2026-Admin#1';

  await page.goto('/');
  await snap(page, '01-home');

  await page.getByRole('link', { name: /^login$/i }).click();
  await snap(page, '02-login');

  await page.getByLabel(/email/i).fill(email);
  await page.getByLabel(/password/i).fill(password);
  await page.getByRole('button', { name: /log in/i }).click();

  // Wait until authenticated UI is visible.
  await page.getByRole('button', { name: /^logout$/i }).waitFor();
}
