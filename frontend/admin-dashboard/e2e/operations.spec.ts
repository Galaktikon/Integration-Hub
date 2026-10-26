import { expect, test } from '@playwright/test'

const username = process.env.E2E_ADMIN_USERNAME || 'admin'
const password = process.env.E2E_ADMIN_PASSWORD

test.beforeEach(async ({ page }) => {
  if (!password) throw new Error('Set E2E_ADMIN_PASSWORD for browser smoke tests.')
  await page.goto('/')
  await page.getByRole('textbox', { name: 'Username' }).fill(username)
  await page.getByRole('textbox', { name: 'Password' }).fill(password)
  await page.getByRole('button', { name: /Sign in/ }).click()
  await expect(page.getByRole('heading', { name: 'Operations' })).toBeVisible()
})

test('administrator can navigate the protected operations views', async ({ page }) => {
  await expect(page.getByText('All integrations')).toBeVisible()
  await page.getByRole('button', { name: 'Connectors', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Connectors', level: 1 })).toBeVisible()
  await page.getByRole('button', { name: 'Run history', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Run history', level: 1 })).toBeVisible()
  await page.getByRole('button', { name: /Sign out/ }).click()
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()
})

test('operator can configure and launch a synthetic flow', async ({ page }) => {
  test.skip(process.env.E2E_MUTATE !== '1', 'Requires a disposable seeded demo environment.')
  const flowName = `Browser smoke ${Date.now()}`
  await page.getByRole('button', { name: '+ New integration', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'New integration' })).toBeVisible()
  await page.getByRole('textbox', { name: 'Integration name' }).fill(flowName)
  await page.getByLabel('Source connector').selectOption({ label: 'Demo Commerce' })
  await page.getByLabel('Destination connector').selectOption({ label: 'Demo ERP' })
  await page.getByRole('button', { name: /Continue to field mapping/ }).click()
  await expect(page.getByText('4 of 4 fields mapped')).toBeVisible()
  await page.getByRole('button', { name: /Continue to validation/ }).click()
  await page.getByLabel('Enabled after saving').check()
  await page.getByRole('button', { name: 'Save integration' }).click()
  await expect(page.getByRole('heading', { name: 'Operations' })).toBeVisible()
  await page.getByRole('button', { name: 'Integrations', exact: true }).click()
  await page.getByRole('button', { name: new RegExp(flowName) }).click()
  await page.getByRole('button', { name: /Run now/ }).click()
  await expect(page.getByRole('heading', { name: 'Run history', level: 1 })).toBeVisible()
  await expect(page.getByText(flowName).first()).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Run progress' })).toBeVisible({ timeout: 30_000 })
})
