import { useQuery } from '@tanstack/react-query'

import type { IncomeExpenseMonthItem } from '@/components/charts/IncomeExpenseBarChart'
import type { NetWorthEvolutionPoint } from '@/features/networth/api'
import type { RecurringListResponse } from '@/features/recurring/api'
import { apiFetch } from '@/lib/apiClient'

/**
 * Request/response contracts mirroring v1.0 Phase 7's exact GET /api/reports/*
 * response shapes (see 13-07-PLAN.md's <interfaces> block) -- no `any`, so a
 * backend contract change surfaces here as a type error. Every hook follows
 * useRecurringQuery's exact pattern (accessToken passed explicitly, TanStack
 * Query v5, `enabled: !!accessToken`), with every filter param embedded in
 * both the queryKey (per-filter-combination caching) and the query-string
 * built via the local `buildQuery` helper.
 *
 * IncomeExpenseMonthItem is reused from the chart primitive that already
 * declares it (`@/components/charts/IncomeExpenseBarChart`); NetWorthEvolutionPoint
 * is reused from `@/features/networth/api` (structurally identical {date,
 * netWorth} shape -- frontend2 has no separate NetWorthLineChart component);
 * RecurringListResponse is reused from `@/features/recurring/api` (Plan
 * 13-02's export). None of these are redeclared here.
 *
 * downloadExport lives in `@/lib/downloadExport` (Plan 13-01) -- this file has
 * zero download/Blob code.
 */

export interface CategorySpendItem {
  categoryId: string | null
  categoryName: string | null
  categoryIcon: string | null
  categoryColor: string | null
  amount: number
}

export interface CashFlowByAccountItem {
  accountId: string
  accountName: string
  inflow: number
  outflow: number
  net: number
}

export interface CardStatementItem {
  accountId: string
  accountName: string
  year: number
  month: number
  statementTotal: number
  creditLimit: number | null
  balanceDueDate: string | null
}

export interface BudgetRealizedItem {
  categoryId: string
  categoryName: string
  plannedAmount: number
  realizedAmount: number
  variance: number
}

export interface WeekdaySpendItem {
  dayOfWeek: number
  totalAmount: number
  transactionCount: number
  averageAmount: number
}

export type NetWorthEvolutionDays = 30 | 90 | 365

function buildQuery(params: Record<string, string | number | boolean | undefined>): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined) search.set(key, String(value))
  }
  const query = search.toString()
  return query ? `?${query}` : ''
}

export function useSpendingByCategoryReportQuery(accessToken: string, dateFrom?: string, dateTo?: string) {
  return useQuery({
    queryKey: ['reports-spending-by-category', dateFrom, dateTo],
    queryFn: () =>
      apiFetch<CategorySpendItem[]>(
        `/api/reports/spending-by-category${buildQuery({ dateFrom, dateTo })}`,
        { accessToken }
      ),
    enabled: !!accessToken && !!dateFrom && !!dateTo,
  })
}

export function useIncomeExpenseReportQuery(accessToken: string, dateFrom?: string, dateTo?: string) {
  return useQuery({
    queryKey: ['reports-income-expense', dateFrom, dateTo],
    queryFn: () =>
      apiFetch<IncomeExpenseMonthItem[]>(
        `/api/reports/income-expense${buildQuery({ dateFrom, dateTo })}`,
        { accessToken }
      ),
    enabled: !!accessToken && !!dateFrom && !!dateTo,
  })
}

export function useNetWorthEvolutionReportQuery(accessToken: string, days: NetWorthEvolutionDays) {
  return useQuery({
    queryKey: ['reports-net-worth-evolution', days],
    queryFn: () =>
      apiFetch<NetWorthEvolutionPoint[]>(`/api/reports/net-worth-evolution${buildQuery({ days })}`, { accessToken }),
    enabled: !!accessToken,
  })
}

export function useCashFlowByAccountQuery(accessToken: string, dateFrom?: string, dateTo?: string) {
  return useQuery({
    queryKey: ['reports-cash-flow-by-account', dateFrom, dateTo],
    queryFn: () =>
      apiFetch<CashFlowByAccountItem[]>(
        `/api/reports/cash-flow-by-account${buildQuery({ dateFrom, dateTo })}`,
        { accessToken }
      ),
    enabled: !!accessToken && !!dateFrom && !!dateTo,
  })
}

export function useCardStatementsQuery(accessToken: string, dateFrom?: string, dateTo?: string) {
  return useQuery({
    queryKey: ['reports-card-statements', dateFrom, dateTo],
    queryFn: () =>
      apiFetch<CardStatementItem[]>(
        `/api/reports/card-statements${buildQuery({ dateFrom, dateTo })}`,
        { accessToken }
      ),
    enabled: !!accessToken && !!dateFrom && !!dateTo,
  })
}

export function useRecurringReportQuery(accessToken: string) {
  return useQuery({
    queryKey: ['reports-recurring'],
    queryFn: () => apiFetch<RecurringListResponse>('/api/reports/recurring', { accessToken }),
    enabled: !!accessToken,
  })
}

export function useBudgetRealizedVsPlannedQuery(accessToken: string, year?: number, month?: number) {
  return useQuery({
    queryKey: ['reports-budget-realized-vs-planned', year, month],
    queryFn: () =>
      apiFetch<BudgetRealizedItem[]>(
        `/api/reports/budget-realized-vs-planned${buildQuery({ year, month })}`,
        { accessToken }
      ),
    enabled: !!accessToken && !!year && !!month,
  })
}

export function useSpendingByWeekdayReportQuery(
  accessToken: string,
  dateFrom?: string,
  dateTo?: string,
  excludeInstallments = true
) {
  return useQuery({
    queryKey: ['reports-spending-by-weekday', dateFrom, dateTo, excludeInstallments],
    queryFn: () =>
      apiFetch<WeekdaySpendItem[]>(
        `/api/reports/spending-by-weekday${buildQuery({ dateFrom, dateTo, excludeInstallments })}`,
        { accessToken }
      ),
    enabled: !!accessToken && !!dateFrom && !!dateTo,
  })
}
