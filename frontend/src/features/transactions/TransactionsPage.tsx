import { useEffect, useRef, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { useTranslation } from 'react-i18next'
import { parseAmount } from '@/lib/amount'
import {
  createColumnHelper,
  getCoreRowModel,
  useReactTable,
} from '@tanstack/react-table'
import Select from 'react-select'
import {
  Alert,
  Badge,
  Button,
  Card,
  CardBody,
  Col,
  Dropdown,
  DropdownItem,
  DropdownMenu,
  DropdownToggle,
  Form,
  Nav,
  OverlayTrigger,
  Placeholder,
  Popover,
  Row,
} from 'react-bootstrap'

import { SuggestionsTab } from '@/features/ai/SuggestionsTab'
import Icon from '@/components/Icon'
import { MoneyText } from '@/components/MoneyText'
import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { useAuth } from '@/features/auth/useAuth'
import { downloadExport } from '@/lib/downloadExport'
import { formatAccountOptionLabel, useAccountsQuery } from '@/features/accounts/api'
import { TransactionDetailSheet } from '@/features/transactions/TransactionDetailSheet'
import { TransactionsPager } from '@/features/transactions/TransactionsPager'
import { TransactionsTable } from '@/features/transactions/TransactionsTable'
import {
  useCategoriesQuery,
  useToggleHiddenMutation,
  useToggleTransferMutation,
  useTransactionsQuery,
  type TransactionListItem,
} from '@/features/transactions/api'
import {
  activeFilterCount,
  buildTransactionExportQueryString,
  isFilterActive,
  type TransactionFilters,
} from '@/features/transactions/filters'

const SEARCH_DEBOUNCE_MS = 300
const SKELETON_ROW_COUNT = 8

function formatRowDate(dateString: string): string {
  return new Intl.DateTimeFormat('pt-BR', { day: '2-digit', month: '2-digit', year: 'numeric' }).format(
    new Date(dateString)
  )
}

/**
 * Builds the initial `TransactionFilters` from whichever of
 * categoryId/dateFrom/dateTo/accountId/type/status are present in the URL's
 * search params -- reuses the same field names `buildTransactionsQueryString`
 * already serializes, so a round trip through the URL never loses a filter.
 * Read once on mount (v1's drill-through contract: the URL seeds the initial
 * filter state, the page's own filter UI is the source of truth after that)
 * -- this is what Phase 12's dashboard `?category=<id>` links rely on.
 */
function filtersFromSearchParams(searchParams: URLSearchParams): TransactionFilters {
  const filters: TransactionFilters = {}

  const dateFrom = searchParams.get('dateFrom')
  const dateTo = searchParams.get('dateTo')
  const accountId = searchParams.get('accountId')
  const categoryId = searchParams.get('categoryId')
  const type = searchParams.get('type')
  const status = searchParams.get('status')

  if (dateFrom) filters.dateFrom = dateFrom
  if (dateTo) filters.dateTo = dateTo
  if (accountId) filters.accountId = accountId
  if (categoryId) filters.categoryId = categoryId
  if (type === 'income' || type === 'expense' || type === 'transfer') filters.type = type
  if (status === 'posted' || status === 'pending') filters.status = status
  if (searchParams.get('excludeTransfers') === 'true') filters.excludeTransfers = true

  return filters
}

interface CategoryOption {
  id: string
  name: string
}

interface FilterBarProps {
  filters: TransactionFilters
  onChange: (next: TransactionFilters) => void
  accounts: { id: string; label: string }[]
  categories: CategoryOption[]
}

type SelectOption = { value: string; label: string }

/**
 * The 6 non-text filter dimensions (period/account/category/type/status/amount),
 * rendered as a single-row filter bar. Search is handled separately by the
 * parent (debounced free-text input) since it doesn't fit this row's
 * select/date-input shape.
 */
function FilterBar({ filters, onChange, accounts, categories }: FilterBarProps) {
  const { t } = useTranslation('transactions')
  const [amountMinDraft, setAmountMinDraft] = useState('')
  const [amountMaxDraft, setAmountMaxDraft] = useState('')
  const [amountOpen, setAmountOpen] = useState(false)

  const categoryOptions: SelectOption[] = categories.map((category) => ({
    value: category.id,
    label: category.name,
  }))
  const selectedCategoryOption = categoryOptions.find((option) => option.value === filters.categoryId) ?? null

  function openAmountPopover() {
    setAmountMinDraft(filters.amountMin !== undefined ? String(filters.amountMin) : '')
    setAmountMaxDraft(filters.amountMax !== undefined ? String(filters.amountMax) : '')
    setAmountOpen(true)
  }

  function applyAmount() {
    onChange({
      ...filters,
      amountMin: parseAmount(amountMinDraft) ?? undefined,
      amountMax: parseAmount(amountMaxDraft) ?? undefined,
    })
    setAmountOpen(false)
  }

  const amountPopover = (
    <Popover id="txn-amount-popover">
      <Popover.Body>
        <div className="mb-2">
          <Form.Label htmlFor="txn-amount-min" className="fs-xs text-muted mb-1">
            {t('filters.amountMin')}
          </Form.Label>
          <Form.Control
            id="txn-amount-min"
            type="text"
            inputMode="decimal"
            autoComplete="off"
            value={amountMinDraft}
            onChange={(event) => setAmountMinDraft(event.target.value)}
          />
        </div>
        <div className="mb-3">
          <Form.Label htmlFor="txn-amount-max" className="fs-xs text-muted mb-1">
            {t('filters.amountMax')}
          </Form.Label>
          <Form.Control
            id="txn-amount-max"
            type="text"
            inputMode="decimal"
            autoComplete="off"
            value={amountMaxDraft}
            onChange={(event) => setAmountMaxDraft(event.target.value)}
          />
        </div>
        <Button size="sm" variant="primary" onClick={applyAmount}>
          {t('filters.apply')}
        </Button>
      </Popover.Body>
    </Popover>
  )

  return (
    <Row className="g-2 align-items-end">
      <Col xs={6} sm={4} xl={2}>
        <Form.Label className="fs-xs text-muted mb-1">{t('filters.from')}</Form.Label>
        <Form.Control
          type="date"
          value={filters.dateFrom ?? ''}
          onChange={(event) => onChange({ ...filters, dateFrom: event.target.value || undefined })}
        />
      </Col>
      <Col xs={6} sm={4} xl={2}>
        <Form.Label className="fs-xs text-muted mb-1">{t('filters.to')}</Form.Label>
        <Form.Control
          type="date"
          value={filters.dateTo ?? ''}
          onChange={(event) => onChange({ ...filters, dateTo: event.target.value || undefined })}
        />
      </Col>
      <Col xs={6} sm={4} xl={2}>
        <Form.Label className="fs-xs text-muted mb-1">{t('filters.account')}</Form.Label>
        <Form.Select
          value={filters.accountId ?? ''}
          onChange={(event) => onChange({ ...filters, accountId: event.target.value || undefined })}
        >
          <option value="">{t('filters.allAccounts')}</option>
          {accounts.map((account) => (
            <option key={account.id} value={account.id}>
              {account.label}
            </option>
          ))}
        </Form.Select>
      </Col>
      <Col xs={6} sm={4} xl={2}>
        <Form.Label className="fs-xs text-muted mb-1">{t('filters.category')}</Form.Label>
        <Select<SelectOption>
          className="react-select"
          classNamePrefix="react-select"
          isClearable
          placeholder={t('filters.allCategories')}
          options={categoryOptions}
          value={selectedCategoryOption}
          onChange={(option) => onChange({ ...filters, categoryId: option?.value })}
        />
      </Col>
      <Col xs={6} sm={4} xl={1}>
        <Form.Label className="fs-xs text-muted mb-1">{t('filters.type')}</Form.Label>
        <Form.Select
          value={filters.type ?? ''}
          onChange={(event) => {
            const nextType = (event.target.value || undefined) as TransactionFilters['type']
            onChange({
              ...filters,
              type: nextType,
              excludeTransfers: nextType === 'transfer' ? undefined : filters.excludeTransfers,
            })
          }}
        >
          <option value="">{t('filters.typeAll')}</option>
          <option value="income">{t('filters.typeIncome')}</option>
          <option value="expense">{t('filters.typeExpense')}</option>
          <option value="transfer">{t('filters.typeTransfer')}</option>
        </Form.Select>
      </Col>
      <Col xs={6} sm={4} xl={1}>
        <Form.Label className="fs-xs text-muted mb-1">{t('filters.status')}</Form.Label>
        <Form.Select
          value={filters.status ?? ''}
          onChange={(event) =>
            onChange({
              ...filters,
              status: (event.target.value || undefined) as TransactionFilters['status'],
            })
          }
        >
          <option value="">{t('filters.statusAll')}</option>
          <option value="posted">{t('filters.statusPosted')}</option>
          <option value="pending">{t('filters.statusPending')}</option>
        </Form.Select>
      </Col>
      <Col xs={6} sm={4} xl={2}>
        <Form.Label className="fs-xs text-muted mb-1">{t('filters.amount')}</Form.Label>
        <OverlayTrigger
          trigger="click"
          placement="bottom"
          show={amountOpen}
          onToggle={(next) => (next ? openAmountPopover() : setAmountOpen(false))}
          overlay={amountPopover}
          rootClose
        >
          <Button variant="outline-secondary" className="w-100 text-start">
            {filters.amountMin !== undefined || filters.amountMax !== undefined
              ? t('filters.amountSet')
              : t('filters.amountAny')}
          </Button>
        </OverlayTrigger>
      </Col>
    </Row>
  )
}

/**
 * FE-TXN-01: unified, filterable, paginated transaction feed across every
 * account/card, wired to GET /api/transactions. Row click and the kebab's
 * "View details"/"Recategorize" set `selectedId`/`focusCategory` -- Plan
 * 11-07 (next wave) renders `<TransactionDetailSheet>` against this exact
 * same state; nothing visibly opens until that plan lands.
 */
export function TransactionsPage() {
  const { t } = useTranslation(['transactions', 'common'])
  const { t: tAi } = useTranslation('ai')
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  const { showNotification } = useNotificationContext()
  const [searchParams] = useSearchParams()
  const [activeTab, setActiveTab] = useState<'transactions' | 'suggestions'>('transactions')

  const [filters, setFilters] = useState<TransactionFilters>(() => filtersFromSearchParams(searchParams))
  const [page, setPage] = useState(1)
  // Keyed by page number rather than a flat always-append array: mutations
  // fired from this feed (hide/transfer quick-toggle from the kebab menu)
  // invalidate the `['transactions']` query key, which can re-fetch
  // whichever page is currently active. Keying by page makes a re-fetch of
  // the currently-displayed page a safe in-place replace instead of a
  // duplicate-inserting append, and avoids an empty-table flash while the
  // active page's data is refetching.
  const [pagesById, setPagesById] = useState<Record<number, TransactionListItem[]>>({})
  const [searchInput, setSearchInput] = useState('')
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [focusCategory, setFocusCategory] = useState(false)
  const [exportPending, setExportPending] = useState<'csv' | 'xlsx' | null>(null)

  const filtersRef = useRef(filters)
  filtersRef.current = filters

  function updateFilters(next: TransactionFilters) {
    setFilters(next)
    setPage(1)
    setPagesById({})
  }

  /** Clears every filter and the search box, so no stale search text stays on screen. */
  function clearFilters() {
    setSearchInput('')
    updateFilters({})
  }

  function handleViewDetails(id: string) {
    setFocusCategory(false)
    setSelectedId(id)
  }

  function handleRecategorize(id: string) {
    setFocusCategory(true)
    setSelectedId(id)
  }

  useEffect(() => {
    const handle = setTimeout(() => {
      const trimmed = searchInput.trim()
      const current = filtersRef.current
      if ((current.search ?? '') === trimmed) return
      updateFilters({ ...current, search: trimmed || undefined })
    }, SEARCH_DEBOUNCE_MS)
    return () => clearTimeout(handle)
  }, [searchInput])

  const accountsQuery = useAccountsQuery(token)
  const categoriesQuery = useCategoriesQuery(token)
  const transactionsQuery = useTransactionsQuery(token, filters, page)

  const toggleHiddenMutation = useToggleHiddenMutation()
  const toggleTransferMutation = useToggleTransferMutation()

  useEffect(() => {
    const data = transactionsQuery.data
    if (!data) return
    setPagesById((prev) => ({ ...prev, [data.page]: data.items }))
  }, [transactionsQuery.data])

  async function handleToggleHidden(item: TransactionListItem) {
    try {
      await toggleHiddenMutation.mutateAsync({ accessToken: token, id: item.id, isHidden: !item.isHidden })
    } catch {
      showNotification({ message: t('errors.update'), variant: 'danger' })
    }
  }

  async function handleToggleTransfer(item: TransactionListItem) {
    try {
      await toggleTransferMutation.mutateAsync({ accessToken: token, id: item.id, isTransfer: !item.isTransfer })
    } catch {
      showNotification({ message: t('errors.update'), variant: 'danger' })
    }
  }

  async function handleExport(format: 'csv' | 'xlsx') {
    setExportPending(format)
    try {
      const effectiveFilters: TransactionFilters = {
        ...filters,
        search: searchInput.trim() || undefined,
      }
      const queryString = buildTransactionExportQueryString(effectiveFilters, format)
      await downloadExport(`/api/export/transactions?${queryString}`, token, `transactions.${format}`)
    } catch {
      showNotification({ message: t('errors.export'), variant: 'danger' })
    } finally {
      setExportPending(null)
    }
  }

  const accounts = (accountsQuery.data ?? []).map((account) => ({
    id: account.id,
    label: formatAccountOptionLabel(account),
  }))
  const accountLabelById = new Map(accounts.map((a) => [a.id, a.label]))
  const categories: CategoryOption[] = (categoriesQuery.data ?? []).map((category) => ({
    id: category.id,
    name: category.name,
  }))

  const items = pagesById[page] ?? []
  const totalCount = transactionsQuery.data?.totalCount ?? 0
  const pageSize = transactionsQuery.data?.pageSize ?? 50
  const filtersActive = isFilterActive(filters)
  const filterCount = activeFilterCount(filters)
  const isInitialLoading = transactionsQuery.isLoading && items.length === 0
  const pageCount = Math.max(1, Math.ceil(totalCount / pageSize))
  const start = totalCount === 0 ? 0 : (page - 1) * pageSize + 1
  const end = Math.min(page * pageSize, totalCount)

  const columnHelper = createColumnHelper<TransactionListItem>()
  const columns = [
    columnHelper.accessor('date', {
      header: t('table.date'),
      cell: ({ row }) => <span className="text-muted">{formatRowDate(row.original.date)}</span>,
    }),
    columnHelper.display({
      id: 'description',
      header: t('table.description'),
      cell: ({ row }) => {
        const item = row.original
        return (
          <div className="d-flex align-items-center gap-2">
            <span>{item.merchantName ?? item.description}</span>
            {item.status === 'Pending' && (
              <Badge bg="secondary" className="fw-normal">
                {t('table.pendingBadge')}
              </Badge>
            )}
            {item.isTransfer && (
              <Badge bg="secondary" className="fw-normal">
                {t('table.transferBadge')}
              </Badge>
            )}
          </div>
        )
      },
    }),
    columnHelper.accessor('accountName', {
      header: t('table.account'),
      cell: ({ row, getValue }) => (
        <span className="text-muted">{accountLabelById.get(row.original.accountId) ?? getValue()}</span>
      ),
    }),
    columnHelper.display({
      id: 'category',
      header: t('table.category'),
      cell: ({ row }) => {
        const item = row.original
        return (
          <div className="d-flex align-items-center gap-1">
            <span className="text-muted">{item.categoryName ?? '—'}</span>
            {item.categoryId && (
              <span className="fs-xs text-muted">({t(`categorySource.${item.categorySource}`)})</span>
            )}
          </div>
        )
      },
    }),
    columnHelper.display({
      id: 'amount',
      header: () => <span className="d-block text-end">{t('table.amount')}</span>,
      cell: ({ row }) => {
        const item = row.original
        const variant = item.isTransfer ? 'transfer' : item.type === 'Credit' ? 'income' : 'expense'
        return (
          <div className="text-end">
            <MoneyText amount={item.amount} variant={variant} />
          </div>
        )
      },
    }),
    columnHelper.display({
      id: 'actions',
      header: '',
      cell: ({ row }) => {
        const item = row.original
        return (
          <Dropdown onClick={(event) => event.stopPropagation()}>
            <DropdownToggle as="div" className="text-muted drop-arrow-none card-drop p-0 cursor-pointer">
              <Icon icon="ellipsis-vertical" className="fs-lg" />
            </DropdownToggle>
            <DropdownMenu align="end">
              <DropdownItem onClick={() => handleViewDetails(item.id)}>{t('rowActions.viewDetails')}</DropdownItem>
              <DropdownItem onClick={() => handleRecategorize(item.id)}>{t('rowActions.recategorize')}</DropdownItem>
              <DropdownItem onClick={() => void handleToggleHidden(item)}>
                {item.isHidden ? t('rowActions.show') : t('rowActions.hide')}
              </DropdownItem>
              <DropdownItem onClick={() => void handleToggleTransfer(item)}>
                {item.isTransfer ? t('rowActions.unmarkTransfer') : t('rowActions.markTransfer')}
              </DropdownItem>
            </DropdownMenu>
          </Dropdown>
        )
      },
      enableSorting: false,
    }),
  ]

  const table = useReactTable({
    data: items,
    columns,
    getCoreRowModel: getCoreRowModel(),
    manualPagination: true,
    pageCount,
    // Rows arrive one server page at a time in server order; there is no client sort.
    enableSorting: false,
  })

  return (
    <>
      <Nav
        variant="tabs"
        className="mb-3"
        activeKey={activeTab}
        onSelect={(key) => setActiveTab(key === 'suggestions' ? 'suggestions' : 'transactions')}
      >
        <Nav.Item>
          <Nav.Link eventKey="transactions">{tAi('suggestions.transactionsTab')}</Nav.Link>
        </Nav.Item>
        <Nav.Item>
          <Nav.Link eventKey="suggestions">{tAi('suggestions.tabLabel')}</Nav.Link>
        </Nav.Item>
      </Nav>

      {activeTab === 'suggestions' ? (
        <SuggestionsTab />
      ) : (
        <>
      <Card className="mb-3">
        <CardBody>
          <Row className="g-2 align-items-end mb-2">
            <Col xs={12} md>
              <FilterBar filters={filters} onChange={updateFilters} accounts={accounts} categories={categories} />
            </Col>
          </Row>
          <Row className="g-2 align-items-center justify-content-between">
            <Col xs={12} sm="auto" className="d-flex flex-wrap align-items-center gap-3">
              <div className="position-relative" style={{ maxWidth: 320 }}>
                <Icon
                  icon="search"
                  className="position-absolute top-50 translate-middle-y text-muted"
                  style={{ left: 12 }}
                />
                <Form.Control
                  value={searchInput}
                  onChange={(event) => setSearchInput(event.target.value)}
                  placeholder={t('filters.searchPlaceholder')}
                  style={{ paddingLeft: 36 }}
                />
              </div>
              <Form.Check
                type="switch"
                id="txn-hide-transfers-switch"
                label={t('filters.hideTransfers')}
                checked={!!filters.excludeTransfers}
                onChange={(event) =>
                  updateFilters({
                    ...filters,
                    excludeTransfers: event.target.checked || undefined,
                    type: event.target.checked && filters.type === 'transfer' ? undefined : filters.type,
                  })
                }
              />
            </Col>
            <Col xs="auto" className="d-flex align-items-center gap-2">
              {filtersActive && (
                <Button variant="link" className="text-decoration-none" onClick={clearFilters}>
                  {t('filters.clearFilters')} {filterCount > 0 && <Badge bg="secondary">{filterCount}</Badge>}
                </Button>
              )}
              <Button
                variant="outline-secondary"
                size="sm"
                disabled={exportPending !== null}
                onClick={() => void handleExport('csv')}
              >
                {exportPending === 'csv' ? t('export.exporting') : t('export.csv')}
              </Button>
              <Button
                variant="outline-secondary"
                size="sm"
                disabled={exportPending !== null}
                onClick={() => void handleExport('xlsx')}
              >
                {exportPending === 'xlsx' ? t('export.exporting') : t('export.excel')}
              </Button>
            </Col>
          </Row>
        </CardBody>
      </Card>

      {transactionsQuery.isError && (
        <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3">
          <span>{t('errors.load')}</span>
          <Button variant="outline-danger" size="sm" onClick={() => void transactionsQuery.refetch()}>
            {t('common:actions.retry')}
          </Button>
        </Alert>
      )}

      {!transactionsQuery.isError && (
        <Card>
          <CardBody className="p-0">
            {isInitialLoading ? (
              <div className="table-responsive">
                <table className="table table-custom table-centered table-select w-100 mb-0">
                  <thead className="bg-light align-middle bg-opacity-25 thead-sm">
                    <tr className="text-uppercase fs-xxs">
                      <th>{t('table.date')}</th>
                      <th>{t('table.description')}</th>
                      <th>{t('table.account')}</th>
                      <th>{t('table.category')}</th>
                      <th className="text-end">{t('table.amount')}</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {Array.from({ length: SKELETON_ROW_COUNT }).map((_, index) => (
                      <tr key={index}>
                        <td>
                          <Placeholder as="span" animation="glow">
                            <Placeholder xs={6} />
                          </Placeholder>
                        </td>
                        <td>
                          <Placeholder as="span" animation="glow">
                            <Placeholder xs={8} />
                          </Placeholder>
                        </td>
                        <td>
                          <Placeholder as="span" animation="glow">
                            <Placeholder xs={7} />
                          </Placeholder>
                        </td>
                        <td>
                          <Placeholder as="span" animation="glow">
                            <Placeholder xs={5} />
                          </Placeholder>
                        </td>
                        <td className="text-end">
                          <Placeholder as="span" animation="glow">
                            <Placeholder xs={5} />
                          </Placeholder>
                        </td>
                        <td />
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : items.length === 0 ? (
              <div className="text-center py-5">
                {filtersActive ? (
                  <>
                    <Icon icon="filter-x" className="fs-1 text-muted mb-3" />
                    <h5>{t('emptyState.filteredTitle')}</h5>
                    <p className="text-muted mb-3">{t('emptyState.filteredBody')}</p>
                    <Button variant="outline-secondary" onClick={clearFilters}>
                      {t('emptyState.clearFilters')}
                    </Button>
                  </>
                ) : (
                  <>
                    <Icon icon="arrow-left-right" className="fs-1 text-muted mb-3" />
                    <h5>{t('emptyState.noneTitle')}</h5>
                    <p className="text-muted mb-3">{t('emptyState.noneBody')}</p>
                    <Link to="/settings/connections" className="btn btn-primary">
                      {t('emptyState.connect')}
                    </Link>
                  </>
                )}
              </div>
            ) : (
              <TransactionsTable
                table={table}
                emptyMessage={t('table.empty')}
                onRowClick={(item) => handleViewDetails(item.id)}
              />
            )}
          </CardBody>
          {!isInitialLoading && items.length > 0 && (
            <div className="border-top p-3">
              <TransactionsPager
                totalItems={totalCount}
                start={start}
                end={end}
                pageIndex={page - 1}
                pageCount={pageCount}
                onPageChange={(index) => setPage(index + 1)}
              />
            </div>
          )}
        </Card>
      )}

      <TransactionDetailSheet
        transactionId={selectedId}
        categories={categoriesQuery.data ?? []}
        focusCategory={focusCategory}
        onClose={() => {
          setSelectedId(null)
          setFocusCategory(false)
        }}
      />
        </>
      )}
    </>
  )
}
