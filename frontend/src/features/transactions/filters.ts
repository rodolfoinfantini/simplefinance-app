/**
 * Transaction feed filter state + pure query-string helpers. Kept separate
 * from api.ts so the filter shape/serialization can be unit-reasoned about
 * (and tested) independently of the TanStack Query wiring.
 *
 * `type`/`status` use the derived request-side vocabulary documented in
 * 03-07-PLAN.md's <interfaces> block (`income|expense|transfer`,
 * `posted|pending`) -- NOT the raw Debit/Credit or Pending/Posted enum
 * values that come back on each `TransactionListItem`.
 */

export interface TransactionFilters {
  dateFrom?: string
  dateTo?: string
  accountId?: string
  categoryId?: string
  type?: 'income' | 'expense' | 'transfer'
  status?: 'posted' | 'pending'
  amountMin?: number
  amountMax?: number
  search?: string
  excludeTransfers?: boolean
}

/**
 * Builds the `?page=&dateFrom=&...` query string for GET /api/transactions.
 * Omits any filter field that is undefined/empty so the backend sees only
 * the dimensions the user actually set (per 03-03's AND-combination
 * semantics -- an absent param means "don't filter on this dimension").
 */
export function buildTransactionsQueryString(filters: TransactionFilters, page: number): string {
  const params = new URLSearchParams()
  params.set('page', String(page))

  if (filters.dateFrom) params.set('dateFrom', filters.dateFrom)
  if (filters.dateTo) params.set('dateTo', filters.dateTo)
  if (filters.accountId) params.set('accountId', filters.accountId)
  if (filters.categoryId) params.set('categoryId', filters.categoryId)
  if (filters.type) params.set('type', filters.type)
  if (filters.status) params.set('status', filters.status)
  if (filters.amountMin !== undefined) params.set('amountMin', String(filters.amountMin))
  if (filters.amountMax !== undefined) params.set('amountMax', String(filters.amountMax))
  if (filters.search) params.set('search', filters.search)
  if (filters.excludeTransfers) params.set('excludeTransfers', 'true')

  return params.toString()
}

/**
 * True when at least one filter dimension is set to a non-default value --
 * drives the "Clear filters" button's conditional render and the mobile
 * "Filters" button's active-count Badge.
 */
export function isFilterActive(filters: TransactionFilters): boolean {
  return (
    !!filters.dateFrom ||
    !!filters.dateTo ||
    !!filters.accountId ||
    !!filters.categoryId ||
    !!filters.type ||
    !!filters.status ||
    filters.amountMin !== undefined ||
    filters.amountMax !== undefined ||
    !!filters.search ||
    !!filters.excludeTransfers
  )
}

/** Count of active filter dimensions, for the mobile "Filters" button's Badge. */
export function activeFilterCount(filters: TransactionFilters): number {
  return [
    filters.dateFrom,
    filters.dateTo,
    filters.accountId,
    filters.categoryId,
    filters.type,
    filters.status,
    filters.amountMin !== undefined,
    filters.amountMax !== undefined,
    filters.search,
    filters.excludeTransfers,
  ].filter(Boolean).length
}
