import { useQuery } from '@tanstack/react-query'

import { apiFetch } from '@/lib/apiClient'

/**
 * Request/response contracts mirroring v1.0 Phase 5's exact GET /api/dashboard/*
 * response shapes (unchanged backend contract, see 12-03-PLAN.md's <interfaces>
 * block) — no `any`, so a backend contract change surfaces here as a type
 * error. Every hook follows frontend2's existing query-hook pattern
 * (accessToken passed explicitly per call, TanStack Query v5, `enabled: !!accessToken`).
 */

export interface DashboardSummary {
  netWorth: number
  totalBalance: number
  totalCardDebt: number
  periodIncome: number
  periodExpense: number
  periodNet: number
}

export interface SpendingByCategoryItem {
  categoryId: string | null
  categoryName: string
  categoryIcon: string | null
  categoryColor: string | null
  amount: number
  percent: number
}

export interface SpendingByCategoryResponse {
  items: SpendingByCategoryItem[]
  totalAmount: number
}

export interface IncomeExpenseMonthItem {
  year: number
  month: number
  income: number
  expense: number
}

export interface IncomeExpenseResponse {
  items: IncomeExpenseMonthItem[]
}

export interface NetWorthPoint {
  date: string
  netWorth: number
}

export interface NetWorthEvolutionResponse {
  points: NetWorthPoint[]
}

export type BudgetState = 'Ok' | 'Attention' | 'Exceeded'

export interface AtRiskBudgetItem {
  categoryId: string
  categoryName: string
  categoryIcon: string | null
  categoryColor: string | null
  percentOfCap: number
  projectedPercent: number
  state: BudgetState
}

export interface AtRiskBudgetsResponse {
  items: AtRiskBudgetItem[]
}

export interface UpcomingDueDateItem {
  accountId: string
  institutionName: string | null
  accountName: string
  dueDate: string
  statementBalance: number | null
}

export interface UpcomingDueDatesResponse {
  items: UpcomingDueDateItem[]
}

export interface SpendingPaceDayPoint {
  day: number
  currentMonth: number | null
  previousMonth: number
  threeMonthAverage: number
}

export interface SpendingPaceResponse {
  currentDay: number
  currentMonthTotal: number
  previousMonthAtSameDay: number
  threeMonthAverageAtSameDay: number
  points: SpendingPaceDayPoint[]
}

export interface TrendMonth {
  year: number
  month: number
}

export interface ParentCategoryTrendSeries {
  categoryId: string | null
  categoryName: string
  categoryColor: string | null
  amounts: number[]
}

export interface ParentCategoryTrendResponse {
  months: TrendMonth[]
  series: ParentCategoryTrendSeries[]
}

export function useDashboardSummaryQuery(accessToken: string, dateFrom?: string, dateTo?: string) {
  return useQuery({
    queryKey: ['dashboard-summary', dateFrom, dateTo],
    queryFn: () =>
      apiFetch<DashboardSummary>(
        `/api/dashboard/summary?dateFrom=${dateFrom}&dateTo=${dateTo}`,
        { accessToken }
      ),
    enabled: !!accessToken && !!dateFrom && !!dateTo,
  })
}

export function useSpendingByCategoryQuery(accessToken: string, dateFrom?: string, dateTo?: string) {
  return useQuery({
    queryKey: ['dashboard-spending-by-category', dateFrom, dateTo],
    queryFn: () =>
      apiFetch<SpendingByCategoryResponse>(
        `/api/dashboard/spending-by-category?dateFrom=${dateFrom}&dateTo=${dateTo}`,
        { accessToken }
      ),
    enabled: !!accessToken && !!dateFrom && !!dateTo,
  })
}

export function useSpendingByParentCategoryQuery(accessToken: string, dateFrom?: string, dateTo?: string) {
  return useQuery({
    queryKey: ['dashboard-spending-by-parent-category', dateFrom, dateTo],
    queryFn: () =>
      apiFetch<SpendingByCategoryResponse>(
        `/api/dashboard/spending-by-parent-category?dateFrom=${dateFrom}&dateTo=${dateTo}`,
        { accessToken }
      ),
    enabled: !!accessToken && !!dateFrom && !!dateTo,
  })
}

export function useIncomeExpenseQuery(accessToken: string, months: 6 | 12 = 6) {
  return useQuery({
    queryKey: ['dashboard-income-expense', months],
    queryFn: () => apiFetch<IncomeExpenseResponse>(`/api/dashboard/income-expense?months=${months}`, { accessToken }),
    enabled: !!accessToken,
  })
}

export function useSpendingPaceQuery(accessToken: string) {
  return useQuery({
    queryKey: ['dashboard-spending-pace'],
    queryFn: () => apiFetch<SpendingPaceResponse>('/api/dashboard/spending-pace', { accessToken }),
    enabled: !!accessToken,
  })
}

export function useParentCategoryTrendQuery(accessToken: string, months: 6 | 12) {
  return useQuery({
    queryKey: ['dashboard-parent-category-trend', months],
    queryFn: () =>
      apiFetch<ParentCategoryTrendResponse>(`/api/dashboard/parent-category-trend?months=${months}`, {
        accessToken,
      }),
    enabled: !!accessToken,
  })
}

export function useNetWorthEvolutionQuery(accessToken: string, months: 6 | 12) {
  return useQuery({
    queryKey: ['dashboard-net-worth-evolution', months],
    queryFn: () =>
      apiFetch<NetWorthEvolutionResponse>(`/api/dashboard/net-worth-evolution?months=${months}`, {
        accessToken,
      }),
    enabled: !!accessToken,
  })
}

export function useAtRiskBudgetsQuery(accessToken: string) {
  return useQuery({
    queryKey: ['dashboard-at-risk-budgets'],
    queryFn: () => apiFetch<AtRiskBudgetsResponse>('/api/dashboard/at-risk-budgets', { accessToken }),
    enabled: !!accessToken,
  })
}

export function useUpcomingDueDatesQuery(accessToken: string) {
  return useQuery({
    queryKey: ['dashboard-upcoming-due-dates'],
    queryFn: () =>
      apiFetch<UpcomingDueDatesResponse>('/api/dashboard/upcoming-due-dates', { accessToken }),
    enabled: !!accessToken,
  })
}
