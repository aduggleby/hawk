/**
 * @file End-to-end tests for importing StatusCake exports.
 */

import { test, expect } from '@playwright/test';
import { loginAsSeedAdmin, snap } from './helpers';

test('import StatusCake tests and alerts', async ({ page }) => {
  await loginAsSeedAdmin(page);

  await page.goto('/Admin/Import/StatusCake');
  await expect(page.getByRole('heading', { name: 'Import: StatusCake' })).toBeVisible();

  const testsExport = JSON.stringify(
    {
      data: [
        {
          id: '2001',
          name: 'SC OK',
          website_url: `${process.env.MOCK_BASE_URL ?? 'http://localhost:8081'}/ok`,
          check_rate: 60,
          paused: false,
        },
        {
          id: '2002',
          name: 'SC Match',
          website_url: `${process.env.MOCK_BASE_URL ?? 'http://localhost:8081'}/nomatch`,
          check_rate: 240,
          paused: true,
          find_string: 'hello',
        },
      ],
    },
    null,
    2,
  );

  await page.getByLabel('Import type').selectOption('tests');
  await page.getByLabel('JSON file').setInputFiles({
    name: 'statuscake-uptime.json',
    mimeType: 'application/json',
    buffer: Buffer.from(testsExport, 'utf-8'),
  });

  await page.getByRole('button', { name: 'Import' }).click();
  await expect(page.getByTestId('import-monitors-created')).toHaveText('2');

  await snap(page, '30-statuscake-import-tests');

  await page.goto('/Monitors');
  await expect(page.getByText('SC OK (sc:2001)')).toBeVisible();
  await expect(page.getByText('SC Match (sc:2002)')).toBeVisible();

  const alertsExport = JSON.stringify(
    [
      {
        test_id: '2001',
        data: [
          {
            status: 'down',
            status_code: 503,
            triggered_at: '2026-02-09T00:00:00Z',
          },
        ],
      },
    ],
    null,
    2,
  );

  await page.goto('/Admin/Import/StatusCake');
  await page.getByLabel('Import type').selectOption('alerts');
  await page.getByLabel('JSON file').setInputFiles({
    name: 'statuscake-alerts.json',
    mimeType: 'application/json',
    buffer: Buffer.from(alertsExport, 'utf-8'),
  });
  await page.getByRole('button', { name: 'Import' }).click();
  await expect(page.getByTestId('import-runs-created')).toHaveText('1');

  await snap(page, '31-statuscake-import-alerts');

  // Verify imported run appears in monitor details.
  await page.goto('/Monitors');
  const row = page.locator('tr', { hasText: 'SC OK (sc:2001)' });
  await row.getByRole('link', { name: 'View' }).click();
  await expect(page.getByText('Imported StatusCake alert')).toBeVisible();
});
