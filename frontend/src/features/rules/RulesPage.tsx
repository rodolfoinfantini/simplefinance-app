import { useState } from 'react'
import { Icon as IconifyIcon } from '@/components/Icon'
import { useTranslation } from 'react-i18next'
import type { TFunction } from 'i18next'
import {
  Alert,
  Badge,
  Button,
  Card,
  CardBody,
  Dropdown,
  DropdownItem,
  DropdownMenu,
  DropdownToggle,
  Form,
  Modal,
  Placeholder,
  Table,
} from 'react-bootstrap'

import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { useAuth } from '@/features/auth/useAuth'
import { useCategoriesQuery } from '@/features/categories/api'

import {
  useApplyCategoryRuleNowMutation,
  useCategoryRulesQuery,
  useDeleteCategoryRuleMutation,
  useReorderCategoryRulesMutation,
  useToggleCategoryRuleActiveMutation,
  type CategoryRuleResponse,
} from './api'
import { RuleFormDialog } from './RuleFormDialog'

function formatCurrency(value: number): string {
  return new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' }).format(value)
}

/**
 * Plain-English match summary built client-side, e.g. `Description contains
 * "IFOOD"` or `Amount between R$ 50,00 and R$ 200,00` -- formatted via the
 * same Intl.NumberFormat('pt-BR', { style: 'currency' }) convention used
 * elsewhere. Ported unchanged from v1's RulesPage.tsx.
 */
export function buildMatchSummary(
  rule: Pick<CategoryRuleResponse, 'matchField' | 'matchOperator' | 'matchValue' | 'amountMin' | 'amountMax'>,
  t: TFunction
): string {
  const fieldLabel = t(`fields.${rule.matchField}`)

  if (rule.matchOperator === 'RANGE') {
    return t('matchSummary.range', {
      field: fieldLabel,
      min: formatCurrency(rule.amountMin ?? 0),
      max: formatCurrency(rule.amountMax ?? 0),
    })
  }

  if (rule.matchField === 'AMOUNT' && rule.matchOperator === 'EQUALS') {
    return t('matchSummary.amountEquals', { field: fieldLabel, value: formatCurrency(Number(rule.matchValue ?? 0)) })
  }

  if (rule.matchOperator === 'REGEX') {
    return t('matchSummary.regex', { field: fieldLabel, value: rule.matchValue ?? '' })
  }

  return t('matchSummary.default', {
    field: fieldLabel,
    operator: t(`operators.summary.${rule.matchOperator}`),
    value: rule.matchValue ?? '',
  })
}

function RowSkeleton() {
  return (
    <tr>
      <td>
        <Placeholder as="span" animation="glow">
          <Placeholder xs={12} style={{ width: 20 }} />
        </Placeholder>
      </td>
      <td>
        <Placeholder as="span" animation="glow">
          <Placeholder xs={12} style={{ width: 220 }} />
        </Placeholder>
      </td>
      <td>
        <Placeholder as="span" animation="glow">
          <Placeholder xs={12} style={{ width: 110 }} />
        </Placeholder>
      </td>
      <td>
        <Placeholder as="span" animation="glow">
          <Placeholder xs={12} style={{ width: 36, height: 20 }} />
        </Placeholder>
      </td>
      <td />
    </tr>
  )
}

function EmptyState({ onCreate }: { onCreate: () => void }) {
  const { t } = useTranslation('rules')
  return (
    <Card>
      <CardBody className="text-center py-5">
        <IconifyIcon icon="lucide:shuffle" className="fs-1 text-muted mb-3" />
        <h5>{t('empty.title')}</h5>
        <p className="text-muted mb-3">{t('empty.description')}</p>
        <Button variant="primary" onClick={onCreate}>
          {t('page.newRule')}
        </Button>
      </CardBody>
    </Card>
  )
}

/**
 * Settings > Rules screen (CAT-03/CAT-04): priority-ordered rule list with an
 * active toggle, plain-English match summary, up/down reorder, and a kebab
 * menu (Edit / Apply now / Delete). Routed at /settings/rules, reachable
 * from the Sidebar's "Rules" nav item.
 */
export function RulesPage() {
  const { t } = useTranslation(['rules', 'common'])
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  const { showNotification } = useNotificationContext()

  const rulesQuery = useCategoryRulesQuery(token)
  const categoriesQuery = useCategoriesQuery(token)
  const toggleActiveMutation = useToggleCategoryRuleActiveMutation()
  const reorderMutation = useReorderCategoryRulesMutation()
  const applyNowMutation = useApplyCategoryRuleNowMutation()
  const deleteMutation = useDeleteCategoryRuleMutation()

  const [applyTarget, setApplyTarget] = useState<CategoryRuleResponse | null>(null)
  const [deleteTarget, setDeleteTarget] = useState<CategoryRuleResponse | null>(null)
  const [dialogOpen, setDialogOpen] = useState(false)
  const [editTarget, setEditTarget] = useState<CategoryRuleResponse | null>(null)

  const rules = rulesQuery.data ?? []
  const categories = categoriesQuery.data ?? []

  function handleAddRule() {
    setEditTarget(null)
    setDialogOpen(true)
  }

  function handleEditRule(rule: CategoryRuleResponse) {
    setEditTarget(rule)
    setDialogOpen(true)
  }

  async function handleToggleActive(rule: CategoryRuleResponse, checked: boolean) {
    try {
      await toggleActiveMutation.mutateAsync({ accessToken: token, id: rule.id, isActive: checked })
    } catch {
      showNotification({ message: t('errors.toggle'), variant: 'danger' })
    }
  }

  async function handleMove(index: number, direction: -1 | 1) {
    const targetIndex = index + direction
    if (targetIndex < 0 || targetIndex >= rules.length) return

    const orderedIds = rules.map((rule) => rule.id)
    ;[orderedIds[index], orderedIds[targetIndex]] = [orderedIds[targetIndex], orderedIds[index]]

    try {
      await reorderMutation.mutateAsync({ accessToken: token, orderedIds })
    } catch {
      showNotification({ message: t('errors.reorder'), variant: 'danger' })
    }
  }

  async function handleApplyNow() {
    if (!applyTarget) return
    try {
      const result = await applyNowMutation.mutateAsync({ accessToken: token, id: applyTarget.id })
      showNotification({ message: t('toasts.applied', { count: result.updatedCount }), variant: 'success' })
    } catch {
      showNotification({ message: t('errors.apply'), variant: 'danger' })
    } finally {
      setApplyTarget(null)
    }
  }

  async function handleDelete() {
    if (!deleteTarget) return
    try {
      await deleteMutation.mutateAsync({ accessToken: token, id: deleteTarget.id })
      showNotification({ message: t('toasts.deleted'), variant: 'success' })
    } catch {
      showNotification({ message: t('errors.delete'), variant: 'danger' })
    } finally {
      setDeleteTarget(null)
    }
  }

  return (
    <div>
      <div className="d-flex flex-wrap align-items-center justify-content-between gap-3 mb-4">
        <h4 className="mb-0">{t('page.title')}</h4>
        <Button variant="primary" onClick={handleAddRule}>
          <IconifyIcon icon="lucide:plus" className="me-1 align-middle" />
          {t('page.newRule')}
        </Button>
      </div>

      {rulesQuery.isError && (
        <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3">
          <span>{t('errors.load')}</span>
          <Button variant="outline-danger" size="sm" onClick={() => void rulesQuery.refetch()}>
            {t('common:actions.retry')}
          </Button>
        </Alert>
      )}

      {!rulesQuery.isPending && !rulesQuery.isError && rules.length === 0 && <EmptyState onCreate={handleAddRule} />}

      {(rulesQuery.isPending || (!rulesQuery.isError && rules.length > 0)) && (
        <Card>
          <div className="table-responsive">
            <Table className="table-custom mb-0">
              <thead>
                <tr>
                  <th style={{ width: 64 }}>{t('table.priority')}</th>
                  <th>{t('table.matchSummary')}</th>
                  <th>{t('table.category')}</th>
                  <th style={{ width: 64 }}>{t('table.active')}</th>
                  <th style={{ width: 40 }} />
                </tr>
              </thead>
              <tbody>
                {rulesQuery.isPending && (
                  <>
                    <RowSkeleton />
                    <RowSkeleton />
                    <RowSkeleton />
                  </>
                )}
                {!rulesQuery.isPending &&
                  rules.map((rule, index) => (
                    <tr key={rule.id}>
                      <td>
                        <div className="d-flex align-items-center gap-1">
                          <span className="text-muted">{index + 1}</span>
                          <div className="d-flex flex-column">
                            <Button
                              variant="link"
                              className="p-0 text-muted lh-1"
                              disabled={index === 0 || reorderMutation.isPending}
                              onClick={() => void handleMove(index, -1)}
                              aria-label={t('table.moveUp')}
                            >
                              <IconifyIcon icon="lucide:chevron-up" />
                            </Button>
                            <Button
                              variant="link"
                              className="p-0 text-muted lh-1"
                              disabled={index === rules.length - 1 || reorderMutation.isPending}
                              onClick={() => void handleMove(index, 1)}
                              aria-label={t('table.moveDown')}
                            >
                              <IconifyIcon icon="lucide:chevron-down" />
                            </Button>
                          </div>
                        </div>
                      </td>
                      <td>{buildMatchSummary(rule, t)}</td>
                      <td>
                        <span>{rule.categoryName}</span>
                        {rule.markAsTransfer && (
                          <Badge bg="info" className="ms-2">
                            {t('table.transferBadge')}
                          </Badge>
                        )}
                      </td>
                      <td>
                        <Form.Check
                          type="switch"
                          checked={rule.isActive}
                          disabled={toggleActiveMutation.isPending}
                          onChange={(event) => void handleToggleActive(rule, event.target.checked)}
                          aria-label={rule.isActive ? t('table.deactivate') : t('table.activate')}
                        />
                      </td>
                      <td>
                        <Dropdown align="end">
                          <DropdownToggle
                            variant="link"
                            className="text-muted drop-arrow-none p-0"
                            aria-label={t('table.actionsLabel')}
                          >
                            <IconifyIcon icon="lucide:more-vertical" />
                          </DropdownToggle>
                          <DropdownMenu>
                            <DropdownItem onClick={() => handleEditRule(rule)}>{t('common:actions.edit')}</DropdownItem>
                            <DropdownItem onClick={() => setApplyTarget(rule)}>{t('table.applyNow')}</DropdownItem>
                            <DropdownItem className="text-danger" onClick={() => setDeleteTarget(rule)}>
                              {t('common:actions.delete')}
                            </DropdownItem>
                          </DropdownMenu>
                        </Dropdown>
                      </td>
                    </tr>
                  ))}
              </tbody>
            </Table>
          </div>
        </Card>
      )}

      <Modal show={!!applyTarget} onHide={() => setApplyTarget(null)} centered>
        <Modal.Header closeButton>
          <Modal.Title>{t('applyDialog.title')}</Modal.Title>
        </Modal.Header>
        <Modal.Body>{t('applyDialog.body')}</Modal.Body>
        <Modal.Footer>
          <Button variant="light" onClick={() => setApplyTarget(null)}>
            {t('common:actions.cancel')}
          </Button>
          <Button variant="primary" onClick={() => void handleApplyNow()} disabled={applyNowMutation.isPending}>
            {t('applyDialog.confirm')}
          </Button>
        </Modal.Footer>
      </Modal>

      <Modal show={!!deleteTarget} onHide={() => setDeleteTarget(null)} centered>
        <Modal.Header closeButton>
          <Modal.Title>{t('deleteDialog.title')}</Modal.Title>
        </Modal.Header>
        <Modal.Body>{t('deleteDialog.body')}</Modal.Body>
        <Modal.Footer>
          <Button variant="light" onClick={() => setDeleteTarget(null)}>
            {t('common:actions.cancel')}
          </Button>
          <Button variant="danger" onClick={() => void handleDelete()} disabled={deleteMutation.isPending}>
            {t('deleteDialog.confirm')}
          </Button>
        </Modal.Footer>
      </Modal>

      <RuleFormDialog
        show={dialogOpen}
        onHide={() => setDialogOpen(false)}
        categories={categories}
        editTarget={editTarget}
        accessToken={token}
      />
    </div>
  )
}
