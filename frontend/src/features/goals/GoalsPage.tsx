import { useState } from 'react'
import { Icon as IconifyIcon } from '@/components/Icon'
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
  Placeholder,
  ProgressBar,
} from 'react-bootstrap'
import { useTranslation } from 'react-i18next'
import { parseAmount } from '@/lib/amount'

import { MoneyText } from '@/components/MoneyText'
import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { formatAccountOptionLabel, useAccountsQuery } from '@/features/accounts/api'
import { useAuth } from '@/features/auth/useAuth'
import { resolveCategoryIcon } from '@/features/categories/icons'
import { useInvestmentPositionsQuery } from '@/features/investments/api'

import { GoalDialog } from './GoalDialog'
import {
  useArchiveGoalMutation,
  useGoalsQuery,
  useUpdateGoalProgressMutation,
  type Goal,
  type GoalStatus,
} from './api'

function formatDate(dateString: string): string {
  return new Intl.DateTimeFormat('pt-BR', { day: '2-digit', month: '2-digit', year: 'numeric' }).format(
    new Date(dateString)
  )
}

function useStatusBadges(): Record<GoalStatus, { label: string; bg: string; text: string } | null> {
  const { t } = useTranslation('goals')
  return {
    Active: null,
    Completed: { label: t('status.completed'), bg: 'success-subtle', text: 'success' },
    Archived: { label: t('status.archived'), bg: 'secondary-subtle', text: 'secondary' },
  }
}

function GoalCardSkeleton() {
  return (
    <Card>
      <CardBody className="d-flex flex-column gap-3">
        <div className="d-flex align-items-center gap-2">
          <Placeholder as="span" animation="glow">
            <Placeholder xs={12} style={{ width: 32, height: 32, borderRadius: '50%' }} />
          </Placeholder>
          <Placeholder as="p" animation="glow" className="mb-0">
            <Placeholder xs={8} />
          </Placeholder>
        </div>
        <Placeholder as="div" animation="glow">
          <Placeholder xs={12} style={{ height: 8 }} />
        </Placeholder>
        <Placeholder as="p" animation="glow" className="mb-0">
          <Placeholder xs={6} />
        </Placeholder>
      </CardBody>
    </Card>
  )
}

interface GoalCardProps {
  goal: Goal
  accessToken: string
  linkedSourceName: string | null
  onEdit: (goal: Goal) => void
}

/**
 * One Goal card: icon + color accent, progress bar/percent, remaining,
 * required monthly pace (when computable), a status Badge, and a kebab menu
 * (Edit / Update progress when unlinked / Archive). FE-GOAL-01.
 */
function GoalCard({ goal, accessToken, linkedSourceName, onEdit }: GoalCardProps) {
  const { t } = useTranslation('goals')
  const isUnlinked = !goal.linkedAccountId && !goal.linkedInvestmentId
  const isArchived = goal.status === 'Archived'
  const statusBadges = useStatusBadges()
  const statusBadge = statusBadges[goal.status]
  const { showNotification } = useNotificationContext()

  const [progressOpen, setProgressOpen] = useState(false)
  const [progressDraft, setProgressDraft] = useState('')

  const archiveMutation = useArchiveGoalMutation()
  const progressMutation = useUpdateGoalProgressMutation()

  async function handleArchive() {
    try {
      await archiveMutation.mutateAsync({ accessToken, id: goal.id })
    } catch {
      showNotification({ message: t('updateError'), variant: 'danger' })
    }
  }

  async function handleUpdateProgress() {
    const amount = parseAmount(progressDraft)
    if (amount === null || amount < 0) return

    try {
      await progressMutation.mutateAsync({ accessToken, id: goal.id, currentAmount: amount })
      setProgressOpen(false)
    } catch {
      showNotification({ message: t('updateError'), variant: 'danger' })
    }
  }

  return (
    <Card className={isArchived ? 'opacity-50' : undefined}>
      <CardBody className="d-flex flex-column gap-3">
        <div className="d-flex align-items-start justify-content-between gap-2">
          <div className="d-flex align-items-center gap-2 min-w-0">
            <span
              className="d-flex align-items-center justify-content-center rounded-circle flex-shrink-0"
              style={{ backgroundColor: `${goal.color ?? '#78716C'}22`, color: goal.color ?? undefined, width: 32, height: 32 }}
            >
              <IconifyIcon icon={resolveCategoryIcon(goal.icon)} />
            </span>
            <span className="fw-semibold text-truncate">{goal.name}</span>
          </div>

          <Dropdown align="end">
            <DropdownToggle variant="link" className="text-muted drop-arrow-none p-0" aria-label={t('card.actionsAriaLabel')}>
              <IconifyIcon icon="lucide:more-vertical" />
            </DropdownToggle>
            <DropdownMenu>
              <DropdownItem onClick={() => onEdit(goal)}>{t('card.edit')}</DropdownItem>
              {isUnlinked && (
                <DropdownItem
                  onClick={() => {
                    setProgressDraft(String(goal.currentAmount))
                    setProgressOpen(true)
                  }}
                >
                  {t('card.updateProgress')}
                </DropdownItem>
              )}
              {!isArchived && <DropdownItem onClick={() => void handleArchive()}>{t('card.archive')}</DropdownItem>}
            </DropdownMenu>
          </Dropdown>
        </div>

        {progressOpen && (
          <div className="d-flex align-items-center gap-2">
            <Form.Control
              type="text"
              inputMode="decimal"
              autoComplete="off"
              autoFocus
              value={progressDraft}
              onChange={(event) => setProgressDraft(event.target.value)}
            />
            <Button size="sm" disabled={progressMutation.isPending} onClick={() => void handleUpdateProgress()}>
              {t('card.save')}
            </Button>
            <Button size="sm" variant="light" onClick={() => setProgressOpen(false)}>
              {t('card.cancel')}
            </Button>
          </div>
        )}

        <div className="d-flex flex-column gap-1">
          <ProgressBar now={Math.min(goal.percentComplete, 100)} variant="success" style={{ height: 6 }} />
          <div className="d-flex align-items-center justify-content-between">
            <span className="fw-semibold tabular-nums">{Math.round(goal.percentComplete)}%</span>
            {statusBadge && (
              <Badge bg={statusBadge.bg} text={statusBadge.text}>
                {statusBadge.label}
              </Badge>
            )}
          </div>
        </div>

        <div className="d-flex flex-column gap-1 text-muted fs-sm">
          <span>
            <MoneyText amount={goal.remaining} variant="neutral" showSign={false} className="text-body" />
            {` ${t('card.remaining')}`}
          </span>
          {goal.requiredMonthlyPace !== null && (
            <span>
              <MoneyText amount={goal.requiredMonthlyPace} variant="neutral" showSign={false} className="text-body" />
              {t('card.perMonthNeeded')}
              {goal.targetDate && ` ${t('card.toHitDate', { date: formatDate(goal.targetDate) })}`}
            </span>
          )}
          {linkedSourceName && <span>{t('card.linkedTo', { name: linkedSourceName })}</span>}
        </div>
      </CardBody>
    </Card>
  )
}

/**
 * FE-GOAL-01: the Goals screen -- create/edit via GoalDialog, a responsive
 * card grid showing live progress/remaining/pace, and
 * Active/Completed/Archived status handling, wired to the unchanged
 * GET/POST/PATCH /api/goals surface (v1.0 Phase 7).
 */
export function GoalsPage() {
  const { t } = useTranslation('goals')
  const { accessToken } = useAuth()
  const token = accessToken ?? ''

  const goalsQuery = useGoalsQuery(token)
  const accountsQuery = useAccountsQuery(token)
  const investmentsQuery = useInvestmentPositionsQuery(token)

  const [dialogOpen, setDialogOpen] = useState(false)
  const [editTarget, setEditTarget] = useState<Goal | null>(null)

  const goals = goalsQuery.data ?? []
  const isInitialLoading = goalsQuery.isPending
  const isEmpty = !isInitialLoading && !goalsQuery.isError && goals.length === 0

  // Archived goals sort to the end of the grid; otherwise preserve API order.
  const sortedGoals = [...goals].sort((a, b) => {
    const aArchived = a.status === 'Archived' ? 1 : 0
    const bArchived = b.status === 'Archived' ? 1 : 0
    return aArchived - bArchived
  })

  const accountNameById = new Map(
    (accountsQuery.data ?? []).map((account) => [account.id, formatAccountOptionLabel(account)])
  )
  const investmentNameById = new Map(
    (investmentsQuery.data?.items ?? []).map((investment) => [investment.id, investment.name])
  )

  function linkedSourceNameFor(goal: Goal): string | null {
    if (goal.linkedAccountId) return accountNameById.get(goal.linkedAccountId) ?? null
    if (goal.linkedInvestmentId) return investmentNameById.get(goal.linkedInvestmentId) ?? null
    return null
  }

  function handleOpenCreate() {
    setEditTarget(null)
    setDialogOpen(true)
  }

  function handleOpenEdit(goal: Goal) {
    setEditTarget(goal)
    setDialogOpen(true)
  }

  return (
    <div>
      <div className="d-flex flex-wrap align-items-center justify-content-between gap-3 mb-4">
        <h4 className="mb-0">{t('pageTitle')}</h4>
        <Button variant="primary" onClick={handleOpenCreate}>
          <IconifyIcon icon="lucide:plus" className="me-1 align-middle" />
          {t('newGoal')}
        </Button>
      </div>

      {goalsQuery.isError && (
        <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3">
          <span>{t('loadError')}</span>
          <Button variant="outline-danger" size="sm" onClick={() => void goalsQuery.refetch()}>
            {t('retry')}
          </Button>
        </Alert>
      )}

      {!goalsQuery.isError && isInitialLoading && (
        <div className="row row-cols-1 row-cols-md-2 row-cols-lg-3 g-3">
          {Array.from({ length: 6 }).map((_, index) => (
            <div className="col" key={index}>
              <GoalCardSkeleton />
            </div>
          ))}
        </div>
      )}

      {!goalsQuery.isError && isEmpty && (
        <Card>
          <CardBody className="text-center py-5">
            <IconifyIcon icon="lucide:target" className="fs-1 text-muted mb-3" />
            <h5>{t('empty.title')}</h5>
            <p className="text-muted mb-3">{t('empty.description')}</p>
            <Button variant="primary" onClick={handleOpenCreate}>
              {t('empty.createGoal')}
            </Button>
          </CardBody>
        </Card>
      )}

      {!goalsQuery.isError && !isInitialLoading && !isEmpty && (
        <div className="row row-cols-1 row-cols-md-2 row-cols-lg-3 g-3">
          {sortedGoals.map((goal) => (
            <div className="col" key={goal.id}>
              <GoalCard
                goal={goal}
                accessToken={token}
                linkedSourceName={linkedSourceNameFor(goal)}
                onEdit={handleOpenEdit}
              />
            </div>
          ))}
        </div>
      )}

      <GoalDialog
        show={dialogOpen}
        onHide={() => setDialogOpen(false)}
        editTarget={editTarget}
        accounts={accountsQuery.data ?? []}
        investments={investmentsQuery.data?.items ?? []}
      />
    </div>
  )
}
