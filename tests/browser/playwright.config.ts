import { defineConfig } from '@playwright/test';
import path from 'node:path';

const results = path.resolve(process.env.HN_BROWSER_RESULTS || '../../TestResults/browser');

export default defineConfig({
  testDir: '.',
  testMatch: 'stories.spec.ts',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: true,
  timeout: 60_000,
  globalTimeout: 180_000,
  expect: { timeout: 10_000 },
  outputDir: path.join(results, 'artifacts'),
  reporter: [
    ['line'],
    ['html', { outputFolder: path.join(results, 'html'), open: 'never' }],
    ['junit', { outputFile: path.join(results, 'junit.xml') }],
  ],
  use: {
    browserName: 'chromium',
    headless: true,
    actionTimeout: 10_000,
    navigationTimeout: 30_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
});