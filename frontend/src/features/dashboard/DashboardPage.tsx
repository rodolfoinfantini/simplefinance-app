import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { Alert, Button, ButtonGroup, Card, CardBody, Col, Placeholder, Row } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import { CategoryBarChart } from '@/components/charts/CategoryBarChart'
import { ChartCard } from '@/components/charts/ChartCard'
import { EvolutionLineChart } from '@/components/charts/EvolutionLineChart'
import { IncomeExpenseBarChart } from '@/components/charts/IncomeExpenseBarChart'
import { StatTile } from '@/components/StatTile'
import { useAuth } from '@/features/auth/useAuth'
import { AiInsightStrip } from '@/features/dashboard/AiInsightStrip'
import { AtRiskBudgetCard } from '@/features/dashboard/AtRiskBudgetCard'
import { ForecastWidget } from '@/features/dashboard/ForecastWidget'
import {
  useAtRiskBudgetsQuery,
  useDashboardSummaryQuery,
  useIncomeExpenseQuery,
  useNetWorthEvolutionQuery,
  useSpendingByCategoryQuery,
  useSpendingByParentCategoryQuery,
  useUpcomingDueDatesQuery,
} from '@/features/dashboard/api'
import { PeriodSelector, thisMonthPeriod, type DashboardPeriod } from '@/features/dashboard/PeriodSelector'
import { UpcomingDueDateRow } from '@/features/dashboard/UpcomingDueDateRow'

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

/**
 * FE-DASH-01..03: the full Dashboard screen (index route `/`) — 6 stat
 * tiles, spending-by-category, net-worth evolution, income-vs-expenses,
 * at-risk budgets, upcoming due dates, and a period selector — each region
 * independently loading, mirroring v1's per-region States table (no single
 * top-level spinner gating the whole page).
 */
export function DashboardPage() {
  const { t } = useTranslation('dashboard')
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  const navigate = useNavigate()

  const [period, setPeriod] = useState<DashboardPeriod>(() => thisMonthPeriod())
  const [netWorthRange, setNetWorthRange] = useState<6 | 12>(6)

  const summaryQuery = useDashboardSummaryQuery(token, period.dateFrom, period.dateTo)
  const spendingByParentCategoryQuery = useSpendingByParentCategoryQuery(token, period.dateFrom, period.dateTo)
  const spendingByCategoryQuery = useSpendingByCategoryQuery(token, period.dateFrom, period.dateTo)
  const incomeExpenseQuery = useIncomeExpenseQuery(token)
  const netWorthEvolutionQuery = useNetWorthEvolutionQuery(token, netWorthRange)
  const atRiskBudgetsQuery = useAtRiskBudgetsQuery(token)
  const upcomingDueDatesQuery = useUpcomingDueDatesQuery(token)

  const summary = summaryQuery.data
  const parentSpendingItems = spendingByParentCategoryQuery.data?.items ?? []
  const spendingItems = spendingByCategoryQuery.data?.items ?? []
  const netWorthPoints = netWorthEvolutionQuery.data?.points ?? []
  const incomeExpenseItems = incomeExpenseQuery.data?.items ?? []
  const atRiskItems = (atRiskBudgetsQuery.data?.items ?? []).slice(0, 4)
  const upcomingDueDates = upcomingDueDatesQuery.data?.items ?? []

  return (
    <>
      <div className="d-flex flex-wrap align-items-center justify-content-between gap-3 mb-3">
        <h4 className="mb-0">{t('pageTitle')}</h4>
        <PeriodSelector value={period} onChange={setPeriod} />
      </div>

      <AiInsightStrip />

      <ForecastWidget />

      {summaryQuery.isError && (
        <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3">
          <span>{t('loadError')}</span>
          <Button variant="outline-danger" size="sm" onClick={() => void summaryQuery.refetch()}>
            {t('common:actions.retry')}
          </Button>
        </Alert>
      )}

      <Row className="g-3 row-cols-1 row-cols-sm-2 row-cols-lg-3 mb-3">
        {summaryQuery.isPending
          ? Array.from({ length: 6 }).map((_, index) => <StatTileSkeleton key={index} />)
          : summary && (
              <>
                <Col>
                  <StatTile label={t('stat.netWorth')} amount={summary.netWorth} accent />
                </Col>
                <Col>
                  <StatTile label={t('stat.totalBalance')} amount={summary.totalBalance} />
                </Col>
                <Col>
                  <StatTile label={t('stat.cardDebt')} amount={summary.totalCardDebt} variant="expense" />
                </Col>
                <Col>
                  <StatTile label={t('stat.incomeThisMonth')} amount={summary.periodIncome} variant="income" />
                </Col>
                <Col>
                  <StatTile label={t('stat.expensesThisMonth')} amount={summary.periodExpense} variant="expense" />
                </Col>
                <Col>
                  <StatTile label={t('stat.netThisMonth')} amount={summary.periodNet} />
                </Col>
              </>
            )}
      </Row>

      <Row className="g-3 row-cols-1 row-cols-xl-2 mb-3">
        <Col>
          <ChartCard
            title={t('chart.spendingByParentCategory.title')}
            isLoading={spendingByParentCategoryQuery.isPending}
            isError={spendingByParentCategoryQuery.isError}
            onRetry={() => void spendingByParentCategoryQuery.refetch()}
            isEmpty={!spendingByParentCategoryQuery.isPending && parentSpendingItems.length === 0}
            emptyMessage={t('chart.spendingByParentCategory.emptyMessage')}
          >
            <CategoryBarChart
              height={Math.max(280, parentSpendingItems.length * 34)}
              items={parentSpendingItems.map((item) => ({
                id: item.categoryId,
                label:
                  item.categoryId === null && item.categoryName === 'Uncategorized'
                    ? t('chart.spendingByCategory.uncategorized')
                    : item.categoryId === null && item.categoryName === 'Others'
                      ? t('chart.spendingByCategory.others')
                      : item.categoryName,
                color: item.categoryColor,
                amount: item.amount,
                percent: item.percent,
              }))}
              onBarClick={(item) => {
                const categoryParam = item.id ? `categoryId=${item.id}&` : ''
                navigate(`/transactions?${categoryParam}dateFrom=${period.dateFrom}&dateTo=${period.dateTo}`)
              }}
            />
          </ChartCard>
        </Col>

        <Col>
          <ChartCard
            title={t('chart.spendingByCategory.title')}
            isLoading={spendingByCategoryQuery.isPending}
            isError={spendingByCategoryQuery.isError}
            onRetry={() => void spendingByCategoryQuery.refetch()}
            isEmpty={!spendingByCategoryQuery.isPending && spendingItems.length === 0}
            emptyMessage={t('chart.spendingByCategory.emptyMessage')}
          >
            <CategoryBarChart
              items={spendingItems.map((item) => ({
                id: item.categoryId,
                // Backend emits sentinel names for the null-category bucket ("Uncategorized")
                // and the top-5 remainder ("Others"); translate them here.
                label:
                  item.categoryId === null && item.categoryName === 'Uncategorized'
                    ? t('chart.spendingByCategory.uncategorized')
                    : item.categoryId === null && item.categoryName === 'Others'
                      ? t('chart.spendingByCategory.others')
                      : item.categoryName,
                color: item.categoryColor,
                amount: item.amount,
                percent: item.percent,
              }))}
              onBarClick={(item) => {
                const categoryParam = item.id ? `categoryId=${item.id}&` : ''
                navigate(`/transactions?${categoryParam}dateFrom=${period.dateFrom}&dateTo=${period.dateTo}`)
              }}
            />
          </ChartCard>
        </Col>
      </Row>

      <Row className="g-3 row-cols-1 row-cols-xl-2 mb-3">
        <Col>
          <ChartCard
            title={t('chart.netWorthOverTime.title')}
            action={
              <ButtonGroup size="sm">
                <Button
                  variant={netWorthRange === 6 ? 'primary' : 'outline-secondary'}
                  onClick={() => setNetWorthRange(6)}
                >
                  {t('chart.netWorthOverTime.ranges.6m')}
                </Button>
                <Button
                  variant={netWorthRange === 12 ? 'primary' : 'outline-secondary'}
                  onClick={() => setNetWorthRange(12)}
                >
                  {t('chart.netWorthOverTime.ranges.12m')}
                </Button>
              </ButtonGroup>
            }
            isLoading={netWorthEvolutionQuery.isPending}
            isError={netWorthEvolutionQuery.isError}
            onRetry={() => void netWorthEvolutionQuery.refetch()}
            isEmpty={!netWorthEvolutionQuery.isPending && netWorthPoints.length === 0}
            emptyMessage={t('chart.netWorthOverTime.emptyMessage')}
          >
            <EvolutionLineChart
              points={netWorthPoints.map((point) => ({ date: point.date, value: point.netWorth }))}
              valueLabel={t('chart.netWorthOverTime.valueLabel')}
            />
          </ChartCard>
        </Col>

        <Col>
          <ChartCard
            title={t('chart.incomeVsExpenses.title')}
            isLoading={incomeExpenseQuery.isPending}
            isError={incomeExpenseQuery.isError}
            onRetry={() => void incomeExpenseQuery.refetch()}
          >
            <IncomeExpenseBarChart items={incomeExpenseItems} />
          </ChartCard>
        </Col>
      </Row>

      <Row className="g-3 row-cols-1 row-cols-xl-2">
        <Col>
          <ChartCard
            title={t('chart.budgetsAtRisk.title')}
            action={
              <Link to="/budget" className="fw-semibold small text-decoration-none">
                {t('chart.budgetsAtRisk.viewAll')}
              </Link>
            }
            isLoading={atRiskBudgetsQuery.isPending}
            isError={atRiskBudgetsQuery.isError}
            onRetry={() => void atRiskBudgetsQuery.refetch()}
            isEmpty={!atRiskBudgetsQuery.isPending && atRiskItems.length === 0}
            emptyMessage={t('chart.budgetsAtRisk.emptyMessage')}
          >
            <div className="d-flex flex-column gap-3">
              {atRiskItems.map((item) => (
                <AtRiskBudgetCard key={item.categoryId} item={item} />
              ))}
            </div>
          </ChartCard>
        </Col>

        <Col>
          <ChartCard
            title={t('chart.upcomingDueDates.title')}
            isLoading={upcomingDueDatesQuery.isPending}
            isError={upcomingDueDatesQuery.isError}
            onRetry={() => void upcomingDueDatesQuery.refetch()}
            isEmpty={!upcomingDueDatesQuery.isPending && upcomingDueDates.length === 0}
            emptyMessage={t('chart.upcomingDueDates.emptyMessage')}
          >
            <div className="d-flex flex-column">
              {upcomingDueDates.map((item) => (
                <UpcomingDueDateRow key={item.accountId} item={item} />
              ))}
            </div>
          </ChartCard>
        </Col>
      </Row>
    </>
  )
}
