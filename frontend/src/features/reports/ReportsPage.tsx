import { useMemo, useState } from 'react'
import { Icon as IconifyIcon } from '@/components/Icon'
import { useNavigate } from 'react-router'
import { useTranslation } from 'react-i18next'
import {
  Badge,
  Button,
  ButtonGroup,
  Card,
  CardBody,
  Form,
  OverlayTrigger,
  Table,
  Tab,
  Tabs,
  Tooltip,
} from 'react-bootstrap'

import type { CategoryBarChartItem } from '@/components/charts/CategoryBarChart'
import { CategoryBarChart } from '@/components/charts/CategoryBarChart'
import { ChartCard } from '@/components/charts/ChartCard'
import { EvolutionLineChart } from '@/components/charts/EvolutionLineChart'
import { IncomeExpenseBarChart } from '@/components/charts/IncomeExpenseBarChart'
import { WeekdaySpendingChart } from '@/components/charts/WeekdaySpendingChart'
import { MoneyText } from '@/components/MoneyText'
import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { useAuth } from '@/features/auth/useAuth'
import { resolveCategoryIcon } from '@/features/categories/icons'
import type { RecurringItem } from '@/features/recurring/api'
import { MonthlySummarySection } from '@/features/reports/MonthlySummarySection'
import { downloadExport } from '@/lib/downloadExport'

import {
  useBudgetRealizedVsPlannedQuery,
  useCardStatementsQuery,
  useCashFlowByAccountQuery,
  useIncomeExpenseReportQuery,
  useNetWorthEvolutionReportQuery,
  useRecurringReportQuery,
  useSpendingByCategoryReportQuery,
  useSpendingByWeekdayReportQuery,
  type NetWorthEvolutionDays,
} from './api'

const DAYS_OPTIONS: NetWorthEvolutionDays[] = [30, 90, 365]
const MONTH_KEYS = [
  'january', 'february', 'march', 'april', 'may', 'june',
  'july', 'august', 'september', 'october', 'november', 'december',
] as const

/** Mirrors PeriodSelector.tsx's `toIsoDate` -- kept local, frontend2 has no date-fns dependency. */
function toIsoDate(date: Date): string {
  const year = date.getFullYear()
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

function formatRowDate(dateString: string): string {
  return new Intl.DateTimeFormat('pt-BR', { day: '2-digit', month: '2-digit', year: 'numeric' }).format(
    new Date(dateString)
  )
}

function formatMonthYear(year: number, month: number): string {
  return new Intl.DateTimeFormat('pt-BR', { month: 'short', year: 'numeric' }).format(new Date(year, month - 1, 1))
}

interface ExportButtonsProps {
  path: string
  filenameBase: string
  accessToken: string
}

/**
 * "Export CSV" / "Export Excel" pair -- appends `format=csv|xlsx` onto the
 * caller's already-filter-qualified path, downloads via `downloadExport`,
 * shows a failure notification on any non-2xx response (RPT-02). Reused by
 * every tab in this screen.
 */
function ExportButtons({ path, filenameBase, accessToken }: ExportButtonsProps) {
  const { t } = useTranslation('reports')
  const { showNotification } = useNotificationContext()
  const [pending, setPending] = useState<'csv' | 'xlsx' | null>(null)

  async function handleExport(format: 'csv' | 'xlsx') {
    setPending(format)
    try {
      const separator = path.includes('?') ? '&' : '?'
      await downloadExport(`${path}${separator}format=${format}`, accessToken, `${filenameBase}.${format}`)
    } catch {
      showNotification({ message: t('export.error'), variant: 'danger' })
    } finally {
      setPending(null)
    }
  }

  return (
    <div className="d-flex align-items-center gap-2">
      <Button variant="outline-secondary" size="sm" disabled={pending !== null} onClick={() => void handleExport('csv')}>
        {pending === 'csv' ? t('export.csvPending') : t('export.csv')}
      </Button>
      <Button variant="outline-secondary" size="sm" disabled={pending !== null} onClick={() => void handleExport('xlsx')}>
        {pending === 'xlsx' ? t('export.excelPending') : t('export.excel')}
      </Button>
    </div>
  )
}

interface RecurringReportRowProps {
  item: RecurringItem
}

/**
 * One "Recurring" tab table row -- reuses Plan 13-02's RecurringPage.tsx
 * New/Price-increase Badge+Tooltip pattern and category-icon-resolution
 * pattern verbatim, and its opacity-0.5-when-cancelled row convention, so
 * both tables render the same underlying data with a visually consistent
 * look.
 */
function RecurringReportRow({ item }: RecurringReportRowProps) {
  const { t } = useTranslation('reports')
  const categoryIcon = item.categoryId ? resolveCategoryIcon(item.categoryIcon) : 'lucide:wallet'

  return (
    <tr style={!item.isActive ? { opacity: 0.5 } : undefined}>
      <td className="fw-semibold">
        <div className="d-flex align-items-center gap-2">
          <span>{item.displayName}</span>
          {item.isNewlyDetected && <Badge bg="secondary">{t('recurringTable.new')}</Badge>}
          {item.amountIncreaseDetected && (
            <OverlayTrigger
              placement="top"
              overlay={
                <Tooltip>
                  {item.previousAverageAmount !== null && (
                    <span className="d-inline-flex align-items-center gap-1">
                      <MoneyText amount={item.previousAverageAmount} variant="neutral" showSign={false} />
                      {' -> '}
                      <MoneyText amount={item.averageAmount} variant="neutral" showSign={false} />
                    </span>
                  )}
                </Tooltip>
              }
            >
              <Badge bg="danger-subtle" text="danger" className="d-inline-flex align-items-center gap-1">
                <IconifyIcon icon="lucide:trending-up" />
                {t('recurringTable.priceIncrease')}
              </Badge>
            </OverlayTrigger>
          )}
        </div>
      </td>
      <td>
        <div className="d-flex align-items-center gap-2">
          <IconifyIcon icon={categoryIcon} className="text-muted" />
          <span>{item.categoryId ? item.categoryName : t('recurringTable.uncategorized')}</span>
        </div>
      </td>
      <td className="text-end">
        <MoneyText amount={item.averageAmount} variant="expense" />
      </td>
      <td>{item.frequency}</td>
      <td className="text-muted">{formatRowDate(item.lastSeenDate)}</td>
      <td className="text-muted">{formatRowDate(item.nextExpectedDate)}</td>
      <td className="text-end">
        <MoneyText amount={item.yearlyTotal} variant="expense" />
      </td>
    </tr>
  )
}

/**
 * RPT-01/RPT-02: the Reports screen -- 7 tabbed, mostly period-filterable
 * reports (reusing Phase 12's ApexCharts primitives for the 3 chart-shaped
 * reports, a react-bootstrap Table for the remaining 4) plus per-tab
 * CSV/Excel export buttons, wired to the unchanged GET /api/reports/* and
 * GET /api/export/* backend from v1.0's Phase 7.
 */
export function ReportsPage() {
  const { t } = useTranslation('reports')
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  const navigate = useNavigate()

  const [period, setPeriod] = useState(() => {
    const now = new Date()
    return {
      dateFrom: toIsoDate(new Date(now.getFullYear(), now.getMonth(), 1)),
      dateTo: toIsoDate(new Date(now.getFullYear(), now.getMonth() + 1, 0)),
    }
  })
  const [days, setDays] = useState<NetWorthEvolutionDays>(30)

  const now = new Date()
  const [year, setYear] = useState(now.getFullYear())
  const [month, setMonth] = useState(now.getMonth() + 1)
  const [excludeInstallments, setExcludeInstallments] = useState(true)

  const spendingQuery = useSpendingByCategoryReportQuery(token, period.dateFrom, period.dateTo)
  const incomeExpenseQuery = useIncomeExpenseReportQuery(token, period.dateFrom, period.dateTo)
  const netWorthQuery = useNetWorthEvolutionReportQuery(token, days)
  const cashFlowQuery = useCashFlowByAccountQuery(token, period.dateFrom, period.dateTo)
  const cardStatementsQuery = useCardStatementsQuery(token, period.dateFrom, period.dateTo)
  const recurringQuery = useRecurringReportQuery(token)
  const budgetQuery = useBudgetRealizedVsPlannedQuery(token, year, month)
  const weekdayQuery = useSpendingByWeekdayReportQuery(token, period.dateFrom, period.dateTo, excludeInstallments)

  const spendingItems: CategoryBarChartItem[] = useMemo(() => {
    const rawItems = spendingQuery.data ?? []
    const total = rawItems.reduce((sum, item) => sum + item.amount, 0)
    return rawItems.map((item) => ({
      id: item.categoryId,
      label: item.categoryName ?? t('spendingByCategory.uncategorized'),
      color: item.categoryColor,
      amount: item.amount,
      percent: total > 0 ? (item.amount / total) * 100 : 0,
    }))
  }, [spendingQuery.data, t])

  const incomeExpenseItems = incomeExpenseQuery.data ?? []
  const netWorthPoints = netWorthQuery.data ?? []
  const cashFlowItems = cashFlowQuery.data ?? []
  const cardStatementItems = cardStatementsQuery.data ?? []
  const recurringItems = recurringQuery.data?.items ?? []
  const budgetItems = budgetQuery.data ?? []
  const weekdayItems = useMemo(() => {
    const labelsByDow: Record<number, string> = {
      0: t('weekdayChart.days.sun'),
      1: t('weekdayChart.days.mon'),
      2: t('weekdayChart.days.tue'),
      3: t('weekdayChart.days.wed'),
      4: t('weekdayChart.days.thu'),
      5: t('weekdayChart.days.fri'),
      6: t('weekdayChart.days.sat'),
    }
    return (weekdayQuery.data ?? []).map((item) => ({
      dayOfWeek: item.dayOfWeek,
      label: labelsByDow[item.dayOfWeek] ?? String(item.dayOfWeek),
      totalAmount: item.totalAmount,
      transactionCount: item.transactionCount,
      averageAmount: item.averageAmount,
    }))
  }, [weekdayQuery.data, t])
  const hasWeekdaySpending = weekdayItems.some((item) => item.totalAmount > 0)

  return (
    <div>
      <h4 className="mb-3">{t('title')}</h4>

      <MonthlySummarySection />

      <Card className="mb-3">
        <CardBody>
          <Form.Label className="fs-xs text-muted mb-2">{t('period.label')}</Form.Label>
          <div className="d-flex flex-wrap gap-3">
            <div>
              <Form.Label className="fs-xs text-muted mb-1">{t('period.from')}</Form.Label>
              <Form.Control
                type="date"
                value={period.dateFrom}
                onChange={(event) => setPeriod((prev) => ({ ...prev, dateFrom: event.target.value }))}
              />
            </div>
            <div>
              <Form.Label className="fs-xs text-muted mb-1">{t('period.to')}</Form.Label>
              <Form.Control
                type="date"
                value={period.dateTo}
                onChange={(event) => setPeriod((prev) => ({ ...prev, dateTo: event.target.value }))}
              />
            </div>
          </div>
        </CardBody>
      </Card>

      <Tabs defaultActiveKey="by-category" className="mb-3">
        <Tab eventKey="by-category" title={t('tabs.byCategory')}>
          <ChartCard
            title={t('spendingByCategory.title')}
            action={
              <ExportButtons
                path={`/api/export/reports/spending-by-category?dateFrom=${period.dateFrom}&dateTo=${period.dateTo}`}
                filenameBase={`spending-by-category-${period.dateFrom}`}
                accessToken={token}
              />
            }
            isLoading={spendingQuery.isPending}
            isError={spendingQuery.isError}
            onRetry={() => void spendingQuery.refetch()}
            isEmpty={!spendingQuery.isPending && spendingItems.length === 0}
            emptyMessage={t('spendingByCategory.empty')}
          >
            <CategoryBarChart
              items={spendingItems}
              onBarClick={(item) => {
                const categoryParam = item.id ? `categoryId=${item.id}&` : ''
                navigate(`/transactions?${categoryParam}dateFrom=${period.dateFrom}&dateTo=${period.dateTo}`)
              }}
            />
          </ChartCard>
        </Tab>

        <Tab eventKey="income-expense" title={t('tabs.incomeExpense')}>
          <ChartCard
            title={t('incomeExpenseChart.title')}
            action={
              <ExportButtons
                path={`/api/export/reports/income-expense?dateFrom=${period.dateFrom}&dateTo=${period.dateTo}`}
                filenameBase={`income-expense-${period.dateFrom}`}
                accessToken={token}
              />
            }
            isLoading={incomeExpenseQuery.isPending}
            isError={incomeExpenseQuery.isError}
            onRetry={() => void incomeExpenseQuery.refetch()}
            isEmpty={!incomeExpenseQuery.isPending && incomeExpenseItems.length === 0}
            emptyMessage={t('incomeExpenseChart.empty')}
          >
            <IncomeExpenseBarChart items={incomeExpenseItems} />
          </ChartCard>
        </Tab>

        <Tab eventKey="net-worth" title={t('tabs.netWorth')}>
          <ChartCard
            title={t('netWorthChart.title')}
            action={
              <div className="d-flex flex-wrap align-items-center gap-2">
                <ButtonGroup size="sm">
                  {DAYS_OPTIONS.map((option) => (
                    <Button
                      key={option}
                      variant={days === option ? 'primary' : 'outline-secondary'}
                      onClick={() => setDays(option)}
                    >
                      {option}D
                    </Button>
                  ))}
                </ButtonGroup>
                <ExportButtons
                  path={`/api/export/reports/net-worth-evolution?days=${days}`}
                  filenameBase={`net-worth-evolution-${days}d`}
                  accessToken={token}
                />
              </div>
            }
            isLoading={netWorthQuery.isPending}
            isError={netWorthQuery.isError}
            onRetry={() => void netWorthQuery.refetch()}
            isEmpty={!netWorthQuery.isPending && netWorthPoints.length === 0}
            emptyMessage={t('netWorthChart.empty')}
          >
            <EvolutionLineChart
              points={netWorthPoints.map((point) => ({ date: point.date, value: point.netWorth }))}
              valueLabel={t('netWorthChart.valueLabel')}
            />
          </ChartCard>
        </Tab>

        <Tab eventKey="cash-flow" title={t('tabs.cashFlow')}>
          <ChartCard
            title={t('cashFlowTable.title')}
            action={
              <ExportButtons
                path={`/api/export/reports/cash-flow-by-account?dateFrom=${period.dateFrom}&dateTo=${period.dateTo}`}
                filenameBase={`cash-flow-by-account-${period.dateFrom}`}
                accessToken={token}
              />
            }
            isLoading={cashFlowQuery.isPending}
            isError={cashFlowQuery.isError}
            onRetry={() => void cashFlowQuery.refetch()}
            isEmpty={!cashFlowQuery.isPending && cashFlowItems.length === 0}
            emptyMessage={t('cashFlowTable.empty')}
          >
            <div className="table-responsive">
              <Table className="table-custom mb-0">
                <thead>
                  <tr>
                    <th>{t('cashFlowTable.columns.account')}</th>
                    <th className="text-end">{t('cashFlowTable.columns.inflow')}</th>
                    <th className="text-end">{t('cashFlowTable.columns.outflow')}</th>
                    <th className="text-end">{t('cashFlowTable.columns.net')}</th>
                  </tr>
                </thead>
                <tbody>
                  {cashFlowItems.map((item) => (
                    <tr key={item.accountId}>
                      <td>{item.accountName}</td>
                      <td className="text-end">
                        <MoneyText amount={item.inflow} variant="income" showSign={false} />
                      </td>
                      <td className="text-end">
                        <MoneyText amount={item.outflow} variant="expense" showSign={false} />
                      </td>
                      <td className="text-end">
                        <MoneyText amount={item.net} variant="neutral" />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            </div>
          </ChartCard>
        </Tab>

        <Tab eventKey="card-statements" title={t('tabs.cardStatements')}>
          <ChartCard
            title={t('cardStatementsTable.title')}
            action={
              <ExportButtons
                path={`/api/export/reports/card-statements?dateFrom=${period.dateFrom}&dateTo=${period.dateTo}`}
                filenameBase={`card-statements-${period.dateFrom}`}
                accessToken={token}
              />
            }
            isLoading={cardStatementsQuery.isPending}
            isError={cardStatementsQuery.isError}
            onRetry={() => void cardStatementsQuery.refetch()}
            isEmpty={!cardStatementsQuery.isPending && cardStatementItems.length === 0}
            emptyMessage={t('cardStatementsTable.empty')}
          >
            <div className="table-responsive">
              <Table className="table-custom mb-0">
                <thead>
                  <tr>
                    <th>{t('cardStatementsTable.columns.account')}</th>
                    <th>{t('cardStatementsTable.columns.month')}</th>
                    <th className="text-end">{t('cardStatementsTable.columns.statementTotal')}</th>
                    <th className="text-end">{t('cardStatementsTable.columns.creditLimit')}</th>
                    <th>{t('cardStatementsTable.columns.dueDate')}</th>
                  </tr>
                </thead>
                <tbody>
                  {cardStatementItems.map((item, index) => (
                    <tr key={`${item.accountId}-${item.year}-${item.month}-${index}`}>
                      <td>{item.accountName}</td>
                      <td className="text-muted">{formatMonthYear(item.year, item.month)}</td>
                      <td className="text-end">
                        <MoneyText amount={item.statementTotal} variant="expense" showSign={false} />
                      </td>
                      <td className="text-end text-muted">
                        {item.creditLimit !== null ? <MoneyText amount={item.creditLimit} variant="neutral" /> : '—'}
                      </td>
                      <td className="text-muted">
                        {item.balanceDueDate ? formatRowDate(item.balanceDueDate) : '—'}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            </div>
          </ChartCard>
        </Tab>

        <Tab eventKey="recurring" title={t('tabs.recurring')}>
          <ChartCard
            title={t('recurringTable.title')}
            action={<ExportButtons path="/api/export/reports/recurring" filenameBase="recurring" accessToken={token} />}
            isLoading={recurringQuery.isPending}
            isError={recurringQuery.isError}
            onRetry={() => void recurringQuery.refetch()}
            isEmpty={!recurringQuery.isPending && recurringItems.length === 0}
            emptyMessage={t('recurringTable.empty')}
          >
            <div className="table-responsive">
              <Table className="table-custom mb-0">
                <thead>
                  <tr>
                    <th>{t('recurringTable.columns.name')}</th>
                    <th>{t('recurringTable.columns.category')}</th>
                    <th className="text-end">{t('recurringTable.columns.avgAmount')}</th>
                    <th>{t('recurringTable.columns.frequency')}</th>
                    <th>{t('recurringTable.columns.lastCharge')}</th>
                    <th>{t('recurringTable.columns.nextEstimated')}</th>
                    <th className="text-end">{t('recurringTable.columns.yearlyTotal')}</th>
                  </tr>
                </thead>
                <tbody>
                  {recurringItems.map((item) => (
                    <RecurringReportRow key={item.id} item={item} />
                  ))}
                </tbody>
              </Table>
            </div>
          </ChartCard>
        </Tab>

        <Tab eventKey="budget" title={t('tabs.budget')}>
          <ChartCard
            title={t('budgetTable.title')}
            action={
              <div className="d-flex flex-wrap align-items-center gap-2">
                <Form.Select
                  style={{ width: 100 }}
                  value={String(year)}
                  onChange={(event) => setYear(Number(event.target.value))}
                >
                  {[now.getFullYear() - 1, now.getFullYear(), now.getFullYear() + 1].map((option) => (
                    <option key={option} value={option}>
                      {option}
                    </option>
                  ))}
                </Form.Select>
                <Form.Select
                  style={{ width: 140 }}
                  value={String(month)}
                  onChange={(event) => setMonth(Number(event.target.value))}
                >
                  {MONTH_KEYS.map((key, index) => (
                    <option key={key} value={String(index + 1)}>
                      {t(`months.${key}`)}
                    </option>
                  ))}
                </Form.Select>
                <ExportButtons
                  path={`/api/export/reports/budget-realized-vs-planned?year=${year}&month=${month}`}
                  filenameBase={`budget-realized-vs-planned-${year}-${String(month).padStart(2, '0')}`}
                  accessToken={token}
                />
              </div>
            }
            isLoading={budgetQuery.isPending}
            isError={budgetQuery.isError}
            onRetry={() => void budgetQuery.refetch()}
            isEmpty={!budgetQuery.isPending && budgetItems.length === 0}
            emptyMessage={t('budgetTable.empty')}
          >
            <div className="table-responsive">
              <Table className="table-custom mb-0">
                <thead>
                  <tr>
                    <th>{t('budgetTable.columns.category')}</th>
                    <th className="text-end">{t('budgetTable.columns.planned')}</th>
                    <th className="text-end">{t('budgetTable.columns.realized')}</th>
                    <th className="text-end">{t('budgetTable.columns.variance')}</th>
                  </tr>
                </thead>
                <tbody>
                  {budgetItems.map((item) => (
                    <tr key={item.categoryId}>
                      <td>{item.categoryName}</td>
                      <td className="text-end">
                        <MoneyText amount={item.plannedAmount} variant="neutral" />
                      </td>
                      <td className="text-end">
                        <MoneyText amount={item.realizedAmount} variant="expense" showSign={false} />
                      </td>
                      <td className="text-end">
                        <MoneyText amount={item.variance} variant={item.variance < 0 ? 'expense' : 'income'} />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            </div>
          </ChartCard>
        </Tab>

        <Tab eventKey="weekday" title={t('tabs.byWeekday')}>
          <ChartCard
            title={t('weekdayChart.title')}
            action={
              <div className="d-flex flex-wrap align-items-center gap-3">
                <Form.Check
                  type="switch"
                  id="reports-weekday-exclude-installments"
                  label={t('weekdayChart.excludeInstallments')}
                  checked={excludeInstallments}
                  onChange={(event) => setExcludeInstallments(event.target.checked)}
                />
                <ExportButtons
                  path={`/api/export/reports/spending-by-weekday?dateFrom=${period.dateFrom}&dateTo=${period.dateTo}&excludeInstallments=${excludeInstallments}`}
                  filenameBase={`spending-by-weekday-${period.dateFrom}-${period.dateTo}`}
                  accessToken={token}
                />
              </div>
            }
            isLoading={weekdayQuery.isPending}
            isError={weekdayQuery.isError}
            onRetry={() => void weekdayQuery.refetch()}
            isEmpty={!weekdayQuery.isPending && !hasWeekdaySpending}
            emptyMessage={t('weekdayChart.empty')}
          >
            <WeekdaySpendingChart
              items={weekdayItems}
              seriesLabel={t('weekdayChart.seriesLabel')}
              transactionsWord={t('weekdayChart.transactionsWord')}
              averageLabel={t('weekdayChart.averageLabel')}
            />
          </ChartCard>
        </Tab>
      </Tabs>
    </div>
  )
}
