import { useEffect } from 'react'
import { zodResolver } from '@hookform/resolvers/zod'
import { Controller, useForm } from 'react-hook-form'
import { useTranslation } from 'react-i18next'
import { parseAmount } from '@/lib/amount'
import { Button, Form, FormControl, FormLabel, Modal } from 'react-bootstrap'
import Select from 'react-select'
import { z } from 'zod'

import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { formatAccountOptionLabel, useAccountsQuery } from '@/features/accounts/api'
import { useCategoriesQuery } from '@/features/categories/api'

import {
  useCreateAlertRuleMutation,
  useUpdateAlertRuleMutation,
  type AlertRuleResponse,
  type AlertRuleType,
  type NotificationChannel,
} from './api'

const TYPE_OPTIONS: AlertRuleType[] = [
  'BudgetThreshold',
  'LowBalance',
  'InvoiceDue',
  'LargeTransaction',
  'NewRecurring',
  'SyncError',
  'GoalReached',
]

const CHANNEL_OPTIONS: NotificationChannel[] = ['Telegram', 'Email', 'Both']

interface SelectOption {
  value: string
  label: string
}

/** Per-type field visibility -- mirrors 08-05's own per-type validator requirements (Threshold for LowBalance/LargeTransaction, DaysBefore for InvoiceDue). */
function needsThreshold(type: AlertRuleType): boolean {
  return type === 'LowBalance' || type === 'LargeTransaction'
}

function needsDaysBefore(type: AlertRuleType): boolean {
  return type === 'InvoiceDue'
}

function needsCategory(type: AlertRuleType): boolean {
  return type === 'BudgetThreshold'
}

function needsAccount(type: AlertRuleType): boolean {
  return type === 'LowBalance' || type === 'InvoiceDue' || type === 'LargeTransaction'
}

const alertRuleFormSchema = z
  .object({
    type: z.enum([
      'BudgetThreshold',
      'LowBalance',
      'InvoiceDue',
      'LargeTransaction',
      'NewRecurring',
      'SyncError',
      'GoalReached',
    ]),
    threshold: z.string().optional(),
    categoryId: z.string().optional(),
    accountId: z.string().optional(),
    daysBefore: z.string().optional(),
    channel: z.enum(['Telegram', 'Email', 'Both']),
  })
  .superRefine((values, ctx) => {
    // Messages are translation keys (resolved via t() at render time), not
    // display text -- keeps this schema locale-independent since it's built
    // once at module scope, outside the component's render/hook lifecycle.
    if (needsThreshold(values.type)) {
      const threshold = parseAmount(values.threshold) ?? Number.NaN
      if (!values.threshold || Number.isNaN(threshold) || threshold <= 0) {
        ctx.addIssue({ code: 'custom', path: ['threshold'], message: 'form.thresholdError' })
      }
    }

    if (needsDaysBefore(values.type)) {
      const daysBefore = Number(values.daysBefore)
      if (
        !values.daysBefore ||
        Number.isNaN(daysBefore) ||
        !Number.isInteger(daysBefore) ||
        daysBefore < 1 ||
        daysBefore > 90
      ) {
        ctx.addIssue({ code: 'custom', path: ['daysBefore'], message: 'form.daysBeforeError' })
      }
    }
  })

type AlertRuleFormValues = z.infer<typeof alertRuleFormSchema>

export interface AlertRuleFormDialogProps {
  show: boolean
  onHide: () => void
  editTarget: AlertRuleResponse | null
  accessToken: string
}

/**
 * Create/edit alert rule dialog (FE-ALRT-01): Type/Channel selects plus
 * type-conditional Threshold/DaysBefore inputs and Category/Account
 * pickers, mirroring RuleFormDialog.tsx's exact Modal+react-hook-form+zod
 * structure. Category picker (BudgetThreshold only) and Account picker
 * (LowBalance/InvoiceDue/LargeTransaction) each offer an explicit "All
 * categories"/"All accounts" option (clearable react-select) mapping to
 * null. Client-side validation is a UX convenience layer only -- the
 * unchanged server validators from v1.0 Phase 8 remain the real enforcement
 * boundary (T-13-11, matching v1's own T-08-17 precedent).
 */
export function AlertRuleFormDialog({ show, onHide, editTarget, accessToken }: AlertRuleFormDialogProps) {
  const { t } = useTranslation(['alerts', 'common'])
  const { showNotification } = useNotificationContext()

  const categoriesQuery = useCategoriesQuery(accessToken)
  const accountsQuery = useAccountsQuery(accessToken)
  const categories = categoriesQuery.data ?? []
  const accounts = accountsQuery.data ?? []

  const createMutation = useCreateAlertRuleMutation()
  const updateMutation = useUpdateAlertRuleMutation()
  const isPending = createMutation.isPending || updateMutation.isPending

  const form = useForm<AlertRuleFormValues>({
    resolver: zodResolver(alertRuleFormSchema),
    defaultValues: {
      type: 'BudgetThreshold',
      threshold: '',
      categoryId: '',
      accountId: '',
      daysBefore: '',
      channel: 'Telegram',
    },
  })

  useEffect(() => {
    if (!show) return

    if (editTarget) {
      form.reset({
        type: editTarget.type,
        threshold: editTarget.threshold != null ? String(editTarget.threshold) : '',
        categoryId: editTarget.categoryId ?? '',
        accountId: editTarget.accountId ?? '',
        daysBefore: editTarget.daysBefore != null ? String(editTarget.daysBefore) : '',
        channel: editTarget.channel,
      })
    } else {
      form.reset({
        type: 'BudgetThreshold',
        threshold: '',
        categoryId: '',
        accountId: '',
        daysBefore: '',
        channel: 'Telegram',
      })
    }
  }, [show, editTarget, form])

  const type = form.watch('type')

  const categoryOptions: SelectOption[] = categories.map((category) => ({ value: category.id, label: category.name }))
  const accountOptions: SelectOption[] = accounts.map((account) => ({
    value: account.id,
    label: formatAccountOptionLabel(account),
  }))

  async function onSubmit(values: AlertRuleFormValues) {
    const fields = {
      type: values.type,
      threshold: needsThreshold(values.type) ? parseAmount(values.threshold) : null,
      categoryId: needsCategory(values.type) ? values.categoryId || null : null,
      accountId: needsAccount(values.type) ? values.accountId || null : null,
      daysBefore: needsDaysBefore(values.type) ? Number(values.daysBefore) : null,
      channel: values.channel,
      isActive: editTarget?.isActive ?? true,
    }

    try {
      if (editTarget) {
        await updateMutation.mutateAsync({ accessToken, id: editTarget.id, ...fields })
        showNotification({ message: t('alerts:toasts.updated'), variant: 'success' })
      } else {
        await createMutation.mutateAsync({ accessToken, ...fields })
        showNotification({ message: t('alerts:toasts.created'), variant: 'success' })
      }
      onHide()
    } catch {
      showNotification({ message: t('alerts:errors.save'), variant: 'danger' })
    }
  }

  return (
    <Modal show={show} onHide={onHide} centered>
      <Modal.Header closeButton>
        <Modal.Title>{editTarget ? t('alerts:form.titleEdit') : t('alerts:form.titleNew')}</Modal.Title>
      </Modal.Header>

      <Form onSubmit={form.handleSubmit(onSubmit)} noValidate>
        <Modal.Body>
          <p className="text-muted fs-sm mb-3">
            {editTarget ? t('alerts:form.subtitleEdit') : t('alerts:form.subtitleNew')}
          </p>

          <div className="mb-3">
            <FormLabel>{t('alerts:form.typeLabel')}</FormLabel>
            <Form.Select {...form.register('type')}>
              {TYPE_OPTIONS.map((option) => (
                <option key={option} value={option}>
                  {t(`alerts:types.${option}`)}
                </option>
              ))}
            </Form.Select>
          </div>

          {needsThreshold(type) && (
            <div className="mb-3">
              <FormLabel>{t('alerts:form.thresholdLabel')}</FormLabel>
              <FormControl
                type="text"
                inputMode="decimal"
                autoComplete="off"
                isInvalid={!!form.formState.errors.threshold}
                {...form.register('threshold')}
              />
              {form.formState.errors.threshold && (
                <div className="text-danger fs-xs mt-1">{t(form.formState.errors.threshold.message ?? '')}</div>
              )}
            </div>
          )}

          {needsDaysBefore(type) && (
            <div className="mb-3">
              <FormLabel>{t('alerts:form.daysBeforeLabel')}</FormLabel>
              <FormControl
                type="number"
                min={1}
                max={90}
                inputMode="numeric"
                isInvalid={!!form.formState.errors.daysBefore}
                {...form.register('daysBefore')}
              />
              {form.formState.errors.daysBefore && (
                <div className="text-danger fs-xs mt-1">{t(form.formState.errors.daysBefore.message ?? '')}</div>
              )}
            </div>
          )}

          {needsCategory(type) && (
            <div className="mb-3">
              <FormLabel>{t('alerts:form.categoryLabel')}</FormLabel>
              <Controller
                control={form.control}
                name="categoryId"
                render={({ field }) => (
                  <Select<SelectOption>
                    className="react-select"
                    classNamePrefix="react-select"
                    placeholder={t('alerts:form.categoryPlaceholder')}
                    isClearable
                    options={categoryOptions}
                    value={categoryOptions.find((option) => option.value === field.value) ?? null}
                    onChange={(option) => field.onChange(option?.value ?? '')}
                  />
                )}
              />
            </div>
          )}

          {needsAccount(type) && (
            <div className="mb-3">
              <FormLabel>{t('alerts:form.accountLabel')}</FormLabel>
              <Controller
                control={form.control}
                name="accountId"
                render={({ field }) => (
                  <Select<SelectOption>
                    className="react-select"
                    classNamePrefix="react-select"
                    placeholder={t('alerts:form.accountPlaceholder')}
                    isClearable
                    options={accountOptions}
                    value={accountOptions.find((option) => option.value === field.value) ?? null}
                    onChange={(option) => field.onChange(option?.value ?? '')}
                  />
                )}
              />
            </div>
          )}

          <div className="mb-3">
            <FormLabel>{t('alerts:form.channelLabel')}</FormLabel>
            <Form.Select {...form.register('channel')}>
              {CHANNEL_OPTIONS.map((option) => (
                <option key={option} value={option}>
                  {t(`alerts:channels.${option}`)}
                </option>
              ))}
            </Form.Select>
          </div>
        </Modal.Body>

        <Modal.Footer>
          <Button variant="light" type="button" onClick={onHide}>
            {t('common:actions.cancel')}
          </Button>
          <Button variant="primary" type="submit" disabled={isPending}>
            {editTarget ? t('alerts:form.submitEdit') : t('alerts:form.submitNew')}
          </Button>
        </Modal.Footer>
      </Form>
    </Modal>
  )
}
