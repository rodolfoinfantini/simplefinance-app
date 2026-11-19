import { useEffect, useMemo, useState } from 'react'
import { zodResolver } from '@hookform/resolvers/zod'
import { Controller, useForm } from 'react-hook-form'
import { useTranslation } from 'react-i18next'
import { parseAmount } from '@/lib/amount'
import type { TFunction } from 'i18next'
import { Button, Form, FormControl, FormLabel, Modal } from 'react-bootstrap'
import Select from 'react-select'
import { z } from 'zod'

import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import type { CategoryResponse } from '@/features/categories/api'

import {
  useCreateCategoryRuleMutation,
  useTestCategoryRuleMutation,
  useUpdateCategoryRuleMutation,
  type CategoryRuleResponse,
  type RuleMatchField,
  type RuleMatchOperator,
} from './api'

const FIELD_VALUES: RuleMatchField[] = ['DESCRIPTION', 'MERCHANT', 'PLUGGY_CATEGORY', 'AMOUNT']

interface CategoryOption {
  value: string
  label: string
}

/** Amount only allows Equals/Range; every other field only allows Contains/Equals/Regex -- mirrors 04-05's server-side CategoryRuleValidationRules combination rules (cosmetic UX guard only, per T-04-17). */
function operatorsForField(field: RuleMatchField): RuleMatchOperator[] {
  return field === 'AMOUNT' ? ['EQUALS', 'RANGE'] : ['CONTAINS', 'EQUALS', 'REGEX']
}

function makeRuleFormSchema(t: TFunction) {
  return z
    .object({
      matchField: z.enum(['DESCRIPTION', 'MERCHANT', 'PLUGGY_CATEGORY', 'AMOUNT']),
      matchOperator: z.enum(['CONTAINS', 'EQUALS', 'REGEX', 'RANGE']),
      matchValue: z.string().max(200, t('dialog.errors.matchValueTooLong')).optional(),
      amountMin: z.string().optional(),
      amountMax: z.string().optional(),
      categoryId: z.string().min(1, t('dialog.errors.categoryRequired')),
      markAsTransfer: z.boolean(),
    })
    .superRefine((values, ctx) => {
      if (values.matchOperator === 'RANGE') {
        const min = parseAmount(values.amountMin) ?? Number.NaN
        const max = parseAmount(values.amountMax) ?? Number.NaN
        if (!values.amountMin || Number.isNaN(min)) {
          ctx.addIssue({ code: 'custom', path: ['amountMin'], message: t('dialog.errors.amountMinRequired') })
        }
        if (!values.amountMax || Number.isNaN(max)) {
          ctx.addIssue({ code: 'custom', path: ['amountMax'], message: t('dialog.errors.amountMaxRequired') })
        }
        if (values.amountMin && values.amountMax && !Number.isNaN(min) && !Number.isNaN(max) && min > max) {
          ctx.addIssue({ code: 'custom', path: ['amountMax'], message: t('dialog.errors.amountMaxGteMin') })
        }
        return
      }

      if (!values.matchValue || values.matchValue.trim().length === 0) {
        ctx.addIssue({ code: 'custom', path: ['matchValue'], message: t('dialog.errors.matchValueRequired') })
        return
      }

      if (values.matchField === 'AMOUNT' && values.matchOperator === 'EQUALS' && parseAmount(values.matchValue) === null) {
        ctx.addIssue({ code: 'custom', path: ['matchValue'], message: t('dialog.errors.invalidNumber') })
      }

      if (values.matchOperator === 'REGEX') {
        try {
          new RegExp(values.matchValue)
        } catch {
          ctx.addIssue({ code: 'custom', path: ['matchValue'], message: t('dialog.errors.invalidRegex') })
        }
      }
    })
}

type RuleFormValues = z.infer<ReturnType<typeof makeRuleFormSchema>>

interface TestDraft {
  matchField: RuleMatchField
  matchOperator: RuleMatchOperator
  matchValue?: string | null
  amountMin?: number | null
  amountMax?: number | null
}

/** Mirrors the zod schema's own combination rules -- returns null while the draft is syntactically incomplete/invalid, so the live preview never fires a doomed /test call. */
/** Trimmed match value; an AMOUNT/EQUALS value is sent as a plain decimal ("1234.56"). */
function normalizeMatchValue(values: RuleFormValues): string | null {
  const value = values.matchValue?.trim()
  if (!value) return null
  if (values.matchField === 'AMOUNT' && values.matchOperator === 'EQUALS') {
    const amount = parseAmount(value)
    return amount === null ? value : String(amount)
  }
  return value
}

function buildTestDraft(values: RuleFormValues): TestDraft | null {
  if (values.matchOperator === 'RANGE') {
    const min = parseAmount(values.amountMin) ?? Number.NaN
    const max = parseAmount(values.amountMax) ?? Number.NaN
    if (!values.amountMin || !values.amountMax || Number.isNaN(min) || Number.isNaN(max) || min > max) return null
    return { matchField: values.matchField, matchOperator: values.matchOperator, amountMin: min, amountMax: max }
  }

  const value = values.matchValue?.trim()
  if (!value) return null

  if (values.matchField === 'AMOUNT' && values.matchOperator === 'EQUALS') {
    const amount = parseAmount(value)
    if (amount === null) return null
    return { matchField: values.matchField, matchOperator: values.matchOperator, matchValue: String(amount) }
  }

  if (values.matchOperator === 'REGEX') {
    try {
      new RegExp(value)
    } catch {
      return null
    }
  }

  return { matchField: values.matchField, matchOperator: values.matchOperator, matchValue: value }
}

function formatSampleDate(dateString: string): string {
  return new Intl.DateTimeFormat('pt-BR', { day: '2-digit', month: '2-digit' }).format(new Date(dateString))
}

function formatSampleAmount(amount: number): string {
  return new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' }).format(amount)
}

export interface RuleFormDialogProps {
  show: boolean
  onHide: () => void
  categories: CategoryResponse[]
  editTarget: CategoryRuleResponse | null
  accessToken: string
}

/**
 * Create/edit rule dialog (CAT-03): Field/Operator/Value-or-Min-Max/Category
 * form plus a live, debounced "Test this rule" preview that calls
 * POST /api/category-rules/test against the in-progress draft -- works
 * before the rule is ever saved, never persists. New rules append to the
 * end of priority order (no priority input here; reordering is a dedicated
 * list action in RulesPage.tsx). Editing preserves the rule's existing
 * priority and isActive state untouched. Category picker sources `categories`
 * from RulesPage's own useCategoriesQuery (features/categories/api.ts,
 * Plan 11-03's hook) -- a deliberate discretion choice over v1's
 * @/features/transactions/api re-export, since this screen never reads
 * `isSystem` and avoiding the cross-feature import keeps this plan's
 * dependency graph limited to 11-01+11-03.
 */
export function RuleFormDialog({ show, onHide, categories, editTarget, accessToken }: RuleFormDialogProps) {
  const { t } = useTranslation(['rules', 'common'])
  const { showNotification } = useNotificationContext()
  const [testResult, setTestResult] = useState<{ count: number; sample: { id: string; date: string; description: string; amount: number }[] } | null>(null)

  const createMutation = useCreateCategoryRuleMutation()
  const updateMutation = useUpdateCategoryRuleMutation()
  const testMutation = useTestCategoryRuleMutation()
  const isPending = createMutation.isPending || updateMutation.isPending

  const ruleFormSchema = useMemo(() => makeRuleFormSchema(t), [t])

  const form = useForm<RuleFormValues>({
    resolver: zodResolver(ruleFormSchema),
    defaultValues: {
      matchField: 'DESCRIPTION',
      matchOperator: 'CONTAINS',
      matchValue: '',
      amountMin: '',
      amountMax: '',
      categoryId: '',
      markAsTransfer: false,
    },
  })

  useEffect(() => {
    if (!show) return

    if (editTarget) {
      form.reset({
        matchField: editTarget.matchField,
        matchOperator: editTarget.matchOperator,
        matchValue: editTarget.matchValue ?? '',
        amountMin: editTarget.amountMin != null ? String(editTarget.amountMin) : '',
        amountMax: editTarget.amountMax != null ? String(editTarget.amountMax) : '',
        categoryId: editTarget.categoryId,
        markAsTransfer: editTarget.markAsTransfer ?? false,
      })
    } else {
      form.reset({
        matchField: 'DESCRIPTION',
        matchOperator: 'CONTAINS',
        matchValue: '',
        amountMin: '',
        amountMax: '',
        categoryId: '',
        markAsTransfer: false,
      })
    }
    setTestResult(null)
  }, [show, editTarget, form])

  const matchField = form.watch('matchField')
  const matchOperator = form.watch('matchOperator')
  const matchValue = form.watch('matchValue')
  const amountMin = form.watch('amountMin')
  const amountMax = form.watch('amountMax')
  const categoryId = form.watch('categoryId')

  // Amount only allows Equals/Range -- auto-switch away from an operator the
  // newly-selected field doesn't support.
  useEffect(() => {
    const allowed = operatorsForField(matchField)
    if (!allowed.includes(matchOperator)) {
      form.setValue('matchOperator', allowed[0])
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [matchField])

  // Live, debounced "Test this rule" preview against the draft -- fires
  // before the rule is saved, never persists (POST /test is a zero-write
  // endpoint per 04-05-SUMMARY.md).
  useEffect(() => {
    if (!show) return

    const draft = buildTestDraft({ matchField, matchOperator, matchValue, amountMin, amountMax, categoryId, markAsTransfer: false })
    if (!draft) {
      setTestResult(null)
      return
    }

    const timeoutId = setTimeout(() => {
      testMutation.mutate(
        { accessToken, ...draft },
        {
          onSuccess: (data) => setTestResult(data),
          onError: () => setTestResult(null),
        }
      )
    }, 400)

    return () => clearTimeout(timeoutId)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [show, accessToken, matchField, matchOperator, matchValue, amountMin, amountMax])

  const isRange = matchOperator === 'RANGE'

  const categoryOptions: CategoryOption[] = categories.map((category) => ({ value: category.id, label: category.name }))

  async function onSubmit(values: RuleFormValues) {
    const range = values.matchOperator === 'RANGE'
    const fields = {
      matchField: values.matchField,
      matchOperator: values.matchOperator,
      matchValue: range ? null : normalizeMatchValue(values),
      amountMin: range ? parseAmount(values.amountMin) : null,
      amountMax: range ? parseAmount(values.amountMax) : null,
      categoryId: values.categoryId,
      isActive: editTarget?.isActive ?? true,
      markAsTransfer: values.markAsTransfer,
    }

    try {
      if (editTarget) {
        await updateMutation.mutateAsync({ accessToken, id: editTarget.id, ...fields })
        showNotification({ message: t('toasts.updated'), variant: 'success' })
      } else {
        await createMutation.mutateAsync({ accessToken, ...fields })
        showNotification({ message: t('toasts.created'), variant: 'success' })
      }
      onHide()
    } catch {
      showNotification({ message: t('dialog.saveError'), variant: 'danger' })
    }
  }

  return (
    <Modal show={show} onHide={onHide} centered>
      <Modal.Header closeButton>
        <Modal.Title>{editTarget ? t('dialog.titleEdit') : t('dialog.titleAdd')}</Modal.Title>
      </Modal.Header>

      <Form onSubmit={form.handleSubmit(onSubmit)} noValidate>
        <Modal.Body>
          <p className="text-muted fs-sm mb-3">
            {editTarget ? t('dialog.descriptionEdit') : t('dialog.descriptionAdd')}
          </p>

          <div className="mb-3">
            <FormLabel>{t('dialog.fields.field')}</FormLabel>
            <Form.Select {...form.register('matchField')}>
              {FIELD_VALUES.map((value) => (
                <option key={value} value={value}>
                  {t(`fields.${value}`)}
                </option>
              ))}
            </Form.Select>
          </div>

          <div className="mb-3">
            <FormLabel>{t('dialog.fields.operator')}</FormLabel>
            <Form.Select {...form.register('matchOperator')}>
              {operatorsForField(matchField).map((operator) => (
                <option key={operator} value={operator}>
                  {t(`operators.labels.${operator}`)}
                </option>
              ))}
            </Form.Select>
            {matchOperator === 'REGEX' && <p className="text-muted fs-xs mt-1 mb-0">{t('dialog.regexHint')}</p>}
          </div>

          {isRange ? (
            <div className="row mb-3">
              <div className="col-6">
                <FormLabel>{t('dialog.fields.min')}</FormLabel>
                <FormControl
                  type="text"
                  inputMode="decimal"
                  autoComplete="off"
                  isInvalid={!!form.formState.errors.amountMin}
                  {...form.register('amountMin')}
                />
                {form.formState.errors.amountMin && (
                  <div className="text-danger fs-xs mt-1">{form.formState.errors.amountMin.message}</div>
                )}
              </div>
              <div className="col-6">
                <FormLabel>{t('dialog.fields.max')}</FormLabel>
                <FormControl
                  type="text"
                  inputMode="decimal"
                  autoComplete="off"
                  isInvalid={!!form.formState.errors.amountMax}
                  {...form.register('amountMax')}
                />
                {form.formState.errors.amountMax && (
                  <div className="text-danger fs-xs mt-1">{form.formState.errors.amountMax.message}</div>
                )}
              </div>
            </div>
          ) : (
            <div className="mb-3">
              <FormLabel>{t('dialog.fields.value')}</FormLabel>
              <FormControl isInvalid={!!form.formState.errors.matchValue} {...form.register('matchValue')} />
              {form.formState.errors.matchValue && (
                <div className="text-danger fs-xs mt-1">{form.formState.errors.matchValue.message}</div>
              )}
            </div>
          )}

          <div className="mb-3">
            <FormLabel>{t('dialog.fields.category')}</FormLabel>
            <Controller
              control={form.control}
              name="categoryId"
              render={({ field }) => (
                <Select<CategoryOption>
                  className="react-select"
                  classNamePrefix="react-select"
                  placeholder={t('dialog.categoryPlaceholder')}
                  options={categoryOptions}
                  value={categoryOptions.find((option) => option.value === field.value) ?? null}
                  onChange={(option) => field.onChange(option?.value ?? '')}
                />
              )}
            />
            {form.formState.errors.categoryId && (
              <div className="text-danger fs-xs mt-1">{form.formState.errors.categoryId.message}</div>
            )}
          </div>

          <div className="mb-3">
            <Controller
              control={form.control}
              name="markAsTransfer"
              render={({ field }) => (
                <Form.Check
                  type="switch"
                  id="rule-mark-as-transfer"
                  label={t('dialog.fields.markAsTransfer')}
                  checked={field.value}
                  onChange={(event) => field.onChange(event.target.checked)}
                  onBlur={field.onBlur}
                />
              )}
            />
            <p className="text-muted fs-xs mt-1 mb-0">{t('dialog.markAsTransferHint')}</p>
          </div>

          <div className="rounded border p-3">
            <p className="fw-medium mb-0">{t('dialog.test.title')}</p>
            {testMutation.isPending && <p className="text-muted fs-xs mt-1 mb-0">{t('dialog.test.checking')}</p>}
            {!testMutation.isPending && testResult && (
              <>
                <p className="text-muted fs-xs mt-1 mb-0">
                  {t('dialog.test.matches', { count: testResult.count })}
                </p>
                {testResult.sample.length > 0 && (
                  <ul className="list-unstyled mt-2 mb-0 d-flex flex-column gap-1">
                    {testResult.sample.slice(0, 5).map((row) => (
                      <li key={row.id} className="d-flex align-items-center justify-content-between gap-2 fs-xs">
                        <span className="text-muted">{formatSampleDate(row.date)}</span>
                        <span className="text-truncate flex-grow-1 mx-2">{row.description}</span>
                        <span>{formatSampleAmount(row.amount)}</span>
                      </li>
                    ))}
                  </ul>
                )}
              </>
            )}
            {!testMutation.isPending && !testResult && (
              <p className="text-muted fs-xs mt-1 mb-0">{t('dialog.test.hint')}</p>
            )}
          </div>
        </Modal.Body>

        <Modal.Footer>
          <Button variant="light" type="button" onClick={onHide}>
            {t('common:actions.cancel')}
          </Button>
          <Button variant="primary" type="submit" disabled={isPending}>
            {editTarget ? t('dialog.saveChanges') : t('dialog.createRule')}
          </Button>
        </Modal.Footer>
      </Form>
    </Modal>
  )
}
