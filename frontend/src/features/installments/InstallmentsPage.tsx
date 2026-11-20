import { useState } from 'react'
import {
  Alert,
  Badge,
  Button,
  Card,
  CardBody,
  Col,
  Form,
  Placeholder,
  ProgressBar,
  Row,
  Table,
} from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import { Icon as IconifyIcon } from '@/components/Icon'
import { MoneyText } from '@/components/MoneyText'
import { StatTile } from '@/components/StatTile'
import { formatAccountOptionLabel, useAccountsQuery } from '@/features/accounts/api'
import { useAuth } from '@/features/auth/useAuth'
import { resolveCategoryIcon } from '@/features/categories/icons'

import { useInstallmentsQuery, type InstallmentPlanItem } from './api'

const monthYearFormatter = new Intl.DateTimeFormat('pt-BR', {
  month: '2-digit',
  year: 'numeric',
  timeZone: 'UTC',
})

const fullDateFormatter = new Intl.DateTimeFormat('pt-BR', {
  day: '2-digit',
  month: '2-digit',
  year: 'numeric',
  timeZone: 'UTC',
})

function formatMonthYear(dateString: string): string {
  return monthYearFormatter.format(new Date(dateString))
}

function formatFullDate(dateString: string): string {
  return fullDateFormatter.format(new Date(dateString))
}

function StatTileSkeleton() {
  return (
    <Col>
      <Card>
        <CardBody>
          <Placeholder as="p" animation="glow" className="mb-1">
            <Placeholder xs={7} />
          </Placeholder>
          <Placeholder as="p" animation="glow" className="mb-0">
            <Placeholder xs={4} style={{ height: 28 }} />
          </Placeholder>
        </CardBody>
      </Card>
    </Col>
  )
}

function RowSkeleton() {
  return (
    <tr>
      {Array.from({ length: 7 }).map((_, index) => (
        <td key={index}>
          <Placeholder as="span" animation="glow">
            <Placeholder xs={8} />
          </Placeholder>
        </td>
      ))}
    </tr>
  )
}

function InstallmentRow({ item }: { item: InstallmentPlanItem }) {
  const { t } = useTranslation('installments')
  const categoryIcon = item.categoryId ? resolveCategoryIcon(item.categoryIcon) : 'lucide:wallet'
  const paidInstallments = Math.max(0, item.totalInstallments - item.remainingInstallments)
  const progressPercent = Math.min(100, Math.max(0, (paidInstallments / item.totalInstallments) * 100))

  return (
    <tr>
      <td className="fw-semibold">
        <div>{item.description}</div>
        <div className="text-muted fs-xs">
          {t('row.totalPurchase')}{' '}
          <MoneyText amount={item.totalAmount} variant="neutral" showSign={false} />
        </div>
      </td>
      <td>
        <span className="text-muted">{item.institutionName ?? item.accountName}</span>
      </td>
      <td>
        <div className="d-flex align-items-center gap-2">
          <IconifyIcon icon={categoryIcon} className="text-muted" />
          {item.categoryId && item.categoryColor && (
            <span
              className="d-inline-block rounded-circle"
              style={{ width: 8, height: 8, backgroundColor: item.categoryColor }}
            />
          )}
          <span>{item.categoryId ? item.categoryName : t('row.uncategorized')}</span>
        </div>
      </td>
      <td style={{ minWidth: 150 }}>
        <div className="d-flex align-items-center justify-content-between gap-2 mb-1">
          <Badge bg="primary">
            {item.currentInstallment}/{item.totalInstallments}
          </Badge>
          <span className="text-muted fs-xs">
            {t('row.remainingCount', { count: item.remainingInstallments })}
          </span>
        </div>
        <ProgressBar now={progressPercent} style={{ height: 6 }} />
      </td>
      <td className="text-end">
        <MoneyText amount={item.installmentAmount} variant="neutral" showSign={false} />
      </td>
      <td className="text-end fw-semibold">
        <MoneyText amount={item.remainingAmount} variant="expense" showSign={false} />
      </td>
      <td>
        <div className="fw-medium">{formatMonthYear(item.lastInstallmentDate)}</div>
        <div className="text-muted fs-xs">{formatFullDate(item.lastInstallmentDate)}</div>
      </td>
    </tr>
  )
}

export function InstallmentsPage() {
  const { t } = useTranslation(['installments', 'common'])
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  const [accountId, setAccountId] = useState<string>('')

  const accountsQuery = useAccountsQuery(token)
  const installmentsQuery = useInstallmentsQuery(token, accountId || undefined)

  const creditAccounts = (accountsQuery.data ?? []).filter((a) => a.type === 'Credit')
  const data = installmentsQuery.data
  const items = data?.items ?? []
  const isInitialLoading = installmentsQuery.isLoading
  const isEmpty = !isInitialLoading && !installmentsQuery.isError && items.length === 0

  return (
    <div>
      <div className="d-flex flex-wrap align-items-center justify-content-between gap-3 mb-3">
        <h4 className="mb-0">{t('pageTitle')}</h4>
        {creditAccounts.length > 0 && (
          <div style={{ minWidth: 260 }}>
            <Form.Select
              value={accountId}
              onChange={(event) => setAccountId(event.target.value)}
              aria-label={t('filters.card')}
            >
              <option value="">{t('filters.allCards')}</option>
              {creditAccounts.map((account) => (
                <option key={account.id} value={account.id}>
                  {formatAccountOptionLabel(account)}
                </option>
              ))}
            </Form.Select>
          </div>
        )}
      </div>

      {installmentsQuery.isError && (
        <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3">
          <span>{t('loadError')}</span>
          <Button variant="outline-danger" size="sm" onClick={() => void installmentsQuery.refetch()}>
            {t('common:actions.retry')}
          </Button>
        </Alert>
      )}

      {!installmentsQuery.isError && (
        <Row className="g-3 row-cols-1 row-cols-md-3 mb-3">
          {isInitialLoading ? (
            <>
              <StatTileSkeleton />
              <StatTileSkeleton />
              <StatTileSkeleton />
            </>
          ) : (
            <>
              <Col>
                <StatTile
                  label={t('stat.totalRemaining')}
                  amount={data?.totalRemainingAmount ?? 0}
                  variant="expense"
                  accent
                />
              </Col>
              <Col>
                <StatTile
                  label={t('stat.monthlyCommitted')}
                  amount={data?.monthlyTotal ?? 0}
                  variant="expense"
                />
              </Col>
              <Col>
                <Card className="h-100">
                  <CardBody>
                    <p className="text-muted text-uppercase fs-xs fw-semibold mb-1">
                      {t('stat.activePlans')}
                    </p>
                    <div className="fs-3 fw-semibold">{data?.activeCount ?? 0}</div>
                  </CardBody>
                </Card>
              </Col>
            </>
          )}
        </Row>
      )}

      {!installmentsQuery.isError && isEmpty && (
        <Card>
          <CardBody className="text-center py-5">
            <IconifyIcon icon="lucide:credit-card" className="fs-1 text-muted mb-3" />
            <h5>{t('empty.title')}</h5>
            <p className="text-muted mb-0">{t('empty.description')}</p>
          </CardBody>
        </Card>
      )}

      {!installmentsQuery.isError && !isEmpty && (
        <Card>
          <div className="table-responsive">
            <Table className="table-custom align-middle mb-0">
              <thead>
                <tr>
                  <th>{t('table.description')}</th>
                  <th>{t('table.card')}</th>
                  <th>{t('table.category')}</th>
                  <th>{t('table.installment')}</th>
                  <th className="text-end">{t('table.installmentAmount')}</th>
                  <th className="text-end">{t('table.remainingAmount')}</th>
                  <th>{t('table.lastInstallmentDate')}</th>
                </tr>
              </thead>
              <tbody>
                {isInitialLoading &&
                  Array.from({ length: 6 }).map((_, index) => <RowSkeleton key={index} />)}

                {!isInitialLoading &&
                  items.map((item) => <InstallmentRow key={item.id} item={item} />)}
              </tbody>
            </Table>
          </div>
        </Card>
      )}
    </div>
  )
}
