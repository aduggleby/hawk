/**
 * @file Playwright config for Hawk end-to-end tests.
 *
 * Notes:
 * - By default, we start the ASP.NET app via `dotnet run` (unless `HAWK_BASE_URL` is provided).
 * - We disable HTTPS redirection in the app for E2E stability.
 * - Seed admin credentials are injected via env so E2E doesn't depend on repo config.
 */
import { defineConfig, devices } from '@playwright/test';

const baseURL = process.env.HAWK_BASE_URL ?? 'http://127.0.0.1:8080';
const runningInDocker = ['1', 'true', 'yes'].includes((process.env.E2E_DOCKER ?? '').toLowerCase());
const dockerWorkers = Number.parseInt(process.env.PLAYWRIGHT_WORKERS ?? '', 10);
const workers =
  Number.isFinite(dockerWorkers) && dockerWorkers > 0
    ? dockerWorkers
    : runningInDocker
      ? 1
      : undefined;

export default defineConfig({
  testDir: './tests',
  // Docker runs share a single DB instance by default; run serially to avoid flakes.
  fullyParallel: runningInDocker ? false : true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 2 : workers,
  reporter: 'list',
  timeout: 120_000,
  use: {
    baseURL,
    headless: false,
    viewport: { width: 1280, height: 720 },
    trace: 'on-first-retry',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
});
