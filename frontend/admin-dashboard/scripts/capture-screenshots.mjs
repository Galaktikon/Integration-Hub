import { chromium } from '@playwright/test'
import { mkdir } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const password = process.env.E2E_ADMIN_PASSWORD
if (!password) throw new Error('Set E2E_ADMIN_PASSWORD before capturing demo screenshots.')
const output = resolve(dirname(fileURLToPath(import.meta.url)), '../../../docs/screenshots')
await mkdir(output, { recursive: true })
const browser = await chromium.launch()
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 })
  await page.goto(process.env.E2E_BASE_URL || 'http://localhost:5173')
  await page.getByRole('textbox', { name: 'Username' }).fill(process.env.E2E_ADMIN_USERNAME || 'admin')
  await page.getByRole('textbox', { name: 'Password' }).fill(password)
  await page.getByRole('button', { name: /Sign in/ }).click()
  await page.getByRole('heading', { name: 'Operations' }).waitFor()
  await page.getByRole('heading', { name: 'All integrations' }).waitFor()
  await page.waitForTimeout(500)
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).waitFor()
  await page.screenshot({ path: resolve(output, 'operations.png') })

  await page.getByRole('button', { name: '+ New integration', exact: true }).click()
  await page.getByRole('textbox', { name: 'Integration name' }).fill('Commerce Orders → ERP')
  await page.getByLabel('Source connector').selectOption({ label: 'Demo Commerce' })
  await page.getByLabel('Destination connector').selectOption({ label: 'Demo ERP' })
  await page.getByRole('button', { name: /Continue to field mapping/ }).click()
  await page.screenshot({ path: resolve(output, 'integration-setup.png') })
  await page.getByRole('button', { name: 'Close' }).click()

  await page.getByRole('button', { name: 'Run history', exact: true }).click()
  await page.locator('.run-row').first().click()
  await page.getByRole('heading', { name: 'Run progress' }).waitFor()
  await page.screenshot({ path: resolve(output, 'run-details.png') })
} finally {
  await browser.close()
}
