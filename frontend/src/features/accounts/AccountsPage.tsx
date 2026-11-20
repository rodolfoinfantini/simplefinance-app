import { Alert, Button, Card, CardBody, Col, Form, Placeholder, ProgressBar, Row } from 'react-bootstrap'
import { Link } from 'react-router'
import { useTranslation } from 'react-i18next'

import Icon from '@/components/Icon'
import { MoneyText } from '@/components/MoneyText'
import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { useAuth } from '@/features/auth/useAuth'

import { useAccountsQuery, useToggleAccountHideMutation, type AccountResponse } from './api'

function formatShortDate(dateString: string | null): string {
  if (!dateString) return '—'
  return new Intl.DateTimeFormat('pt-BR', { day: '2-digit', month: '2-digit', timeZone: 'UTC' }).format(new Date(dateString))
}

function TileSkeleton() {
  return (
    <Col>
      <Card>
        <CardBody>
          <div className="d-flex flex-wrap align-items-start justify-content-between gap-2 mb-3">
            <div>
              <Placeholder as="p" animation="glow" className="mb-1">
                <Placeholder xs={7} />
              </Placeholder>
              <Placeholder as="p" animation="glow" className="mb-0">
                <Placeholder xs={5} />
              </Placeholder>
            </div>
            <Placeholder as="div" animation="glow">
              <Placeholder xs={6} style={{ width: 36, height: 20 }} />
            </Placeholder>
          </div>
          <Placeholder as="p" animation="glow" className="mb-0">
            <Placeholder xs={4} style={{ height: 28 }} />
          </Placeholder>
        </CardBody>
      </Card>
    </Col>
  )
}

function AccountTile({ account, accessToken }: { account: AccountResponse; accessToken: string }) {
  const toggleHideMutation = useToggleAccountHideMutation()
  const { showNotification } = useNotificationContext()
  const { t } = useTranslation('accounts')

  async function handleHideToggle(checked: boolean) {
    try {
      await toggleHideMutation.mutateAsync({ accessToken, id: account.id, isHidden: checked })
      showNotification({
        message: checked ? t('hideToggle.hiddenToast') : t('hideToggle.shownToast'),
        variant: 'success',
      })
    } catch {
      showNotification({ message: t('updateError'), variant: 'danger' })
    }
  }

  return (
    <Col>
      <Card className="h-100">
        <CardBody>
          <div className="d-flex flex-wrap align-items-start justify-content-between gap-2 mb-3">
            <div>
              <p className="fw-semibold mb-1">{account.institutionName ?? account.name}</p>
              <p className="text-muted mb-0">{account.numberMasked ?? account.name}</p>
            </div>
            <div className="d-flex flex-column align-items-end gap-1">
              <Form.Check
                type="switch"
                checked={account.isHidden}
                onChange={(e) => void handleHideToggle(e.target.checked)}
                disabled={toggleHideMutation.isPending}
                aria-label={t('hideToggle.ariaLabel')}
              />
              <span className="text-muted small">{t('hideToggle.label')}</span>
            </div>
          </div>
          <MoneyText amount={account.balance} variant="neutral" showSign={false} className="fs-3 fw-semibold" />
          {account.automaticallyInvestedBalance != null && account.automaticallyInvestedBalance > 0 && (
            <p className="text-muted small mt-1 mb-0">
              {t('bankAccount.reserved')}{' '}
              <MoneyText amount={account.automaticallyInvestedBalance} variant="neutral" showSign={false} className="text-body fw-medium" />
            </p>
          )}
        </CardBody>
      </Card>
    </Col>
  )
}

function CreditCardTile({ account }: { account: AccountResponse }) {
  const { t } = useTranslation('accounts')
  const creditCard = account.creditCard

  if (!creditCard) return null

  const usedAmount =
    creditCard.creditLimit != null && creditCard.availableCredit != null
      ? creditCard.creditLimit - creditCard.availableCredit
      : null
  const progressNow =
    usedAmount != null && creditCard.creditLimit
      ? Math.min(100, Math.max(0, (usedAmount / creditCard.creditLimit) * 100))
      : null

  return (
    <Col>
      <Card className="h-100">
        <CardBody>
          <div className="mb-3">
            <p className="fw-semibold mb-1">{account.institutionName ?? account.name}</p>
            <p className="text-muted mb-0">{account.numberMasked ?? account.name}</p>
          </div>

          <div className="mb-3">
            <p className="text-muted small mb-1">{t('creditCard.currentInvoice')}</p>
            <MoneyText
              amount={creditCard.lastStatementBalance ?? 0}
              variant="expense"
              showSign={false}
              className="fs-3 fw-semibold"
            />
            {creditCard.nextStatementBalance != null && creditCard.nextStatementBalance > 0 && (
              <p className="text-muted small mt-1 mb-0">
                {t('creditCard.nextInvoice')}{' '}
                <MoneyText amount={creditCard.nextStatementBalance} variant="neutral" showSign={false} className="text-body fw-medium" />
              </p>
            )}
          </div>

          {creditCard.creditLimit != null && usedAmount != null && progressNow != null && (
            <div className="mb-3">
              <ProgressBar now={progressNow} className="mb-1" style={{ height: 6 }} />
              <p className="text-muted small mb-0">
                <MoneyText amount={usedAmount} variant="neutral" showSign={false} className="text-body" />
                {' '}
                {t('creditCard.usedOf')}
                {' '}
                <MoneyText amount={creditCard.creditLimit} variant="neutral" showSign={false} className="text-body" />
              </p>
            </div>
          )}

          <div className="d-flex align-items-center justify-content-between gap-3 text-muted small mb-2">
            {creditCard.availableCredit != null && (
              <span>
                {t('creditCard.available')}{' '}
                <MoneyText amount={creditCard.availableCredit} variant="neutral" showSign={false} className="text-body" />
              </span>
            )}
            {creditCard.creditLimit != null && (
              <span>
                {t('creditCard.limit')}{' '}
                <MoneyText amount={creditCard.creditLimit} variant="neutral" showSign={false} className="text-body" />
              </span>
            )}
          </div>

          <div className="d-flex align-items-center justify-content-between gap-3 text-muted small">
            <span>{t('creditCard.closes', { date: formatShortDate(creditCard.balanceCloseDate) })}</span>
            <span>{t('creditCard.due', { date: formatShortDate(creditCard.balanceDueDate) })}</span>
          </div>
        </CardBody>
      </Card>
    </Col>
  )
}

function EmptyState() {
  const { t } = useTranslation('accounts')
  return (
    <Card>
      <CardBody className="text-center py-5">
        <Icon icon="landmark" className="fs-1 text-muted mb-3" />
        <h5>{t('empty.title')}</h5>
        <p className="text-muted mb-3">{t('empty.description')}</p>
        <Link to="/settings/connections" className="btn btn-primary">
          {t('empty.connect')}
        </Link>
      </CardBody>
    </Card>
  )
}

/**
 * ACCT-01/02/03: renders every bank account and credit card imported from
 * Pluggy (GET /api/accounts), with an account-level "Hide from totals"
 * toggle wired to PATCH /api/accounts/{id}/hide. Credit-card tiles
 * additionally show the current invoice (statement balance), limit-usage
 * progress bar, and closing/due dates — never a hide toggle (ACCT-03 only
 * names accounts, not cards, matching v1's CreditCardTile).
 */
export function AccountsPage() {
  const { t } = useTranslation(['accounts', 'common'])
  const { accessToken } = useAuth()
  const accountsQuery = useAccountsQuery(accessToken ?? '')

  const accounts = accountsQuery.data ?? []
  const bankAccounts = accounts.filter((account) => account.type === 'Bank')
  const creditCards = accounts.filter((account) => account.type === 'Credit' && account.creditCard)

  return (
    <>
      {accountsQuery.isError && (
        <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3">
          <span>{t('loadError')}</span>
          <Button variant="outline-danger" size="sm" onClick={() => void accountsQuery.refetch()}>
            {t('common:actions.retry')}
          </Button>
        </Alert>
      )}

      {accountsQuery.isPending && (
        <section>
          <h4 className="mb-3">{t('sections.bankAccounts')}</h4>
          <Row className="row-cols-1 row-cols-md-2 row-cols-xl-3 g-3">
            <TileSkeleton />
            <TileSkeleton />
            <TileSkeleton />
          </Row>
        </section>
      )}

      {!accountsQuery.isPending && !accountsQuery.isError && accounts.length === 0 && <EmptyState />}

      {!accountsQuery.isPending && !accountsQuery.isError && accounts.length > 0 && (
        <>
          {bankAccounts.length > 0 && (
            <section className="mb-4">
              <h4 className="mb-3">{t('sections.bankAccounts')}</h4>
              <Row className="row-cols-1 row-cols-md-2 row-cols-xl-3 g-3">
                {bankAccounts.map((account) => (
                  <AccountTile key={account.id} account={account} accessToken={accessToken ?? ''} />
                ))}
              </Row>
            </section>
          )}

          {creditCards.length > 0 && (
            <section>
              <h4 className="mb-3">{t('sections.creditCards')}</h4>
              <Row className="row-cols-1 row-cols-md-2 row-cols-xl-3 g-3">
                {creditCards.map((account) => (
                  <CreditCardTile key={account.id} account={account} />
                ))}
              </Row>
            </section>
          )}
        </>
      )}
    </>
  )
}
