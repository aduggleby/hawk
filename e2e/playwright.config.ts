import { defineConfig, devices } from '@playwright/test';

const PORT = process.env.HAWK_E2E_PORT ?? '5199';
const baseURL = process.env.HAWK_BASE_URL ?? `http://127.0.0.1:${PORT}`;

export default defineConfig({
  testDir: './tests',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 2 : undefined,
  reporter: 'list',
  use: {
    baseURL,
    trace: 'on-first-retry',
  },
  webServer: process.env.HAWK_BASE_URL
    ? undefined
    : {
        command: `dotnet run --project ../Hawk.Web --no-build --no-launch-profile`,
        url: baseURL,
        reuseExistingServer: !process.env.CI,
        timeout: 120_000,
        env: {
          ASPNETCORE_URLS: baseURL,
          ASPNETCORE_ENVIRONMENT: 'Development',
          Hawk__DisableHttpsRedirection: 'true',
          // Keep deterministic even if appsettings changes later.
          Hawk__SeedAdmin__Email: process.env.HAWK_SEED_EMAIL ?? 'ad@dualconsult.com',
          Hawk__SeedAdmin__Password: process.env.HAWK_SEED_PASSWORD ?? 'Hawk!2026-Admin#1',
        },
      },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
});
