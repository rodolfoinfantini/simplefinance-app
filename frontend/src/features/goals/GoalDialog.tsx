import { useEffect, useState } from 'react'
import { Icon as IconifyIcon } from '@/components/Icon'
import { zodResolver } from '@hookform/resolvers/zod'
import { useForm } from 'react-hook-form'
import { Button, Form, FormControl, FormLabel, Modal } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'
import { amountValueAs } from '@/lib/amount'
import { z } from 'zod'

import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { formatAccountOptionLabel, type AccountResponse } from '@/features/accounts/api'
import { useAuth } from '@/features/auth/useAuth'
import { CATEGORY_ICON_NAMES, resolveCategoryIcon } from '@/features/categories/icons'
import type { InvestmentPositionItem } from '@/features/investments/api'

import { useCreateGoalMutation, useUpdateGoalMutation, type Goal } from './api'

const NONE_SOURCE_VALUE = 'none'
const ACCOUNT_PREFIX = 'account:'
const INVESTMENT_PREFIX = 'investment:'

const DEFAULT_ICON = 'PiggyBank'

/**
 * Fixed 10-swatch preset set, same convention as CategoryDialog.tsx's own
 * COLOR_PRESETS -- declared locally here since that array isn't exported
 * from CategoryDialog.tsx (13-03-PLAN.md's <interfaces> block).
 */
const COLOR_PRESETS = [
  '#F97316',
  '#EAB308',
  '#84CC16',
  '#14B8A6',
  '#06B6D4',
  '#8B5CF6',
  '#D946EF',
  '#EC4899',
  '#78716C',
  '#0EA5E9',
]

/**
 * Client-side mirror of 07-04's server-side rules (TargetAmount > 0) kept
 * in sync deliberately -- the server validator remains the authoritative
 * gate, this schema exists purely for immediate UX feedback (T-13-05).
 */
const goalFormSchema = z.object({
  name: z.string().trim().min(1, 'common:validation.nameRequired').max(128, 'common:validation.nameTooLong'),
  targetAmount: z
    .number({ message: 'common:validation.amountPositive' })
    .positive('common:validation.amountPositive'),
  targetDate: z.string().nullable(),
  linkedSource: z.string(),
  icon: z.string().min(1, 'common:validation.iconRequired'),
  color: z.string().min(1, 'common:validation.colorRequired'),
})

type GoalFormInput = z.input<typeof goalFormSchema>
type GoalFormValues = z.output<typeof goalFormSchema>

function linkedSourceFromGoal(goal: Goal): string {
  if (goal.linkedAccountId) return `${ACCOUNT_PREFIX}${goal.linkedAccountId}`
  if (goal.linkedInvestmentId) return `${INVESTMENT_PREFIX}${goal.linkedInvestmentId}`
  return NONE_SOURCE_VALUE
}

export interface GoalDialogProps {
  show: boolean
  onHide: () => void
  editTarget: Goal | null
  accounts: AccountResponse[]
  investments: InvestmentPositionItem[]
}

/**
 * Create/edit form (FE-GOAL-01), mirroring CategoryDialog.tsx's Modal +
 * react-hook-form + zod structure exactly: name, target amount, optional
 * target date, a single linked-source Select resolved back into mutually
 * exclusive linkedAccountId/linkedInvestmentId on submit, and icon/color
 * pickers reusing CategoryDialog's own searchable-grid/preset-swatch
 * patterns. Submits via useCreateGoalMutation (create) or
 * useUpdateGoalMutation (edit, pre-filled from editTarget).
 */
export function GoalDialog({ show, onHide, editTarget, accounts, investments }: GoalDialogProps) {
  const { t } = useTranslation('goals')
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  const { showNotification } = useNotificationContext()
  const [iconFilter, setIconFilter] = useState('')

  const createMutation = useCreateGoalMutation()
  const updateMutation = useUpdateGoalMutation()
  const isPending = createMutation.isPending || updateMutation.isPending

  const form = useForm<GoalFormInput, unknown, GoalFormValues>({
    resolver: zodResolver(goalFormSchema),
    defaultValues: {
      name: '',
      targetAmount: 0,
      targetDate: null,
      linkedSource: NONE_SOURCE_VALUE,
      icon: DEFAULT_ICON,
      color: COLOR_PRESETS[0],
    },
  })

  useEffect(() => {
    if (!show) return

    setIconFilter('')

    if (editTarget) {
      form.reset({
        name: editTarget.name,
        targetAmount: editTarget.targetAmount,
        targetDate: editTarget.targetDate,
        linkedSource: linkedSourceFromGoal(editTarget),
        icon: editTarget.icon ?? DEFAULT_ICON,
        color: editTarget.color ?? COLOR_PRESETS[0],
      })
    } else {
      form.reset({
        name: '',
        targetAmount: 0,
        targetDate: null,
        linkedSource: NONE_SOURCE_VALUE,
        icon: DEFAULT_ICON,
        color: COLOR_PRESETS[0],
      })
    }
  }, [show, editTarget, form])

  const filteredIconNames = CATEGORY_ICON_NAMES.filter((name) =>
    name.toLowerCase().includes(iconFilter.trim().toLowerCase())
  )

  const linkedSourceOptions = [
    ...accounts.map((account) => ({
      value: `${ACCOUNT_PREFIX}${account.id}`,
      label: t('dialog.accountOption', { name: formatAccountOptionLabel(account) }),
    })),
    ...investments.map((investment) => ({
      value: `${INVESTMENT_PREFIX}${investment.id}`,
      label: t('dialog.investmentOption', { name: investment.name }),
    })),
  ]

  async function onSubmit(values: GoalFormValues) {
    const linkedAccountId = values.linkedSource.startsWith(ACCOUNT_PREFIX)
      ? values.linkedSource.slice(ACCOUNT_PREFIX.length)
      : null
    const linkedInvestmentId = values.linkedSource.startsWith(INVESTMENT_PREFIX)
      ? values.linkedSource.slice(INVESTMENT_PREFIX.length)
      : null

    const fields = {
      name: values.name,
      targetAmount: values.targetAmount,
      targetDate: values.targetDate === '' ? null : values.targetDate,
      linkedAccountId,
      linkedInvestmentId,
      icon: values.icon,
      color: values.color,
    }

    try {
      if (editTarget) {
        await updateMutation.mutateAsync({ accessToken: token, id: editTarget.id, ...fields })
        showNotification({ message: t('toasts.goalUpdated'), variant: 'success' })
      } else {
        await createMutation.mutateAsync({ accessToken: token, ...fields })
        showNotification({ message: t('toasts.goalCreated'), variant: 'success' })
      }
      onHide()
    } catch {
      showNotification({ message: t('saveError'), variant: 'danger' })
    }
  }

  const iconValue = form.watch('icon')
  const colorValue = form.watch('color')

  return (
    <Modal show={show} onHide={onHide} centered>
      <Modal.Header closeButton>
        <Modal.Title>{editTarget ? t('dialog.editTitle') : t('dialog.newTitle')}</Modal.Title>
      </Modal.Header>

      <Form onSubmit={form.handleSubmit(onSubmit)} noValidate>
        <Modal.Body>
          <p className="text-muted fs-sm mb-3">
            {editTarget ? t('dialog.editDescription') : t('dialog.createDescription')}
          </p>

          <div className="mb-3">
            <FormLabel>{t('dialog.nameLabel')}</FormLabel>
            <FormControl
              autoFocus
              isInvalid={!!form.formState.errors.name}
              {...form.register('name')}
            />
            {form.formState.errors.name && (
              <div className="text-danger fs-xs mt-1">{t(form.formState.errors.name.message ?? '')}</div>
            )}
          </div>

          <div className="mb-3">
            <FormLabel>{t('dialog.targetAmountLabel')}</FormLabel>
            <FormControl
              type="text"
              inputMode="decimal"
              autoComplete="off"
              isInvalid={!!form.formState.errors.targetAmount}
              {...form.register('targetAmount', { setValueAs: amountValueAs })}
            />
            {form.formState.errors.targetAmount && (
              <div className="text-danger fs-xs mt-1">{t(form.formState.errors.targetAmount.message ?? '')}</div>
            )}
          </div>

          <div className="mb-3">
            <FormLabel>{t('dialog.targetDateLabel')}</FormLabel>
            <FormControl
              type="date"
              {...form.register('targetDate')}
              value={form.watch('targetDate') ?? ''}
              onChange={(event) => form.setValue('targetDate', event.target.value || null)}
            />
          </div>

          <div className="mb-3">
            <FormLabel>{t('dialog.linkedSourceLabel')}</FormLabel>
            <Form.Select {...form.register('linkedSource')}>
              <option value={NONE_SOURCE_VALUE}>{t('dialog.none')}</option>
              {linkedSourceOptions.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </Form.Select>
          </div>

          <div className="mb-3">
            <FormLabel>{t('dialog.iconLabel')}</FormLabel>
            <FormControl
              placeholder={t('dialog.iconSearchPlaceholder')}
              value={iconFilter}
              onChange={(event) => setIconFilter(event.target.value)}
              className="mb-2"
            />
            <div className="d-flex flex-wrap g-2" style={{ gap: '0.5rem', maxHeight: 180, overflowY: 'auto' }}>
              {filteredIconNames.length === 0 && <span className="text-muted fs-sm">{t('dialog.noIconFound')}</span>}
              {filteredIconNames.map((name) => (
                <button
                  key={name}
                  type="button"
                  className={`btn btn-sm d-flex align-items-center gap-1 ${
                    iconValue === name ? 'btn-primary' : 'btn-outline-secondary'
                  }`}
                  onClick={() => form.setValue('icon', name, { shouldValidate: true })}
                >
                  <IconifyIcon icon={resolveCategoryIcon(name)} />
                  {name}
                </button>
              ))}
            </div>
            {form.formState.errors.icon && (
              <div className="text-danger fs-xs mt-1">{t(form.formState.errors.icon.message ?? '')}</div>
            )}
          </div>

          <div className="mb-2">
            <FormLabel>{t('dialog.colorLabel')}</FormLabel>
            <div className="d-flex flex-wrap" style={{ gap: '0.5rem' }}>
              {COLOR_PRESETS.map((color) => (
                <button
                  key={color}
                  type="button"
                  className="d-flex align-items-center justify-content-center rounded-circle border-0"
                  style={{
                    backgroundColor: color,
                    width: 32,
                    height: 32,
                    outline: colorValue === color ? '2px solid var(--bs-body-color)' : 'none',
                    outlineOffset: 2,
                  }}
                  onClick={() => form.setValue('color', color, { shouldValidate: true })}
                  aria-label={t('dialog.selectColorAria', { color })}
                >
                  {colorValue === color && <IconifyIcon icon="lucide:check" className="text-white" />}
                </button>
              ))}
            </div>
            {form.formState.errors.color && (
              <div className="text-danger fs-xs mt-1">{t(form.formState.errors.color.message ?? '')}</div>
            )}
          </div>
        </Modal.Body>

        <Modal.Footer>
          <Button variant="light" type="button" onClick={onHide}>
            {t('dialog.cancel')}
          </Button>
          <Button variant="primary" type="submit" disabled={isPending}>
            {editTarget ? t('dialog.saveChanges') : t('dialog.createGoal')}
          </Button>
        </Modal.Footer>
      </Form>
    </Modal>
  )
}
